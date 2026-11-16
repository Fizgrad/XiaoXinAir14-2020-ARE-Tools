"""Initial window geometry in GTK application pixels, independent of device DPI."""

import math


PREFERRED_SIZE = (840, 620)
WORKAREA_MARGIN = 32


def _fit_dimension(work_size, preferred):
    if isinstance(work_size, bool) or not isinstance(work_size, (int, float)):
        return preferred
    try:
        if not math.isfinite(work_size) or work_size <= 0:
            return preferred
    except OverflowError:
        return preferred
    return max(1, min(preferred, int(work_size) - WORKAREA_MARGIN))


def initial_window_size(work_width=None, work_height=None):
    """Keep the compact default while fitting the monitor's logical work area.

    ``Gdk.Monitor.get_workarea()`` already returns application pixels, including
    desktop scaling. Applying a scale factor again would enlarge the window and
    defeat fitting on a high DPI screen. Missing monitor information uses the
    preferred size independently for each dimension.

    This is an initial preference; GTK still applies the widget minimum size,
    and users remain free to resize their window.
    """
    return (_fit_dimension(work_width, PREFERRED_SIZE[0]),
            _fit_dimension(work_height, PREFERRED_SIZE[1]))
