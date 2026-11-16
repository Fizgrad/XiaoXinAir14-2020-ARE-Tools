"""Native vector monitoring charts in GTK logical coordinates."""

import html
import math

import gi
gi.require_version("Gtk", "3.0")
gi.require_version("PangoCairo", "1.0")
gi.require_foreign("cairo")
from gi.repository import Gtk, Pango, PangoCairo

from .trends import PercentageHistory, TemperatureHistory, sample_time


COLORS = ("#e76f36", "#3b82f6", "#14a38b", "#ab65d9", "#d59b17",
          "#e05c91", "#599329", "#5f83a6", "#a97956", "#26a4c8")


class HistoryChart(Gtk.Box):
    def __init__(self, history, title, unit, minimum, maximum, empty_text, compact=False):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=4 if compact else 8)
        self.history = history
        self.unit = unit
        self.minimum = minimum
        self.maximum = maximum
        self.empty_text = empty_text
        self.compact = compact
        self.height_lines = 5 if compact else 6
        self.minimum_height = 96 if compact else 110
        self.colors = {}
        self.draw_count = 0
        self.last_scale = 1
        self.heading_row = Gtk.Box(spacing=12)
        heading = Gtk.Label(label=title, xalign=0, valign=Gtk.Align.BASELINE)
        if compact:
            heading.set_width_chars(5)
        self.heading_row.pack_start(heading, False, False, 0)
        self.pack_start(self.heading_row, False, False, 0)

        self.canvas = Gtk.DrawingArea()
        self.canvas.connect("draw", self.draw_chart)
        self.canvas.connect("style-updated", self.font_changed)
        self.canvas.connect("notify::scale-factor", lambda *_: self.canvas.queue_draw())
        self.font_changed(self.canvas)
        self.pack_start(self.canvas, False, False, 0)

        if compact:
            self.legend = Gtk.Box(spacing=8, valign=Gtk.Align.BASELINE)
        else:
            self.legend = Gtk.FlowBox()
            self.legend.set_selection_mode(Gtk.SelectionMode.NONE)
            self.legend.set_homogeneous(True)
            self.legend.set_min_children_per_line(1)
            self.legend.set_max_children_per_line(3)
            self.legend.set_column_spacing(12)
            self.legend.set_row_spacing(0)
        self.legend_labels = {}
        if compact:
            self.heading_row.pack_start(self.legend, False, False, 0)
        else:
            self.pack_start(self.legend, False, False, 0)

    def add(self, sensors):
        self.history.add(sensors)
        latest = self.history.latest()
        for name in list(self.legend_labels):
            if name not in latest:
                widget = self.legend_labels.pop(name)
                self.legend.remove(widget if self.compact else widget.get_parent())
        for name, value in latest.items():
            if name not in self.colors:
                self.colors[name] = COLORS[len(self.colors) % len(COLORS)]
            if name not in self.legend_labels:
                widget = Gtk.Label(xalign=0, valign=Gtk.Align.BASELINE)
                if self.compact:
                    widget.set_width_chars(10)
                else:
                    widget.set_ellipsize(Pango.EllipsizeMode.END)
                    widget.set_max_width_chars(26)
                widget.set_tooltip_text(name)
                self.legend.add(widget)
                self.legend_labels[name] = widget
            reading = "—" if value is None else f"{value:.1f} {self.unit}"
            if self.compact:
                reading = f'<span font_family="monospace" font_features="tnum" style="normal">{reading}</span>'
            self.legend_labels[name].set_markup(
                f'<span foreground="{self.colors[name]}">●</span> '
                + ("" if self.compact else html.escape(name) + "  ") + reading)
        self.legend.show_all()
        self.canvas.queue_draw()

    def font_changed(self, _):
        _, line_height = self.canvas.create_pango_layout("20 " + self.unit).get_pixel_size()
        self.canvas.set_size_request(-1, max(self.minimum_height, line_height * self.height_lines))
        self.canvas.queue_draw()

    def draw_chart(self, widget, context):
        width, height = widget.get_allocated_width(), widget.get_allocated_height()
        if width < 120 or height < 80:
            return False
        now = sample_time()
        series = self.history.series(now)
        points = [point for segments in series.values() for segment in segments for point in segment]
        minimum = math.floor(min([self.minimum] + [p[1] for p in points]) / 20) * 20
        maximum = math.ceil(max([self.maximum] + [p[1] for p in points]) / 20) * 20
        label_width, line_height = self.canvas.create_pango_layout("100").get_pixel_size()
        end_width, _ = self.canvas.create_pango_layout("现在").get_pixel_size()
        left, right = label_width + 12, width - max(16, end_width / 2 + 4)
        top, bottom = line_height + 4, height - line_height - 8
        foreground = widget.get_style_context().get_color(Gtk.StateFlags.NORMAL)

        def text(caption, px, py, anchor=0):
            layout = widget.create_pango_layout(caption)
            attrs = Pango.AttrList()
            attrs.insert(Pango.attr_scale_new(0.85))
            layout.set_attributes(attrs)
            text_width, _ = layout.get_pixel_size()
            context.set_source_rgba(foreground.red, foreground.green, foreground.blue, foreground.alpha)
            context.move_to(px - anchor * text_width, py)
            PangoCairo.show_layout(context, layout)

        text(self.unit, 4, 0)

        def x(timestamp):
            return left + (timestamp - now + 300) / 300 * (right - left)

        def y(value):
            return bottom - (value - minimum) / (maximum - minimum) * (bottom - top)

        desired_divisions = int((maximum - minimum) / 20)
        divisions = desired_divisions if bottom - top >= desired_divisions * line_height * 1.1 else 2
        if bottom - top < 2 * line_height * 1.1:
            divisions = 1
        for i in range(divisions + 1):
            value = minimum + (maximum - minimum) * i / divisions
            position = y(value)
            context.set_source_rgba(foreground.red, foreground.green, foreground.blue, 0.13)
            context.set_line_width(1)
            context.move_to(left, position)
            context.line_to(right, position)
            context.stroke()
            text(f"{value:g}", left - 7, position - line_height * 0.425, 1)
        tick_width, _ = self.canvas.create_pango_layout("−5m").get_pixel_size()
        tick_seconds = (300, 240, 180, 120, 60, 0) if right - left >= 6 * (tick_width + 14) else (300, 180, 0)
        if right - left < 3 * (tick_width + 14):
            tick_seconds = (300, 0)
        for seconds in tick_seconds:
            position = left + (300 - seconds) / 300 * (right - left)
            caption = f"−{seconds // 60}m" if seconds else "现在"
            text(caption, position, bottom + 6, 0.5)
        context.save()
        context.rectangle(left - 3, top - 3, right - left + 6, bottom - top + 6)
        context.clip()
        for name, segments in series.items():
            color = self.colors.get(name, COLORS[0])
            context.set_source_rgb(*(int(color[i:i + 2], 16) / 255 for i in (1, 3, 5)))
            context.set_line_width(2)
            context.set_line_join(1)
            for segment in segments:
                if not segment:
                    continue
                context.move_to(x(segment[0][0]), y(segment[0][1]))
                for timestamp, value in segment[1:]:
                    context.line_to(x(timestamp), y(value))
                context.stroke()
                t, value = segment[-1]
                context.arc(x(t), y(value), 2.5, 0, 2 * math.pi)
                context.fill()
        context.restore()
        if not points:
            text(self.empty_text, (left + right) / 2, (top + bottom - line_height) / 2, 0.5)
        self.draw_count += 1
        self.last_scale = widget.get_scale_factor()
        return False


class TemperatureChart(HistoryChart):
    def __init__(self):
        super().__init__(TemperatureHistory(), "温度 · 最近 5 分钟", "°C", 20, 100, "暂无温度数据")


class ResourceChart(HistoryChart):
    def __init__(self, name, color):
        super().__init__(PercentageHistory(), name, "%", 0, 100, "暂无数据", compact=True)
        self.colors[name] = color
        self.set_size_request(210, -1)
        self.set_valign(Gtk.Align.START)
