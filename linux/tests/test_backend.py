import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from fancontroller.backend import Backend


class BackendTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.backend = Backend(self.root)

    def write(self, name, value):
        path = self.root / name.lstrip("/")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(str(value) + "\n", encoding="utf-8")
        return path

    def battery(self, **values):
        values.setdefault("type", "Battery")
        for name, value in values.items():
            self.write(f"/sys/class/power_supply/BAT1/{name}", value)

    def controls(self):
        self.write("/sys/firmware/acpi/platform_profile", "balanced")
        self.write("/sys/firmware/acpi/platform_profile_choices", "low-power balanced performance")
        self.write("/sys/bus/platform/drivers/ideapad_acpi/VPC2004:00/conservation_mode", "0")
        self.write("/sys/bus/platform/drivers/ideapad_acpi/VPC2004:00/fn_lock", "1")
        self.write("/sys/bus/platform/drivers/ideapad_acpi/VPC2004:00/usb_charging", "0")
        self.write("/sys/class/leds/platform::kbd_backlight/brightness", "0")
        self.write("/sys/class/leds/platform::kbd_backlight/max_brightness", "2")

    def test_missing_hardware_stays_unknown_and_json_serializable(self):
        snapshot = self.backend.snapshot()
        self.assertEqual(snapshot["batteries"], [])
        self.assertIsNone(snapshot["metrics"]["cpu_percent"])
        self.assertIsNone(snapshot["metrics"]["memory_percent"])
        self.assertFalse(any(control["available"] for control in snapshot["controls"].values()))
        self.assertEqual(snapshot["inventory"], [])
        self.assertTrue(any(check["level"] == "unknown" for check in snapshot["health"]))
        json.dumps(snapshot, allow_nan=False)

    def test_cpu_interval_excludes_guest_and_detects_reset(self):
        self.write("/proc/stat", "cpu 100 0 100 800 0 0 0 0 100 0")
        self.assertIsNone(self.backend.metrics()["cpu_percent"])
        self.write("/proc/stat", "cpu 150 0 100 850 0 0 0 0 150 0")
        self.assertEqual(self.backend.metrics()["cpu_percent"], 50)
        self.write("/proc/stat", "cpu 1 0 0 10 0 0 0 0 0 0")
        self.assertIsNone(self.backend.metrics()["cpu_percent"])

    def test_memory_and_sensor_units_ignore_invalid_sensors(self):
        self.write("/proc/meminfo", "MemTotal: 8388608 kB\nMemAvailable: 2097152 kB")
        self.write("/proc/cpuinfo", "cpu MHz : 2000.0\ncpu MHz : 3000.0\ncpu MHz : nan")
        self.write("/sys/class/hwmon/hwmon0/name", "k10temp")
        self.write("/sys/class/hwmon/hwmon0/temp1_input", "45625")
        self.write("/sys/class/hwmon/hwmon0/temp1_label", "Tctl")
        self.write("/sys/class/hwmon/hwmon0/temp2_input", "2147483647")
        self.write("/sys/class/hwmon/hwmon0/fan1_input", "2000")
        self.write("/sys/class/drm/card0/device/gpu_busy_percent", "14")
        metrics = self.backend.metrics()
        self.assertEqual(metrics["memory_percent"], 75)
        self.assertEqual(metrics["memory_total_gib"], 8)
        self.assertEqual(metrics["memory_used_gib"], 6)
        self.assertEqual(metrics["cpu_mhz"], 2500)
        self.assertEqual(metrics["temperatures"], [{"name": "k10temp / Tctl", "celsius": 45.6}])
        self.assertEqual(metrics["fans"][0]["rpm"], 2000)
        self.assertEqual(metrics["gpu_percent"], 14)

    def test_gpu_missing_zero_full_and_invalid_readings(self):
        self.assertIsNone(self.backend.metrics()["gpu_percent"])
        for value in (0, 100):
            with self.subTest(value=value):
                self.write("/sys/class/drm/card0/device/gpu_busy_percent", value)
                self.assertEqual(self.backend.metrics()["gpu_percent"], value)
        for value in (-1, 101, "nan", "inf", "invalid", ""):
            with self.subTest(value=value):
                self.write("/sys/class/drm/card0/device/gpu_busy_percent", value)
                self.assertIsNone(self.backend.metrics()["gpu_percent"])

    def test_gpu_skips_unavailable_cards_and_connector_metadata(self):
        self.write("/sys/class/drm/card0/device/gpu_busy_percent", "unavailable")
        self.write("/sys/class/drm/card0-eDP-1/status", "connected")
        self.write("/sys/class/drm/card0-eDP-1/modes", "1920x1080")
        self.write("/sys/class/drm/card1/device/gpu_busy_percent", 32)
        self.assertEqual(self.backend.metrics()["gpu_percent"], 32)

    def test_battery_energy_units_and_online_state(self):
        self.battery(energy_now=20000000, energy_full=40000000, energy_full_design=50000000,
                     power_now=10000000, capacity=50, cycle_count=66, status="Discharging")
        self.write("/sys/class/power_supply/AC/type", "Mains")
        self.write("/sys/class/power_supply/AC/online", "0")
        battery = self.backend.batteries()[0]
        self.assertEqual(battery["energy_now_wh"], 20)
        self.assertEqual(battery["energy_full_wh"], 40)
        self.assertEqual(battery["energy_design_wh"], 50)
        self.assertEqual(battery["health_percent"], 80)
        self.assertEqual(battery["power_w"], 10)
        self.assertEqual(battery["cycles"], 66)
        self.assertFalse(battery["online"])

    def test_battery_charge_conversion_uses_nominal_voltage(self):
        self.battery(charge_now=1000000, charge_full=2000000, charge_full_design=2500000,
                     voltage_min_design=10000000, voltage_now=12000000,
                     current_now=-1000000, status="Discharging")
        battery = self.backend.batteries()[0]
        self.assertEqual(battery["energy_now_wh"], 10)
        self.assertEqual(battery["energy_full_wh"], 20)
        self.assertEqual(battery["percent"], 50)
        self.assertEqual(battery["health_percent"], 80)
        self.assertEqual(battery["power_w"], 12)
        self.assertIsNone(battery["online"])

    def test_peripheral_or_absent_batteries_are_excluded(self):
        self.battery(scope="Device", capacity=50)
        self.assertEqual(self.backend.batteries(), [])
        self.battery(scope="System", present=0)
        self.assertEqual(self.backend.batteries(), [])

    def test_unknown_battery_values_stay_none(self):
        self.battery(energy_full="nan", energy_full_design=0, capacity="invalid", cycle_count=-1)
        battery = self.backend.batteries()[0]
        self.assertIsNone(battery["health_percent"])
        self.assertIsNone(battery["percent"])
        self.assertIsNone(battery["cycles"])
        self.assertIsNone(battery["power_w"])

    def test_zero_full_capacity_is_unknown_but_zero_power_and_cycles_are_valid(self):
        self.battery(energy_now=0, energy_full=0, energy_full_design=50000000,
                     power_now=0, cycle_count=0, capacity=0)
        battery = self.backend.batteries()[0]
        self.assertIsNone(battery["health_percent"])
        self.assertEqual(battery["percent"], 0)
        self.assertEqual(battery["power_w"], 0)
        self.assertEqual(battery["cycles"], 0)
        health = self.backend.health(batteries=[battery])
        self.assertTrue(any(item["level"] == "unknown" and "电池" in item["name"] for item in health))

    def test_allowlisted_control_write_and_readback(self):
        self.controls()
        self.assertEqual(self.backend.set_control("profile", "performance"), "performance")
        self.assertEqual(self.backend.set_control("conservation", "1"), "1")
        self.assertEqual(self.backend.set_control("keyboard_backlight", "2"), "2")
        self.assertEqual(self.backend.controls()["profile"]["value"], "performance")
        self.assertFalse(self.backend.controls()["fan_dust"]["available"])

    def test_invalid_names_values_and_fan_are_rejected_before_write(self):
        self.controls()
        for name, value in (("../../etc/passwd", "x"), ("profile", "balanced\nperformance"),
                            ("profile", "turbo"), ("conservation", "true"),
                            ("conservation", 1), ("keyboard_backlight", "3"), ("fan_dust", "2")):
            with self.subTest(name=name, value=value), self.assertRaises(ValueError):
                self.backend.set_control(name, value)
        self.assertEqual(self.backend.controls()["profile"]["value"], "balanced")
        self.assertEqual(self.backend.controls()["conservation"]["value"], "0")

    def test_unknown_hardware_state_disables_controls(self):
        self.controls()
        self.write("/sys/bus/platform/drivers/ideapad_acpi/VPC2004:00/fn_lock", "133")
        self.assertFalse(self.backend.controls()["fn_lock"]["available"])
        with self.assertRaises(ValueError):
            self.backend.set_control("fn_lock", "1")

    def test_sysfs_symlink_cannot_target_another_filesystem_tree(self):
        self.controls()
        victim = self.write("/etc/victim", "balanced")
        profile = self.root / "sys/firmware/acpi/platform_profile"
        profile.unlink()
        profile.symlink_to(victim)
        with self.assertRaises(ValueError):
            self.backend.set_control("profile", "performance")
        self.assertEqual(victim.read_text(), "balanced\n")

    def test_fixture_symlink_cannot_escape_fixture_root(self):
        self.controls()
        profile = self.root / "sys/firmware/acpi/platform_profile"
        profile.unlink()
        profile.symlink_to("/sys/firmware/acpi/platform_profile")
        with self.assertRaises(ValueError):
            self.backend.set_control("profile", "performance")

    def test_sysfs_class_symlinks_within_sys_are_supported(self):
        target = self.write("/sys/devices/platform/lenovo/leds/platform::fnlock/brightness", "1")
        link = self.root / "sys/class/leds/platform::fnlock"
        link.parent.mkdir(parents=True)
        link.symlink_to(target.parent)
        self.assertEqual(self.backend.set_control("fn_lock", "0"), "0")
        self.assertEqual(target.read_text(), "0\n")

    def test_inventory_detects_disk_and_not_partitions(self):
        self.write("/sys/class/block/nvme0n1/size", 1000000000)
        self.write("/sys/class/block/nvme0n1/device/model", "Example SSD")
        self.write("/sys/class/block/nvme0n1p1/partition", 1)
        self.write("/sys/class/block/nvme0n1p1/size", 1000000)
        self.write("/sys/bus/usb/devices/1-1/idVendor", "1234")
        self.write("/sys/bus/usb/devices/1-1/idProduct", "5678")
        self.write("/sys/bus/usb/devices/1-1/product", "USB camera")
        devices = self.backend.inventory()
        storage = [device for device in devices if device["category"] == "存储"]
        self.assertEqual(len(storage), 1)
        self.assertIn("512.0 GB", storage[0]["details"])
        self.assertTrue(any(device["name"] == "USB camera" for device in devices))

    def test_readback_mismatch_is_an_error(self):
        self.controls()
        real_read = self.backend._read
        reads = 0

        def read(path, default=None):
            nonlocal reads
            if path.name == "conservation_mode":
                reads += 1
                # Initial discovery says disabled; final read still disabled.
                return "0"
            return real_read(path, default)

        with patch.object(self.backend, "_read", side_effect=read):
            with self.assertRaisesRegex(OSError, "readback mismatch"):
                self.backend.set_control("conservation", "1")
        self.assertEqual(reads, 2)

    def test_led_fnlock_fallback(self):
        self.write("/sys/class/leds/platform::fnlock/brightness", "1")
        self.assertTrue(self.backend.controls()["fn_lock"]["available"])
        self.assertEqual(self.backend.set_control("fn_lock", "0"), "0")

    def test_health_does_not_assume_smart_success(self):
        self.battery(energy_full=30000000, energy_full_design=50000000)
        health = self.backend.health()
        self.assertTrue(any(item["level"] == "warning" and "电池" in item["name"] for item in health))
        self.assertTrue(any(item["level"] == "unknown" and "SMART" in item["name"] for item in health))


if __name__ == "__main__":
    unittest.main()
