"""Exercise the real pipe client with unprivileged fixture subprocesses only."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch

from fancontroller import control_session as client


REAL_POPEN = subprocess.Popen
READY = 'import json,sys,time\nprint(json.dumps({"type":"ready","protocol":1}), flush=True)\n'
ECHO = READY + ('for line in sys.stdin:\n'
               ' request=json.loads(line)\n'
               ' print(json.dumps({"id":request["id"],"success":True,"result":request["value"]}),flush=True)\n')


class ControlSessionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.commands = []
        self.children = []
        self.script = ECHO
        backend = Mock()
        backend.controls.return_value = {
            "profile": {"available": True, "choices": ["low-power", "balanced", "performance"]},
            "conservation": {"available": False, "reason": "unsupported"}}
        self.backend = backend
        self.backend_patch = patch.object(client, "Backend", return_value=backend)
        self.backend_patch.start()
        self.addCleanup(self.backend_patch.stop)
        self.paths_patch = patch.object(client.Path, "is_file", return_value=True)
        self.paths_patch.start()
        self.addCleanup(self.paths_patch.stop)
        self.popen_patch = patch.object(client.subprocess, "Popen", side_effect=self.start_fixture)
        self.popen_patch.start()
        self.addCleanup(self.popen_patch.stop)
        self.session = client.ControlSession()
        self.addCleanup(self.cleanup_processes)

    def start_fixture(self, command, **kwargs):
        self.commands.append((command, kwargs))
        process = REAL_POPEN([sys.executable, "-u", "-c", self.script], **kwargs)
        self.children.append(process)
        return process

    def cleanup_processes(self):
        self.session.close()
        for process in self.children:
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=1)

    def test_constructor_is_lazy_and_invalid_requests_never_authorize(self):
        self.assertFalse(self.session.authorized)
        self.backend.controls.assert_not_called()
        for name, value in (("/tmp/file", "1"), ([], "1"), ("profile", []),
                            ("profile", "balanced\nperformance"), ("conservation", "1")):
            with self.assertRaises(ValueError):
                self.session.set_control(name, value)
        self.assertEqual(self.commands, [])

    def test_reuses_one_fixed_authorized_process_and_closes_its_pipes(self):
        self.assertEqual(self.session.set_control("profile", "balanced"), "balanced")
        self.assertTrue(self.session.authorized)
        self.assertEqual(self.session.set_control("profile", "performance"), "performance")
        self.assertEqual(len(self.commands), 1)
        command, kwargs = self.commands[0]
        self.assertEqual(command, ["/usr/bin/pkexec", "--disable-internal-agent", client.HELPER, "--session"])
        self.assertIs(kwargs["shell"], False)
        self.assertIs(kwargs["close_fds"], True)
        self.assertEqual(kwargs["stderr"], subprocess.DEVNULL)
        self.assertEqual(kwargs["bufsize"], 0)
        self.session.close()
        self.assertFalse(self.session.authorized)
        self.assertTrue(self.children[0].stdin.closed)
        self.children[0].wait(timeout=1)
        self.assertIsNotNone(self.children[0].returncode)

    def test_concurrent_calls_are_serialized_and_not_reauthorized(self):
        barrier = threading.Barrier(3)
        results, errors = [], []
        def request(value):
            barrier.wait()
            try:
                results.append(self.session.set_control("profile", value))
            except Exception as error:
                errors.append(error)
        workers = [threading.Thread(target=request, args=(value,)) for value in ("balanced", "performance")]
        for worker in workers:
            worker.start()
        barrier.wait()
        for worker in workers:
            worker.join(timeout=2)
            self.assertFalse(worker.is_alive())
        self.assertEqual(errors, [])
        self.assertEqual(sorted(results), ["balanced", "performance"])
        self.assertEqual(len(self.commands), 1)

    def test_cancelled_authorization_is_not_retried(self):
        self.script = "import sys\nsys.exit(126)\n"
        with self.assertRaisesRegex(RuntimeError, "授权"):
            self.session.set_control("profile", "balanced")
        self.assertEqual(len(self.commands), 1)
        self.assertFalse(self.session.authorized)

    def test_authentication_timeout_discards_process_without_replay(self):
        self.script = "import time\ntime.sleep(2)\n"
        with patch.object(client, "AUTH_TIMEOUT", 0.05):
            with self.assertRaises(TimeoutError):
                self.session.set_control("profile", "balanced")
        self.assertEqual(len(self.commands), 1)
        self.assertFalse(self.session.authorized)

    def test_command_timeout_never_replays_request(self):
        log = self.directory / "requests.txt"
        self.script = READY + ('for line in sys.stdin:\n'
            f' open({str(log)!r},"a").write(line)\n'
            ' time.sleep(2)\n')
        with patch.object(client, "COMMAND_TIMEOUT", 0.05):
            with self.assertRaises(TimeoutError):
                self.session.set_control("profile", "balanced")
        self.assertEqual(len(self.commands), 1)
        self.assertEqual(len(log.read_text().splitlines()), 1)
        self.assertFalse(self.session.authorized)

    def test_invalid_handshakes_fail_before_first_request(self):
        for ready in ('{"type":"ready","protocol":true}', '{"type":"ready","protocol":2}',
                      '{"type":"ready","protocol":1,"extra":1}', '[]', 'not json'):
            with self.subTest(ready=ready):
                self.script = f'import sys\nprint({ready!r},flush=True)\n'
                with self.assertRaises(RuntimeError):
                    self.session.set_control("profile", "balanced")
                self.assertFalse(self.session.authorized)

    def test_malformed_or_mismatched_responses_fail_closed(self):
        responses = [
            '{"id":999,"success":true,"result":"balanced"}',
            '{"id":true,"success":true,"result":"balanced"}',
            '{"id":1,"success":"true","result":"balanced"}',
            '{"id":1,"success":true,"result":"performance"}',
            '{"id":1,"success":true,"result":"balanced","extra":1}',
            '{"id":1,"id":1,"success":true,"result":"balanced"}',
            '{"id":1,"success":false,"error":{}}',
            '[]', 'not json', 'x' * (client.MAX_MESSAGE + 1)]
        for response in responses:
            with self.subTest(response=response[:100]):
                session = client.ControlSession()
                self.script = READY + f'input()\nprint({response!r},flush=True)\n'
                try:
                    with self.assertRaises(RuntimeError):
                        session.set_control("profile", "balanced")
                    self.assertFalse(session.authorized)
                finally:
                    session.close()

    def test_eof_during_command_is_not_retried(self):
        self.script = READY + "input()\n"
        with self.assertRaises(RuntimeError):
            self.session.set_control("profile", "balanced")
        self.assertFalse(self.session.authorized)
        self.assertEqual(len(self.commands), 1)

    def test_valid_hardware_error_keeps_authorized_session_for_explicit_request(self):
        self.script = READY + ('for line in sys.stdin:\n'
            ' request=json.loads(line)\n'
            ' response={"id":request["id"],"success":request["value"]!="performance"}\n'
            ' response.update({"error":"firmware rejected"} if not response["success"] else {"result":request["value"]})\n'
            ' print(json.dumps(response),flush=True)\n')
        with self.assertRaisesRegex(RuntimeError, "firmware rejected"):
            self.session.set_control("profile", "performance")
        self.assertTrue(self.session.authorized)
        self.assertEqual(self.session.set_control("profile", "balanced"), "balanced")
        self.assertEqual(len(self.commands), 1)

    def test_close_during_authorization_returns_promptly_and_sends_no_command(self):
        log = self.directory / "requests.txt"
        self.script = ('import sys,time,json\ntime.sleep(0.3)\n'
            'print(json.dumps({"type":"ready","protocol":1}),flush=True)\n'
            f'open({str(log)!r},"w").write(sys.stdin.read())\n')
        errors = []
        worker = threading.Thread(target=lambda: self.capture_error(errors))
        worker.start()
        deadline = time.monotonic() + 1
        while self.session._process is None and time.monotonic() < deadline:
            time.sleep(0.001)
        self.assertIsNotNone(self.session._process)
        started = time.monotonic()
        self.session.close()
        self.assertLess(time.monotonic() - started, 0.1)
        worker.join(timeout=1)
        self.assertFalse(worker.is_alive())
        self.assertTrue(errors)
        self.children[0].wait(timeout=1)
        self.assertFalse(log.exists() and log.read_text())
        self.assertFalse(self.session.authorized)

    def capture_error(self, errors):
        try:
            self.session.set_control("profile", "balanced")
        except Exception as error:
            errors.append(error)

    def test_close_after_handshake_before_write_prevents_command(self):
        entered, release = threading.Event(), threading.Event()
        original = self.session._write_request
        def delayed_write(*args):
            entered.set()
            release.wait(timeout=1)
            return original(*args)
        errors = []
        with patch.object(self.session, "_write_request", side_effect=delayed_write):
            worker = threading.Thread(target=lambda: self.capture_error(errors))
            worker.start()
            self.assertTrue(entered.wait(timeout=1))
            self.assertTrue(self.session.authorized)
            self.session.close()
            release.set()
            worker.join(timeout=1)
            self.assertFalse(worker.is_alive())
        self.assertTrue(errors)
        self.assertFalse(self.session.authorized)
        with self.assertRaisesRegex(RuntimeError, "关闭"):
            self.session.set_control("profile", "balanced")
        self.assertEqual(len(self.commands), 1)


if __name__ == "__main__":
    unittest.main()
