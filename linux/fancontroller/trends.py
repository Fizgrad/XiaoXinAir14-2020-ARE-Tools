"""Bounded metric samples for live graphs, without synthesized readings."""

from __future__ import annotations

from collections import deque
import math
import time


def sample_time() -> float:
    """Use Linux's suspend-aware monotonic clock for samples and graph axes."""
    clock = getattr(time, "CLOCK_BOOTTIME", None)
    if clock is not None:
        try:
            return time.clock_gettime(clock)
        except (AttributeError, OSError):
            pass
    return time.monotonic()


def _finite_number(value) -> bool:
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        return False
    try:
        return math.isfinite(value)
    except OverflowError:
        return False


class SeriesHistory:
    """Keep raw samples in separate segments when measurements are interrupted.

    Timestamps use the monotonic clock. Invalid/missing readings break a sensor's
    segment, as does a sampling pause longer than ``max_gap_seconds``. A sensor
    retires when its last measured point leaves the retention window.
    """

    def __init__(self, window_seconds=300, max_gap_seconds=10, *, value_key="value", minimum=None, maximum=None):
        for name, value in (("window_seconds", window_seconds), ("max_gap_seconds", max_gap_seconds)):
            if not _finite_number(value) or value <= 0:
                raise ValueError(f"{name} must be a finite positive number")
        if not isinstance(value_key, str) or not value_key:
            raise ValueError("value_key must be a nonempty string")
        for name, value in (("minimum", minimum), ("maximum", maximum)):
            if value is not None and not _finite_number(value):
                raise ValueError(f"{name} must be a finite number or None")
        if minimum is not None and maximum is not None and minimum > maximum:
            raise ValueError("minimum must not exceed maximum")
        self.window_seconds = float(window_seconds)
        self.max_gap_seconds = float(max_gap_seconds)
        self.value_key = value_key
        self.minimum = minimum
        self.maximum = maximum
        # Dict insertion order keeps colors/legend stable when sysfs reorders.
        self._data: dict[str, deque[deque[tuple[float, float]]]] = {}
        self._connected: dict[str, bool] = {}
        self._latest: dict[str, float | None] = {}
        self._last_timestamp: float | None = None

    @staticmethod
    def _timestamp(value):
        value = sample_time() if value is None else value
        if not _finite_number(value):
            raise ValueError("timestamp must be a finite number")
        return float(value)

    def _trim(self, now: float):
        cutoff = now - self.window_seconds
        for name in list(self._data):
            segments = self._data[name]
            while segments:
                while segments[0] and segments[0][0][0] < cutoff:
                    segments[0].popleft()
                if segments[0]:
                    break
                segments.popleft()
            if not segments:
                del self._data[name]
                self._connected.pop(name, None)
                self._latest.pop(name, None)

    def add(self, sensors: list[dict], timestamp: float | None = None):
        timestamp = self._timestamp(timestamp)
        if self._last_timestamp is not None and timestamp < self._last_timestamp:
            raise ValueError("timestamps must not move backwards")
        self._trim(timestamp)
        readings = {}
        for sensor in sensors:
            if not isinstance(sensor, dict):
                continue
            name, value = sensor.get("name"), sensor.get(self.value_key)
            if not isinstance(name, str) or not name.strip():
                continue
            # Each snapshot has one value per named sensor; the last duplicate
            # determines whether that sensor was measured in this snapshot.
            valid = (_finite_number(value)
                     and (self.minimum is None or value >= self.minimum)
                     and (self.maximum is None or value <= self.maximum))
            readings[name] = float(value) if valid else None
        for name in self._data:
            if readings.get(name) is None:
                self._connected[name] = False
                self._latest[name] = None
        for name, value in readings.items():
            if value is None:
                continue
            if name not in self._data:
                self._data[name] = deque()
                self._connected[name] = False
            segments = self._data[name]
            previous = segments[-1][-1] if segments else None
            if previous is not None and self._connected[name] and timestamp - previous[0] <= self.max_gap_seconds:
                if previous[0] == timestamp:
                    # Repeated updates at the same instant replace a point,
                    # rather than drawing a vertical line from duplicate data.
                    segments[-1][-1] = timestamp, value
                else:
                    segments[-1].append((timestamp, value))
            else:
                segments.append(deque([(timestamp, value)]))
            self._connected[name] = True
            self._latest[name] = value
        self._last_timestamp = timestamp

    def series(self, now: float | None = None) -> dict[str, list[list[tuple[float, float]]]]:
        now = self._timestamp(now)
        self._trim(now)
        return {name: [list(segment) for segment in segments] for name, segments in self._data.items()}

    def latest(self) -> dict[str, float | None]:
        return dict(self._latest)


class TemperatureHistory(SeriesHistory):
    def __init__(self, window_seconds=300, max_gap_seconds=10):
        super().__init__(window_seconds, max_gap_seconds, value_key="celsius", minimum=-30, maximum=150)


class PercentageHistory(SeriesHistory):
    def __init__(self, window_seconds=300, max_gap_seconds=10):
        super().__init__(window_seconds, max_gap_seconds, value_key="value", minimum=0, maximum=100)
