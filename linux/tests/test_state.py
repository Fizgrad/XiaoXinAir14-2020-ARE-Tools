import csv
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from fancontroller.state import Automation, BatteryHistory, Settings, process_names


class StateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.settings = dict(automation=True, ac_profile="performance", battery_profile="low-power", applications=[])

    def test_corrupt_settings_use_defaults_without_writing(self):
        for content in ("not json", "[]", "null"):
            (self.root / "settings.json").write_text(content)
            settings = Settings(self.root)
            self.assertFalse(settings.values["automation"])
            self.assertFalse(settings.values["record_battery"])
            self.assertTrue(settings.error)
            self.assertEqual(content, settings.path.read_text())

    def test_settings_filter_invalid_values_and_round_trip(self):
        (self.root / "settings.json").write_text(json.dumps(dict(
            automation="yes", record_battery=True, ac_profile="turbo",
            applications=[None, {}, {"process": "", "profile": "balanced"},
                          {"process": "firefox", "profile": "balanced"}])))
        settings = Settings(self.root)
        self.assertFalse(settings.values["automation"])
        self.assertTrue(settings.values["record_battery"])
        self.assertEqual(settings.values["ac_profile"], "balanced")
        self.assertEqual(settings.values["applications"], [{"process": "firefox", "profile": "balanced"}])
        settings.save()
        self.assertEqual(Settings(self.root).values, settings.values)
        self.assertFalse(settings.path.with_suffix(".tmp").exists())

    def test_obsolete_monitor_view_is_ignored_without_changing_other_settings(self):
        expected = dict(self.settings, record_battery=True,
                        applications=[{"process": "firefox", "profile": "balanced"}])
        for view in ("bars", "history"):
            with self.subTest(view=view):
                path = self.root / "settings.json"
                content = json.dumps(dict(expected, monitor_view=view))
                path.write_text(content)
                settings = Settings(self.root)
                self.assertEqual(settings.error, "")
                self.assertEqual(settings.values, expected)
                self.assertEqual(path.read_text(), content)
                settings.save()
                self.assertEqual(json.loads(path.read_text()), expected)
                self.assertEqual(Settings(self.root).values, expected)

    def test_automation_debounce_and_cooldown(self):
        auto = Automation()
        self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=0))
        self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=2.9))
        self.assertEqual(auto.evaluate(self.settings, "balanced", True, set(), now=3), "performance")
        auto.complete("performance", True)
        self.assertIsNone(auto.evaluate(self.settings, "performance", False, set(), now=4))
        self.assertIsNone(auto.evaluate(self.settings, "performance", False, set(), now=7))
        self.assertEqual(auto.evaluate(self.settings, "performance", False, set(), now=33), "low-power")

    def test_cancelled_authorization_requires_explicit_resume(self):
        auto = Automation()
        auto.evaluate(self.settings, "balanced", True, set(), now=0)
        self.assertEqual(auto.evaluate(self.settings, "balanced", True, set(), now=3), "performance")
        auto.complete("authorization cancelled", False)
        for now in (33, 100, 1000):
            self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=now))
        auto.resume()
        self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=1001))
        self.assertEqual(auto.evaluate(self.settings, "balanced", True, set(), now=1004), "performance")

    def test_external_profile_change_has_manual_priority(self):
        auto = Automation()
        auto.evaluate(self.settings, "balanced", True, set(), now=0)
        self.assertIsNone(auto.evaluate(self.settings, "low-power", True, set(), now=1))
        self.assertIsNone(auto.evaluate(self.settings, "low-power", True, set(), now=600))
        self.assertIsNone(auto.evaluate(self.settings, "low-power", True, set(), now=601))
        self.assertEqual(auto.evaluate(self.settings, "low-power", True, set(), now=604), "performance")

    def test_explicit_manual_pause(self):
        auto = Automation()
        auto.pause(now=0)
        self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=599))
        self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=600))
        self.assertEqual(auto.evaluate(self.settings, "balanced", True, set(), now=603), "performance")

    def test_unknown_power_restarts_stability_window(self):
        auto = Automation()
        auto.evaluate(self.settings, "balanced", True, set(), now=0)
        self.assertIsNone(auto.evaluate(self.settings, "balanced", None, set(), now=1))
        self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=10))
        self.assertEqual(auto.evaluate(self.settings, "balanced", True, set(), now=13), "performance")

    def test_unknown_processes_restarts_stability_window(self):
        self.settings["applications"] = [{"process": "firefox", "profile": "performance"}]
        auto = Automation()
        auto.evaluate(self.settings, "balanced", False, {"firefox"}, now=0)
        self.assertIsNone(auto.evaluate(self.settings, "balanced", False, None, now=1))
        self.assertIsNone(auto.evaluate(self.settings, "balanced", False, {"firefox"}, now=10))
        self.assertEqual(auto.evaluate(self.settings, "balanced", False, {"firefox"}, now=13), "performance")

    def test_unknown_profile_restarts_stability_window(self):
        auto = Automation()
        auto.evaluate(self.settings, "balanced", True, set(), now=0)
        self.assertIsNone(auto.evaluate(self.settings, None, True, set(), now=1))
        self.assertIsNone(auto.evaluate(self.settings, "balanced", True, set(), now=10))
        self.assertEqual(auto.evaluate(self.settings, "balanced", True, set(), now=13), "performance")

    def test_first_application_rule_has_priority_even_without_power_data(self):
        self.settings["applications"] = [{"process": "firefox", "profile": "performance"},
                                         {"process": "firefox", "profile": "low-power"}]
        auto = Automation()
        auto.evaluate(self.settings, "balanced", None, {"firefox"}, now=0)
        self.assertEqual(auto.evaluate(self.settings, "balanced", None, {"firefox"}, now=3), "performance")
        self.settings["automation"] = False
        self.assertIsNone(auto.evaluate(self.settings, "performance", None, {"firefox"}, now=40))

    def test_process_names_handle_exited_tasks_and_unreadable_proc(self):
        (self.root / "1").mkdir()
        (self.root / "1/comm").write_text("firefox\n")
        (self.root / "2").mkdir()
        (self.root / "not-a-task").mkdir()
        self.assertEqual(process_names(self.root), {"firefox"})
        self.assertIsNone(process_names(self.root / "missing"))
        with patch.object(Path, "read_text", side_effect=PermissionError):
            self.assertIsNone(process_names(self.root))

    def test_battery_history_throttles_and_exports_missing_values(self):
        history = BatteryHistory(self.root / "history")
        battery = dict(name="BAT1", status="Discharging", percent=50, power_w=None)
        history.add([battery], now=1700000000)
        history.add([battery], now=1700000001)
        history.add([battery], now=1700000030)
        destination = self.root / "export.csv"
        history.export(destination)
        with destination.open(encoding="utf-8-sig", newline="") as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual(len(rows), 2)
        self.assertEqual(rows[0]["battery"], "BAT1")
        self.assertEqual(rows[0]["power_w"], "")

    def test_history_export_cannot_overwrite_source_or_hardlink(self):
        history = BatteryHistory(self.root / "history")
        history.add([dict(name="BAT1")], now=1700000000)
        source = next(history.directory.glob("*.csv"))
        content = source.read_bytes()
        link = self.root / "hardlink.csv"
        link.hardlink_to(source)
        for destination in (source, history.directory / "new.csv", link):
            with self.assertRaises(ValueError):
                history.export(destination)
        self.assertEqual(source.read_bytes(), content)

    def test_history_retains_recent_files(self):
        history = BatteryHistory(self.root / "history")
        history.directory.mkdir()
        old = history.directory / "battery-2020-01-01.csv"
        old.write_text("old\n")
        os.utime(old, (1, 1))
        unrelated = history.directory / "keep.csv"
        unrelated.write_text("keep")
        history.add([dict(name="BAT1")], now=1700000000)
        self.assertFalse(old.exists())
        self.assertTrue(unrelated.exists())


if __name__ == "__main__":
    unittest.main()
