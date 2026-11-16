"""One lazily authorized, private hardware-control pipe per running GUI.

The root helper receives only allowlisted controls and enumerated values. A
failed exchange is never retried or replayed. Closing stdin ends the helper;
the application never installs a root daemon or a reusable system permission.
"""

import atexit
import json
import os
from pathlib import Path
import selectors
import subprocess
import threading
import time

from .backend import Backend
from .cli import CONTROL_NAMES, HELPER


AUTH_TIMEOUT = 120.0
COMMAND_TIMEOUT = 10.0
MAX_MESSAGE = 8192


class _ControlFailure(RuntimeError):
    """A valid helper error leaves its authenticated pipe usable."""


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate protocol key")
        result[key] = value
    return result


class ControlSession:
    def __init__(self):
        self._operation_lock = threading.Lock()
        self._state_lock = threading.Lock()
        self._closed = threading.Event()
        self._process = None
        self._authorized = False
        self._sequence = 0
        self._buffer = bytearray()
        atexit.register(self.close)

    @property
    def authorized(self):
        with self._state_lock:
            return bool(not self._closed.is_set() and self._authorized
                        and self._process is not None and self._process.poll() is None)

    def _check_process(self, process):
        with self._state_lock:
            if self._closed.is_set():
                raise RuntimeError("设备控制会话已关闭")
            if process is not self._process:
                raise RuntimeError("设备控制会话已结束")

    @staticmethod
    def _release_process(process):
        # Popen uses unbuffered pipes, so closing them cannot wait on a buffered
        # write or on the helper. In particular, GUI shutdown never waits for
        # authentication or firmware operations to finish.
        for stream in (process.stdin, process.stdout):
            if stream is not None:
                try:
                    stream.close()
                except OSError:
                    pass

        def reap():
            try:
                process.wait(timeout=1)
                return
            except subprocess.TimeoutExpired:
                pass
            # Cancellation during authentication may leave pkexec waiting.
            # An elevated helper normally exits on EOF; signalling it may be
            # refused once credentials changed, so EOF is the primary cleanup.
            for stop in (process.terminate, process.kill):
                try:
                    stop()
                    process.wait(timeout=1)
                    return
                except (OSError, subprocess.TimeoutExpired):
                    continue

        threading.Thread(target=reap, daemon=True).start()

    def _discard(self, process):
        with self._state_lock:
            if self._process is process:
                self._process = None
                self._authorized = False
        self._release_process(process)

    def close(self):
        with self._state_lock:
            if self._closed.is_set():
                return
            self._closed.set()
            process = self._process
            self._process = None
            self._authorized = False
        if process is not None:
            self._release_process(process)

    def _read_message(self, process, timeout):
        deadline = time.monotonic() + timeout
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout.fileno(), selectors.EVENT_READ)
            while True:
                self._check_process(process)
                newline = self._buffer.find(b"\n")
                if newline >= 0:
                    line = bytes(self._buffer[:newline])
                    del self._buffer[:newline + 1]
                    try:
                        message = json.loads(line.decode("utf-8"), object_pairs_hook=_unique_object)
                    except (ValueError, UnicodeError) as exc:
                        raise RuntimeError("设备设置助手返回无效响应") from exc
                    if not isinstance(message, dict):
                        raise RuntimeError("设备设置助手返回无效响应")
                    return message
                if len(self._buffer) >= MAX_MESSAGE:
                    raise RuntimeError("设备设置助手响应过长")
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("等待设备设置助手超时，操作未确认")
                if not selector.select(min(remaining, 0.05)):
                    continue
                self._check_process(process)
                try:
                    data = os.read(process.stdout.fileno(), min(4096, MAX_MESSAGE - len(self._buffer)))
                except BlockingIOError:
                    continue
                if not data:
                    raise RuntimeError("管理员授权未完成或设备设置助手已退出")
                self._buffer.extend(data)

    def _start(self):
        if not Path(HELPER).is_file() or not Path("/usr/bin/pkexec").is_file():
            raise RuntimeError("请先安装完整的 .deb 软件包及 pkexec")
        process = subprocess.Popen(
            ["/usr/bin/pkexec", "--disable-internal-agent", HELPER, "--session"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            bufsize=0, close_fds=True, shell=False)
        try:
            os.set_blocking(process.stdin.fileno(), False)
            os.set_blocking(process.stdout.fileno(), False)
            with self._state_lock:
                if self._closed.is_set():
                    raise RuntimeError("设备控制会话已关闭")
                self._process = process
                self._authorized = False
            self._buffer.clear()
            ready = self._read_message(process, AUTH_TIMEOUT)
            if (set(ready) != {"type", "protocol"} or ready["type"] != "ready"
                    or type(ready["protocol"]) is not int or ready["protocol"] != 1):
                raise RuntimeError("设备设置助手协议不兼容")
            with self._state_lock:
                if self._closed.is_set() or self._process is not process:
                    raise RuntimeError("设备控制会话已关闭")
                self._authorized = True
            return process
        except Exception:
            self._discard(process)
            raise

    def _write_request(self, process, request):
        payload = (json.dumps(request, ensure_ascii=True, separators=(",", ":")) + "\n").encode("ascii")
        if len(payload) > 2048:
            raise ValueError("设备设置请求过长")
        # A nonblocking write smaller than PIPE_BUF is atomic. The shared lock
        # gives close() a precise boundary: no new requests after it returns.
        with self._state_lock:
            if self._closed.is_set() or self._process is not process:
                raise RuntimeError("设备控制会话已关闭")
            if os.write(process.stdin.fileno(), payload) != len(payload):
                raise RuntimeError("设备设置请求发送不完整，操作未确认")

    def set_control(self, name, value):
        # Reject malformed/unavailable requests before any administrator prompt.
        if not isinstance(name, str) or name not in CONTROL_NAMES:
            raise ValueError("不支持的设备设置")
        control = Backend().controls().get(name, {})
        if not control.get("available"):
            raise ValueError(control.get("reason") or "内核未提供此设备设置")
        if not isinstance(value, str) or value not in control.get("choices", []):
            raise ValueError("无效的设备设置值")
        with self._operation_lock:
            with self._state_lock:
                if self._closed.is_set():
                    raise RuntimeError("设备控制会话已关闭")
                process = self._process
            process = process if process is not None else self._start()
            try:
                self._sequence += 1
                request_id = self._sequence
                self._write_request(process, {"id": request_id, "control": name, "value": value})
                response = self._read_message(process, COMMAND_TIMEOUT)
                success = response.get("success")
                expected = {"id", "success", "result" if success is True else "error"}
                if (set(response) != expected or type(response.get("id")) is not int
                        or response["id"] != request_id or type(success) is not bool):
                    raise RuntimeError("设备设置助手返回无效响应")
                if not success:
                    if not isinstance(response["error"], str):
                        raise RuntimeError("设备设置助手返回无效响应")
                    raise _ControlFailure(response["error"] or "设备设置失败")
                if response["result"] != value:
                    raise RuntimeError("设备设置助手未返回请求的内核状态")
                self._check_process(process)
                return response["result"]
            except _ControlFailure:
                raise
            except Exception:
                self._discard(process)
                raise
