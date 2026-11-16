"""User-owned configuration, battery history, and conservative scene switching."""

import csv
import io
import json
import os
from pathlib import Path
import time

from . import APP_NAME


PROFILES = ("low-power", "balanced", "performance")


def config_home():
    return Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "fancontroller"


def data_home():
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "fancontroller"


class Settings:
    def __init__(self, directory=None):
        self.directory = Path(directory) if directory else config_home()
        self.path = self.directory / "settings.json"
        self.values = {"automation": False, "ac_profile": "balanced", "battery_profile": "low-power",
                       "applications": [], "record_battery": False}
        self.error = ""
        try:
            if self.path.exists():
                saved = json.loads(self.path.read_text())
                if not isinstance(saved, dict):
                    raise ValueError("配置必须是 JSON 对象")
                for key in ("automation", "record_battery"):
                    if type(saved.get(key)) is bool:
                        self.values[key] = saved[key]
                for key in ("ac_profile", "battery_profile"):
                    if saved.get(key) in PROFILES:
                        self.values[key] = saved[key]
                rules = saved.get("applications", [])
                if isinstance(rules, list):
                    self.values["applications"] = [r for r in rules if isinstance(r, dict)
                        and isinstance(r.get("process"), str) and r["process"]
                        and r.get("profile") in PROFILES]
        except (OSError, ValueError) as exc:
            self.error = f"配置读取失败，使用默认配置：{exc}"

    def save(self):
        self.directory.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_suffix(".tmp")
        temporary.write_text(json.dumps(self.values, ensure_ascii=False, indent=2) + "\n")
        temporary.replace(self.path)


class Automation:
    """Debounce for 3s, cooldown for 30s, manual changes take priority for 10min.

    Only a completed successful readback commits ownership. Polling cannot open
    repeated authentication prompts after a cancelled/failed attempt.
    """
    def __init__(self):
        self.candidate = None
        self.since = 0
        self.last_attempt = float("-inf")
        self.observed = None
        self.pause_until = 0
        self.failed = False
        self.status = "自动场景未启用"

    def pause(self, now=None):
        now = time.monotonic() if now is None else now
        self.pause_until = now + 600
        self.candidate = None
        self.status = "手动优先，自动切换暂停 10 分钟"

    def resume(self):
        self.pause_until = 0
        self.failed = False
        self.candidate = None
        self.last_attempt = float("-inf")

    def evaluate(self, settings, current, online, processes, now=None):
        now = time.monotonic() if now is None else now
        if not settings["automation"]:
            self.observed = current
            self.candidate = None
            self.status = "自动场景未启用"
            return None
        if current not in PROFILES:
            self.candidate = None
            self.status = "等待有效的散热模式"
            return None
        if self.observed is not None and current != self.observed:
            self.pause(now)
        self.observed = current
        if now < self.pause_until:
            self.status = "手动优先，自动切换暂停中"
            return None
        if self.failed:
            self.status = "上次切换未完成，请点击「恢复自动切换」重试"
            return None
        if settings["applications"] and processes is None:
            self.candidate = None
            self.status = "进程列表不可读，保留当前模式"
            return None
        rule = next((r for r in settings["applications"] if r["process"] in (processes or set())), None)
        if rule:
            desired = rule["profile"]
            reason = "应用：" + rule["process"]
        elif online is not None:
            desired = settings["ac_profile"] if online else settings["battery_profile"]
            reason = "接通电源" if online else "使用电池"
        else:
            self.candidate = None
            self.status = "电源状态未知，保留当前模式"
            return None
        if current == desired:
            self.candidate = None
            self.status = reason + " · " + current
            return None
        if desired != self.candidate:
            self.candidate, self.since = desired, now
        self.status = "等待切换：" + reason
        if now - self.since < 3 or now - self.last_attempt < 30:
            return None
        self.last_attempt = now
        return desired

    def complete(self, value, success):
        self.failed = not success
        if success:
            self.observed = value
            self.candidate = None
            self.status = "已自动切换：" + value


def process_names(proc=Path("/proc")):
    try:
        names = set()
        for path in proc.iterdir():
            if path.name.isdigit():
                try:
                    # comm is the Linux task name (limited by the kernel).
                    names.add((path / "comm").read_text().strip())
                except FileNotFoundError:
                    continue
                except PermissionError:
                    return None
        return names
    except OSError:
        return None


HISTORY_FIELDS = ("time", "battery", "status", "percent", "energy_now_wh", "energy_full_wh",
                  "energy_design_wh", "health_percent", "power_w", "cycles")


class BatteryHistory:
    def __init__(self, directory=None):
        self.directory = Path(directory) if directory else data_home() / "history"
        self.last = float("-inf")

    def add(self, batteries, now=None):
        now = time.time() if now is None else now
        if now - self.last < 30 or not batteries:
            return
        self.directory.mkdir(parents=True, exist_ok=True)
        day = time.strftime("%Y-%m-%d", time.localtime(now))
        path = self.directory / ("battery-" + day + ".csv")
        exists = path.exists() and path.stat().st_size > 0
        with path.open("a", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=HISTORY_FIELDS)
            if not exists:
                writer.writeheader()
            for battery in batteries:
                row = {key: battery.get(key) for key in HISTORY_FIELDS}
                row.update(time=time.strftime("%Y-%m-%dT%H:%M:%S%z", time.localtime(now)),
                           battery=battery.get("name"))
                writer.writerow(row)
        self.last = now
        for old in self.directory.glob("battery-????-??-??.csv"):
            if old.stat().st_mtime < now - 30 * 86400:
                old.unlink()

    def export(self, destination):
        destination = Path(destination)
        files = sorted(self.directory.glob("battery-????-??-??.csv"))
        if destination.resolve().is_relative_to(self.directory.resolve()) or any(
                destination.exists() and f.samefile(destination) for f in files):
            raise ValueError("请选择历史记录目录以外的导出文件")
        # Build before opening destination, including when a source is unreadable.
        buffer = io.StringIO(newline="")
        writer = csv.DictWriter(buffer, fieldnames=HISTORY_FIELDS)
        writer.writeheader()
        for source in files:
            with source.open(newline="", encoding="utf-8") as stream:
                for row in csv.DictReader(stream):
                    writer.writerow({k: row.get(k, "") for k in HISTORY_FIELDS})
        destination.write_text(buffer.getvalue(), encoding="utf-8-sig")


def autostart_path():
    return config_home().parent / "autostart/fancontroller.desktop"


def set_autostart(enabled):
    path = autostart_path()
    if enabled:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"[Desktop Entry]\nType=Application\nName={APP_NAME}\n"
                        "Exec=/usr/bin/fancontroller\nIcon=fancontroller\nTerminal=false\n")
    else:
        path.unlink(missing_ok=True)
