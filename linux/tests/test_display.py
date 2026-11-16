import unittest

from fancontroller.display import initial_window_size


class InitialWindowSizeTests(unittest.TestCase):
    def test_large_workareas_preserve_compact_default(self):
        for workarea in ((1920, 1040), (2560, 1400), (3840, 2100)):
            with self.subTest(workarea=workarea):
                self.assertEqual(initial_window_size(*workarea), (840, 620))

    def test_logical_workareas_at_different_scales(self):
        # Representative 1920x1080 output workareas at100%,150%,200%.
        # The compositor/GDK has already converted device pixels to logical ones.
        for workarea, expected in (((1920, 1040), (840, 620)),
                                   ((1280, 693), (840, 620)),
                                   ((960, 520), (840, 488))):
            with self.subTest(workarea=workarea):
                self.assertEqual(initial_window_size(*workarea), expected)

    def test_short_laptop_and_portrait_workareas_fit_each_axis(self):
        self.assertEqual(initial_window_size(1280, 576), (840, 544))
        self.assertEqual(initial_window_size(768, 1280), (736, 620))
        self.assertEqual(initial_window_size(800, 600), (768, 568))

    def test_missing_invalid_and_nonfinite_monitor_values_use_fallback(self):
        self.assertEqual(initial_window_size(), (840, 620))
        for invalid in (None, "1920", True, False, 0, -1, float("nan"), float("inf"), float("-inf")):
            with self.subTest(invalid=invalid):
                self.assertEqual(initial_window_size(invalid, 500), (840, 468))
                self.assertEqual(initial_window_size(700, invalid), (668, 620))

    def test_fractional_dimensions_round_down_and_remain_positive(self):
        self.assertEqual(initial_window_size(800.9, 600.9), (768, 568))
        self.assertEqual(initial_window_size(20, 20), (1, 1))


if __name__ == "__main__":
    unittest.main()
