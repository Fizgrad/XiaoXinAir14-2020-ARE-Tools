"""Linux hardware access using public, kernel-owned sysfs interfaces.

All sampling is read-only. The only write API takes a control name and an
enumerated value, never a path. An alternative filesystem root makes parsing
and error behavior testable without accessing real firmware.
"""

from __future__ import annotations

import math
import os
from pathlib import Path
import platform
import re
import shutil
import time


class Backend:
    def __init__(self, root: Path | str = "/"):
        self.root = Path(root).resolve()
        self._previous_cpu: tuple[int, int] | None = None
        self._inventory_cache: list[dict] | None = None
        self._inventory_at = 0.0

    def _path(self, path: str) -> Path:
        return self.root / path.lstrip("/")

    def _safe(self, path: Path, *, sysfs: bool = False) -> Path:
        resolved = path.resolve()
        boundary = self._path("/sys") if sysfs else self.root
        if not resolved.is_relative_to(boundary):
            raise ValueError("Hardware path escapes the trusted filesystem")
        return resolved

    def _read(self, path: Path, default: str | None = None) -> str | None:
        try:
            return self._safe(path).read_text(encoding="utf-8", errors="replace").strip()
        except (OSError, ValueError, RuntimeError):
            return default

    def _number(self, path: Path, scale: float = 1) -> float | None:
        try:
            value = float(self._read(path) or "") / scale
            return value if math.isfinite(value) else None
        except (ValueError, TypeError):
            return None

    @staticmethod
    def _glob(path: Path, pattern: str) -> list[Path]:
        try:
            return sorted(path.glob(pattern))
        except OSError:
            return []

    def identity(self) -> dict:
        dmi = self._path("/sys/class/dmi/id")
        os_values = {}
        for line in (self._read(self._path("/etc/os-release"), "") or "").splitlines():
            if "=" in line:
                key, value = line.split("=", 1)
                os_values[key] = value.strip('"')
        cpu = None
        for line in (self._read(self._path("/proc/cpuinfo"), "") or "").splitlines():
            if line.partition(":")[0].strip() == "model name":
                cpu = line.partition(":")[2].strip()
                break
        return {
            "manufacturer": self._read(dmi / "sys_vendor", "Unknown"),
            "model": self._read(dmi / "product_name", "Unknown"),
            "bios": self._read(dmi / "bios_version", "Unknown"),
            "os": os_values.get("PRETTY_NAME", "Linux"),
            "kernel": self._read(self._path("/proc/sys/kernel/osrelease"), platform.release()),
            "cpu": cpu or "Unknown",
        }

    def metrics(self) -> dict:
        cpu_percent = None
        lines = (self._read(self._path("/proc/stat"), "") or "").splitlines()
        if lines and lines[0].startswith("cpu "):
            try:
                # guest/guest_nice are already included in user/nice.
                values = [int(value) for value in lines[0].split()[1:9]]
                if len(values) >= 4 and all(value >= 0 for value in values):
                    total = sum(values)
                    idle = values[3] + (values[4] if len(values) > 4 else 0)
                    if self._previous_cpu:
                        previous_total, previous_idle = self._previous_cpu
                        delta = total - previous_total
                        if delta > 0 and 0 <= idle - previous_idle <= delta:
                            cpu_percent = round(100 * (1 - (idle - previous_idle) / delta), 1)
                    self._previous_cpu = total, idle
            except ValueError:
                pass

        frequencies = []
        for line in (self._read(self._path("/proc/cpuinfo"), "") or "").splitlines():
            if line.partition(":")[0].strip() == "cpu MHz":
                try:
                    value = float(line.partition(":")[2])
                    if math.isfinite(value) and value >= 0:
                        frequencies.append(value)
                except ValueError:
                    pass

        memory = {}
        for line in (self._read(self._path("/proc/meminfo"), "") or "").splitlines():
            key, _, tail = line.partition(":")
            try:
                memory[key] = max(0, int(tail.split()[0]))
            except (ValueError, IndexError):
                continue
        total_kib = memory.get("MemTotal", 0)
        available_kib = memory.get("MemAvailable")
        used_kib = None if not total_kib or available_kib is None else max(0, total_kib - available_kib)

        temperatures, fans = [], []
        for device in self._glob(self._path("/sys/class/hwmon"), "hwmon*"):
            driver = self._read(device / "name", device.name)
            for sensor in self._glob(device, "temp*_input"):
                value = self._number(sensor, 1000)
                if value is not None and -30 <= value <= 150:
                    label = self._read(sensor.with_name(sensor.name.replace("_input", "_label")), sensor.stem)
                    temperatures.append({"name": f"{driver} / {label}", "celsius": round(value, 1)})
            for sensor in self._glob(device, "fan*_input"):
                value = self._number(sensor)
                if value is not None and 0 <= value <= 100000:
                    label = self._read(sensor.with_name(sensor.name.replace("_input", "_label")), sensor.stem)
                    fans.append({"name": f"{driver} / {label}", "rpm": int(value)})

        gpu_percent = None
        for card in self._glob(self._path("/sys/class/drm"), "card[0-9]*"):
            value = self._number(card / "device/gpu_busy_percent")
            if value is not None and 0 <= value <= 100:
                gpu_percent = value
                break
        return {
            "cpu_percent": cpu_percent,
            "cpu_mhz": round(sum(frequencies) / len(frequencies), 1) if frequencies else None,
            "memory_percent": round(100 * used_kib / total_kib, 1) if used_kib is not None else None,
            "memory_used_gib": round(used_kib / 1024**2, 2) if used_kib is not None else None,
            "memory_total_gib": round(total_kib / 1024**2, 2) if total_kib else None,
            "temperatures": temperatures,
            "fans": fans,
            "gpu_percent": gpu_percent,
        }

    def batteries(self) -> list[dict]:
        supplies = self._glob(self._path("/sys/class/power_supply"), "*")
        online_values = []
        for supply in supplies:
            if self._read(supply / "type") in ("Mains", "USB", "USB_C", "USB_PD"):
                value = self._read(supply / "online")
                if value in ("0", "1"):
                    online_values.append(value == "1")
        online = any(online_values) if online_values else None
        result = []
        for supply in supplies:
            if self._read(supply / "type") != "Battery" or self._read(supply / "present") == "0":
                continue
            # Peripheral batteries (mouse, keyboard) are not laptop capacity.
            if self._read(supply / "scope") == "Device":
                continue
            voltage_now = self._number(supply / "voltage_now", 1e6)
            voltage_design = self._number(supply / "voltage_min_design", 1e6)
            energy = {}
            for suffix in ("now", "full", "full_design"):
                value = self._number(supply / f"energy_{suffix}", 1e6)
                if value is None:
                    charge = self._number(supply / f"charge_{suffix}", 1e6)
                    # A common nominal voltage keeps health/capacity comparable.
                    voltage = voltage_design or voltage_now
                    value = charge * voltage if charge is not None and voltage else None
                energy[suffix] = value if value is not None and value >= 0 else None
            percent = self._number(supply / "capacity")
            if percent is None and energy["now"] is not None and energy["full"]:
                percent = energy["now"] * 100 / energy["full"]
            if percent is not None:
                percent = min(100, max(0, percent))
            power = self._number(supply / "power_now", 1e6)
            if power is None:
                current = self._number(supply / "current_now", 1e6)
                power = current * voltage_now if current is not None and voltage_now is not None else None
            health = None
            # A reported zero full capacity is usually unavailable/uninitialized
            # firmware data; do not turn it into a diagnosis of 0% health.
            if energy["full"] and energy["full_design"]:
                health = round(100 * energy["full"] / energy["full_design"], 1)
            cycles = self._number(supply / "cycle_count")
            result.append({
                "name": supply.name,
                "status": self._read(supply / "status", "Unknown"),
                "percent": round(percent, 1) if percent is not None else None,
                "energy_now_wh": energy["now"],
                "energy_full_wh": energy["full"],
                "energy_design_wh": energy["full_design"],
                "health_percent": health,
                # Magnitude in W; status determines charging vs discharging.
                "power_w": abs(power) if power is not None else None,
                "cycles": int(cycles) if cycles is not None and cycles >= 0 else None,
                "online": online,
            })
        return result

    def _control(self, path: Path | None, choices: list[str], reason: str = "") -> dict:
        value = self._read(path) if path else None
        available = bool(path is not None and value is not None and value in choices)
        if not available and not reason:
            reason = "此设备不支持" if value is None else f"状态未知：{value}"
        return {"available": available, "value": value, "choices": choices,
                "path": str(path.relative_to(self.root)) if path else None,
                "reason": reason if not available else ""}

    def controls(self) -> dict:
        profile = self._path("/sys/firmware/acpi/platform_profile")
        choices = (self._read(profile.with_name("platform_profile_choices"), "") or "").split()
        # Reject malformed/non-enumerated tokens even on a fixture filesystem.
        choices = list(dict.fromkeys(value for value in choices if re.fullmatch(r"[a-z][a-z-]{0,31}", value)))
        result = {"profile": self._control(profile, choices)}
        devices = self._glob(self._path("/sys/bus/platform/drivers/ideapad_acpi"), "VPC2004:*")
        device = devices[0] if devices else None
        for name, attribute in (("conservation", "conservation_mode"), ("usb_charging", "usb_charging"), ("fn_lock", "fn_lock")):
            path = device / attribute if device else None
            if name == "fn_lock" and (path is None or self._read(path) is None):
                path = self._path("/sys/class/leds/platform::fnlock/brightness")
            result[name] = self._control(path, ["0", "1"])
        backlight = self._path("/sys/class/leds/platform::kbd_backlight/brightness")
        maximum = self._number(backlight.with_name("max_brightness"))
        levels = [str(value) for value in range(int(maximum) + 1)] if maximum is not None and maximum.is_integer() and 0 < maximum <= 255 else []
        result["keyboard_backlight"] = self._control(backlight, levels)
        # fan_mode=133 on the target firmware is outside Linux's documented
        # 0/1/2/4 ABI. Do not interpret arbitrary EC flags as fan commands.
        result["fan_dust"] = {"available": False, "value": None, "choices": [], "path": None,
                              "reason": "暂不支持"}
        return result

    def set_control(self, name: str, value: str) -> str:
        """Write one enumerated, discovered sysfs attribute and verify readback.

        This method does not grant permissions. Installed GUI calls a separately
        packaged, polkit-authorized helper; CLI may be invoked by an admin.
        """
        controls = self.controls()
        if name not in controls or not controls[name]["available"]:
            raise ValueError(f"Control unavailable: {name}")
        control = controls[name]
        if not isinstance(value, str) or value not in control["choices"]:
            raise ValueError(f"Invalid value for {name}: {value!r}")
        path = self._safe(self.root / control["path"], sysfs=True)
        # Resolve once, then refuse a final-component symlink introduced later.
        descriptor = os.open(path, os.O_WRONLY | os.O_TRUNC | os.O_NOFOLLOW)
        try:
            payload = (value + "\n").encode("ascii")
            if os.write(descriptor, payload) != len(payload):
                raise OSError("Incomplete hardware write; state not confirmed")
        finally:
            os.close(descriptor)
        actual = self._read(path)
        if actual != value:
            raise OSError(f"Hardware readback mismatch: requested {value!r}, received {actual!r}")
        return actual

    def inventory(self) -> list[dict]:
        now = time.monotonic()
        if self._inventory_cache is not None and now - self._inventory_at < 30:
            return self._inventory_cache
        result = []
        for device in self._glob(self._path("/sys/bus/pci/devices"), "*"):
            vendor = self._read(device / "vendor", "?")
            product = self._read(device / "device", "?")
            device_class = self._read(device / "class", "?")
            try:
                driver = (device / "driver").resolve().name if (device / "driver").exists() else "未绑定驱动"
            except (OSError, RuntimeError):
                driver = "Unknown"
            result.append({"category": "PCI", "name": device.name,
                           "details": f"Vendor {vendor}, Device {product}, Class {device_class}, {driver}"})
        for device in self._glob(self._path("/sys/bus/usb/devices"), "*"):
            vendor, product = self._read(device / "idVendor"), self._read(device / "idProduct")
            if vendor is None or product is None:
                continue
            label = self._read(device / "product", device.name)
            manufacturer = self._read(device / "manufacturer", "")
            result.append({"category": "USB", "name": label,
                           "details": f"{device.name} · {vendor}:{product} · {manufacturer}"})
        for device in self._glob(self._path("/sys/class/net"), "*"):
            address = self._read(device / "address", "?")
            state = self._read(device / "operstate", "Unknown")
            result.append({"category": "网络", "name": device.name, "details": f"{address} · {state}"})
        for device in self._glob(self._path("/sys/class/block"), "*"):
            if self._read(device / "partition") is not None or device.name.startswith(("loop", "ram")):
                continue
            sectors = self._number(device / "size")
            size = f"{sectors * 512 / 1e9:.1f} GB" if sectors is not None else "容量未知"
            model = self._read(device / "device/model", "")
            result.append({"category": "存储", "name": device.name, "details": f"{model} · {size}"})
        for connector in self._glob(self._path("/sys/class/drm"), "card*-*"):
            if self._read(connector / "status") != "connected":
                continue
            modes = self._read(connector / "modes", "") or ""
            result.append({"category": "显示器", "name": connector.name, "details": ", ".join(modes.splitlines()[:6])})
        for card in self._glob(self._path("/sys/class/sound"), "card[0-9]*"):
            result.append({"category": "音频", "name": card.name, "details": self._read(card / "id", "Unknown")})
        for adapter in self._glob(self._path("/sys/class/bluetooth"), "hci*"):
            result.append({"category": "蓝牙", "name": adapter.name, "details": self._read(adapter / "address", "已检测到蓝牙适配器")})
        self._inventory_cache, self._inventory_at = result, now
        return result

    def health(self, metrics: dict | None = None, batteries: list[dict] | None = None) -> list[dict]:
        metrics = self.metrics() if metrics is None else metrics
        batteries = self.batteries() if batteries is None else batteries
        result = []
        if batteries:
            for battery in batteries:
                health = battery["health_percent"]
                result.append({"level": "unknown" if health is None else "warning" if health < 80 else "ok",
                               "name": f"{battery['name']} 电池容量",
                               "detail": "暂无容量数据" if health is None else f"满充容量为设计容量的 {health:.1f}%"})
        else:
            result.append({"level": "unknown", "name": "电池", "detail": "暂无电池数据"})
        if metrics["temperatures"]:
            hottest = max(metrics["temperatures"], key=lambda sensor: sensor["celsius"])
            result.append({"level": "warning" if hottest["celsius"] >= 90 else "ok", "name": "当前温度",
                           "detail": f"最高 {hottest['celsius']:.1f} °C · {hottest['name']}"})
        else:
            result.append({"level": "unknown", "name": "温度", "detail": "暂无温度数据"})
        try:
            usage = shutil.disk_usage(self.root)
            available = usage.free / usage.total * 100
            result.append({"level": "warning" if available < 10 else "ok", "name": "根文件系统空间",
                           "detail": f"剩余 {usage.free / 1024**3:.1f} GiB ({available:.1f}%)"})
        except OSError:
            result.append({"level": "unknown", "name": "根文件系统空间", "detail": "无法读取文件系统容量"})
        result.append({"level": "unknown", "name": "磁盘 SMART / NVMe 健康",
                       "detail": "暂无检测数据"})
        return result

    def snapshot(self) -> dict:
        metrics, batteries = self.metrics(), self.batteries()
        return {"identity": self.identity(), "metrics": metrics, "batteries": batteries,
                "controls": self.controls(), "inventory": self.inventory(), "health": self.health(metrics, batteries)}
