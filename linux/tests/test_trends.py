import math
import time
import unittest
from unittest.mock import patch

from fancontroller.trends import PercentageHistory, SeriesHistory, TemperatureHistory, sample_time


def sensors(**values):
    return [{"name": name, "celsius": value} for name, value in values.items()]


class TemperatureHistoryTests(unittest.TestCase):
    def test_initial_point_does_not_invent_past_history(self):
        history = TemperatureHistory()
        history.add(sensors(cpu=42), timestamp=100)
        self.assertEqual(history.series(now=100), {"cpu": [[(100, 42)]]})
        self.assertEqual(history.latest(), {"cpu": 42})

    def test_regular_samples_join_and_return_value_is_independent(self):
        history = TemperatureHistory()
        history.add(sensors(cpu=42), timestamp=0)
        history.add(sensors(cpu=43), timestamp=2)
        series = history.series(now=2)
        self.assertEqual(series, {"cpu": [[(0, 42), (2, 43)]]})
        series["cpu"][0].append((3, 99))
        self.assertEqual(history.series(now=2), {"cpu": [[(0, 42), (2, 43)]]})
        latest = history.latest()
        latest["cpu"] = 99
        self.assertEqual(history.latest(), {"cpu": 43})

    def test_missing_sensor_breaks_line_without_zero_sample(self):
        history = TemperatureHistory()
        history.add(sensors(cpu=42, gpu=40), timestamp=0)
        history.add(sensors(gpu=41), timestamp=2)
        self.assertEqual(history.latest(), {"cpu": None, "gpu": 41})
        history.add(sensors(cpu=44, gpu=42), timestamp=4)
        self.assertEqual(history.series(now=4), {
            "cpu": [[(0, 42)], [(4, 44)]],
            "gpu": [[(0, 40), (2, 41), (4, 42)]],
        })

    def test_suspend_or_long_gap_splits_at_threshold(self):
        history = TemperatureHistory(max_gap_seconds=10)
        history.add(sensors(cpu=42), timestamp=0)
        history.add(sensors(cpu=43), timestamp=10)
        history.add(sensors(cpu=44), timestamp=20.01)
        self.assertEqual(history.series(now=20.01), {"cpu": [[(0, 42), (10, 43)], [(20.01, 44)]]})

    def test_retention_trims_points_segments_and_retires_missing_sensor(self):
        history = TemperatureHistory(window_seconds=10, max_gap_seconds=3)
        history.add(sensors(cpu=40, gpu=39), timestamp=0)
        history.add(sensors(cpu=41), timestamp=2)
        history.add(sensors(cpu=42), timestamp=8)
        self.assertEqual(history.series(now=10), {"cpu": [[(0, 40), (2, 41)], [(8, 42)]], "gpu": [[(0, 39)]]})
        self.assertEqual(history.series(now=10.1), {"cpu": [[(2, 41)], [(8, 42)]]})
        self.assertEqual(history.latest(), {"cpu": 42})
        self.assertEqual(history.series(now=19), {})
        self.assertEqual(history.latest(), {})

    def test_snapshot_reordering_does_not_reorder_series(self):
        history = TemperatureHistory()
        history.add(sensors(cpu=42, gpu=40), timestamp=0)
        history.add(sensors(gpu=41, cpu=43, nvme=30), timestamp=2)
        self.assertEqual(list(history.series(now=2)), ["cpu", "gpu", "nvme"])
        self.assertEqual(list(history.latest()), ["cpu", "gpu", "nvme"])

    def test_invalid_values_break_segments_and_never_create_invalid_series(self):
        invalid = (None, math.nan, math.inf, -math.inf, "42", True, -31, 151, 10**1000)
        for value in invalid:
            with self.subTest(value=value):
                history = TemperatureHistory()
                history.add(sensors(cpu=40), timestamp=0)
                history.add(sensors(cpu=value, gpu=value), timestamp=2)
                self.assertEqual(history.latest(), {"cpu": None})
                history.add(sensors(cpu=41), timestamp=4)
                self.assertEqual(history.series(now=4), {"cpu": [[(0, 40)], [(4, 41)]]})

    def test_valid_temperature_bounds_and_real_zero_are_retained(self):
        history = TemperatureHistory()
        history.add(sensors(cold=-30, hot=150, zero=0), timestamp=0)
        self.assertEqual(history.latest(), {"cold": -30, "hot": 150, "zero": 0})

    def test_timestamp_validation_does_not_mutate_existing_history(self):
        history = TemperatureHistory()
        history.add(sensors(cpu=40), timestamp=10)
        for timestamp in (9, math.nan, math.inf, "11", True, 10**1000):
            with self.subTest(timestamp=timestamp), self.assertRaises(ValueError):
                history.add(sensors(cpu=99), timestamp=timestamp)
        self.assertEqual(history.series(now=10), {"cpu": [[(10, 40)]]})
        with self.assertRaises(ValueError):
            history.series(now=math.inf)

    def test_duplicate_timestamp_updates_point(self):
        history = TemperatureHistory()
        history.add(sensors(cpu=40), timestamp=10)
        history.add(sensors(cpu=41), timestamp=10)
        self.assertEqual(history.series(now=10), {"cpu": [[(10, 41)]]})

    def test_default_clock_includes_suspend_time(self):
        history = TemperatureHistory()
        with patch("fancontroller.trends.time.clock_gettime", return_value=20) as clock:
            history.add(sensors(cpu=40))
            self.assertEqual(history.series(), {"cpu": [[(20, 40)]]})
            self.assertEqual(clock.call_count, 2)
            clock.assert_called_with(time.CLOCK_BOOTTIME)

    def test_suspend_elapsed_in_default_clock_breaks_the_line(self):
        history = TemperatureHistory()
        with patch("fancontroller.trends.time.clock_gettime", side_effect=[10, 100, 100]):
            history.add(sensors(cpu=40))
            history.add(sensors(cpu=41))
            self.assertEqual(history.series(), {"cpu": [[(10, 40)], [(100, 41)]]})

    def test_clock_falls_back_to_monotonic_when_boottime_is_unavailable(self):
        with patch("fancontroller.trends.time.CLOCK_BOOTTIME", None, create=True), \
                patch("fancontroller.trends.time.monotonic", return_value=30):
            self.assertEqual(sample_time(), 30)

    def test_clock_falls_back_to_monotonic_if_kernel_rejects_boottime(self):
        with patch("fancontroller.trends.time.clock_gettime", side_effect=OSError("not supported")), \
                patch("fancontroller.trends.time.monotonic", return_value=30):
            self.assertEqual(sample_time(), 30)

    def test_configuration_must_be_finite_and_positive(self):
        for value in (0, -1, math.nan, math.inf, "10", True):
            for key in ("window_seconds", "max_gap_seconds"):
                with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                    TemperatureHistory(**{key: value})

    def test_malformed_sensor_entries_do_not_crash_or_make_lines(self):
        history = TemperatureHistory()
        history.add([None, {}, {"name": "", "celsius": 42}, {"name": 1, "celsius": 42},
                     {"name": "valid", "celsius": 42}], timestamp=0)
        self.assertEqual(history.series(now=0), {"valid": [[(0, 42)]]})


class PercentageHistoryTests(unittest.TestCase):
    def test_zero_and_one_hundred_are_real_samples(self):
        history = PercentageHistory()
        history.add([{"name": "CPU", "value": 0}, {"name": "内存", "value": 100}], timestamp=0)
        self.assertEqual(history.latest(), {"CPU": 0, "内存": 100})
        self.assertEqual(history.series(now=0), {"CPU": [[(0, 0)]], "内存": [[(0, 100)]]})

    def test_unknown_out_of_range_and_nonfinite_values_break_lines(self):
        for value in (None, -1, 101, math.nan, math.inf, "42", False):
            with self.subTest(value=value):
                history = PercentageHistory()
                history.add([{"name": "CPU", "value": 10}], timestamp=0)
                history.add([{"name": "CPU", "value": value}], timestamp=2)
                self.assertEqual(history.latest(), {"CPU": None})
                history.add([{"name": "CPU", "value": 20}], timestamp=4)
                self.assertEqual(history.series(now=4), {"CPU": [[(0, 10)], [(4, 20)]]})

    def test_retention_and_order_share_the_generic_history_behavior(self):
        history = PercentageHistory(window_seconds=10)
        history.add([{"name": "CPU", "value": 10}, {"name": "内存", "value": 20}], timestamp=0)
        history.add([{"name": "内存", "value": 21}, {"name": "CPU", "value": 11}], timestamp=5)
        self.assertEqual(list(history.series(now=5)), ["CPU", "内存"])
        self.assertEqual(history.series(now=11), {"CPU": [[(5, 11)]], "内存": [[(5, 21)]]})
        self.assertEqual(history.series(now=16), {})


class SeriesHistoryTests(unittest.TestCase):
    def test_generic_series_accepts_finite_values_without_temperature_bounds(self):
        history = SeriesHistory(value_key="watts")
        history.add([{"name": "input", "watts": -500}, {"name": "output", "watts": 1000}], timestamp=0)
        self.assertEqual(history.latest(), {"input": -500, "output": 1000})

    def test_custom_bounds_are_inclusive(self):
        history = SeriesHistory(minimum=10, maximum=20)
        history.add([{"name": "low", "value": 10}, {"name": "high", "value": 20},
                     {"name": "invalid", "value": 9}], timestamp=0)
        self.assertEqual(history.latest(), {"low": 10, "high": 20})

    def test_invalid_configuration_is_rejected(self):
        for config in ({"minimum": 20, "maximum": 10}, {"minimum": math.nan},
                       {"maximum": math.inf}, {"value_key": ""}, {"value_key": None}):
            with self.subTest(config=config), self.assertRaises(ValueError):
                SeriesHistory(**config)


if __name__ == "__main__":
    unittest.main()
