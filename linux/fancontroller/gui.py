"""GTK 3 desktop UI. No X11 APIs, global input hooks, or root GUI process."""

from collections import deque
import datetime
import json
import threading
import time

import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk

from . import APP_NAME, __version__
from .backend import Backend
from .cli import export_report
from .choice_buttons import ChoiceButtons
from .control_session import ControlSession
from .display import initial_window_size
from .state import (Automation, BatteryHistory, Settings, autostart_path,
                    process_names, set_autostart)
from .temperature_chart import ResourceChart, TemperatureChart


LABELS = {"profile": "Fn+Q 散热模式", "conservation": "养护充电",
          "usb_charging": "USB 持续供电", "fn_lock": "Fn 锁",
          "keyboard_backlight": "键盘背光", "fan_dust": "单次除尘"}
VALUES = {"low-power": "节能 / 静音", "balanced": "智能散热 / 均衡", "performance": "性能"}
PROFILE_CHOICES = (("low-power", "节能"), ("balanced", "均衡"), ("performance", "性能"))


def control_value(name, value):
    if name == "profile":
        return VALUES.get(value, value)
    if name in ("conservation", "usb_charging", "fn_lock"):
        return "开启" if value == "1" else "关闭"
    if name == "keyboard_backlight":
        return "关闭" if value == "0" else "亮度 " + value
    return value


def number(value, unit="", digits=1):
    return "暂无数据" if value is None else f"{value:.{digits}f}{unit}"


def label(text="", style=None):
    widget = Gtk.Label(label=text, xalign=0)
    widget.set_line_wrap(True)
    widget.set_selectable(True)
    if style:
        widget.get_style_context().add_class(style)
    return widget


def box(spacing=12):
    return Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=spacing)


def section(title, parent, accessory=None):
    frame = Gtk.Frame()
    frame.set_shadow_type(Gtk.ShadowType.NONE)
    frame.get_style_context().add_class("card")
    content = box(10)
    content.set_border_width(18)
    heading = Gtk.Box(spacing=12)
    heading.pack_start(label(title, "section-title"), True, True, 0)
    if accessory is not None:
        heading.pack_end(accessory, False, False, 0)
    content.pack_start(heading, False, False, 0)
    frame.add(content)
    parent.pack_start(frame, False, False, 0)
    return content


class Window(Gtk.Window):
    def __init__(self, smoke_test=False):
        super().__init__(title=APP_NAME)
        display = Gdk.Display.get_default()
        monitor = display.get_primary_monitor()
        if monitor is None and display.get_n_monitors():
            monitor = display.get_monitor(0)
        area = monitor.get_workarea() if monitor else None
        self.set_default_size(*initial_window_size(area.width, area.height) if area else initial_window_size())
        self.set_position(Gtk.WindowPosition.CENTER)
        self.set_icon_name("fancontroller")
        self.backend = Backend()
        self.control_session = ControlSession()
        self.settings = Settings()
        self.history = BatteryHistory()
        self.automation = Automation()
        self.snapshot = None
        self.polling = False
        self.busy = False
        self.sample_status = "正在读取设备信息…"
        self.feedback_message = ""
        self.feedback_until = 0
        self.closed = False
        self.smoke_test = smoke_test
        self.smoke_ok = False
        self.recent = deque(maxlen=8)
        self.editors = {}
        self.control_handlers = []
        self.control_signature = None
        self.connect("destroy", self.on_close)

        header = Gtk.HeaderBar(title=APP_NAME)
        header.set_has_subtitle(False)
        header.set_tooltip_text(f"Debian · {__version__}")
        header.get_style_context().add_class("compact-titlebar")
        header.set_show_close_button(True)
        export = Gtk.Button(label="导出报告")
        export.connect("clicked", self.export)
        header.pack_end(export)
        self.set_titlebar(header)
        root = box(0)
        self.add(root)
        stack = Gtk.Stack(transition_type=Gtk.StackTransitionType.CROSSFADE)
        switcher = Gtk.StackSwitcher(stack=stack, halign=Gtk.Align.CENTER)
        switcher.get_style_context().add_class("compact-tabs")
        header.set_custom_title(switcher)
        header.pack_start(label(APP_NAME))
        root.pack_start(stack, True, True, 0)
        self.status = label(self.sample_status)
        self.status.set_margin_start(20)
        self.status.set_margin_end(20)
        self.status.set_margin_top(10)
        self.status.set_margin_bottom(10)
        root.pack_end(self.status, False, False, 0)
        self.pages = {}
        for name, title in (("dashboard", "仪表盘"), ("controls", "设备控制"),
                            ("hardware", "硬件健康"), ("settings", "设置")):
            content = box(14)
            content.set_border_width(20)
            scroll = Gtk.ScrolledWindow()
            scroll.set_policy(Gtk.PolicyType.AUTOMATIC, Gtk.PolicyType.AUTOMATIC)
            scroll.add(content)
            stack.add_titled(scroll, name, title)
            self.pages[name] = content
        self.make_dashboard()
        self.control_box = section("设备设置", self.pages["controls"])
        self.control_rows = box(10)
        self.control_box.pack_start(self.control_rows, False, False, 0)
        hardware = section("硬件清单", self.pages["hardware"])
        self.search = Gtk.SearchEntry(placeholder_text="搜索设备、分类或型号")
        self.search.connect("search-changed", self.filter_inventory)
        hardware.pack_start(self.search, False, False, 0)
        self.inventory = Gtk.ListStore(str, str, str)
        self.filtered = self.inventory.filter_new()
        self.filtered.set_visible_func(self.inventory_visible)
        tree = Gtk.TreeView(model=self.filtered)
        for i, title in enumerate(("分类", "设备", "详情")):
            cell = Gtk.CellRendererText()
            column = Gtk.TreeViewColumn(title, cell, text=i)
            column.set_resizable(True)
            if i == 2:
                column.set_expand(True)
            tree.append_column(column)
        hardware.pack_start(tree, True, True, 0)
        self.health_label = label()
        section("健康检查", self.pages["hardware"]).pack_start(self.health_label, False, False, 0)
        self.make_settings()
        self.show_all()
        if self.settings.error:
            self.feedback(self.settings.error)
        GLib.timeout_add_seconds(2, self.poll)
        self.poll()
        if smoke_test:
            GLib.timeout_add_seconds(6, self.finish_smoke)

    def make_dashboard(self):
        page = self.pages["dashboard"]
        monitor = section("实时监控 · 最近 5 分钟", page)
        resources = box(10)
        self.resource_charts = {name: ResourceChart(name, color) for name, color in
                                (("CPU", "#e76f36"), ("内存", "#3b82f6"), ("GPU", "#ab65d9"))}
        self.cpu_info, self.memory_info = label(), label()
        for info in (self.cpu_info, self.memory_info):
            info.set_xalign(1)
            info.set_valign(Gtk.Align.BASELINE)
            info.get_style_context().add_class("dim-label")
        self.resource_charts["CPU"].heading_row.pack_end(self.cpu_info, False, False, 0)
        self.resource_charts["内存"].heading_row.pack_end(self.memory_info, False, False, 0)
        for chart in self.resource_charts.values():
            resources.pack_start(chart, False, False, 0)
        monitor.pack_start(resources, False, False, 0)
        self.temperature_chart = TemperatureChart()
        monitor.pack_start(self.temperature_chart, False, False, 0)
        self.thermal_label = label()
        monitor.pack_start(self.thermal_label, False, False, 0)
        self.battery_label = label()
        section("电池", page).pack_start(self.battery_label, False, False, 0)
        self.identity_label = label("正在识别设备…")
        section("当前设备", page).pack_start(self.identity_label, False, False, 0)
        self.feedback_label = label("尚无设置操作")
        section("最近操作", page).pack_start(self.feedback_label, False, False, 0)

    def make_settings(self):
        page = self.pages["settings"]
        automatic = section("自动切换散热模式", page)
        self.auto_toggle = Gtk.CheckButton(label="启用自动场景")
        self.auto_toggle.set_active(self.settings.values["automation"])
        automatic.pack_start(self.auto_toggle, False, False, 0)
        self.ac_choice, self.battery_choice = ChoiceButtons(PROFILE_CHOICES), ChoiceButtons(PROFILE_CHOICES)
        for choice, key, title in ((self.ac_choice, "ac_profile", "接通电源"),
                                   (self.battery_choice, "battery_profile", "使用电池")):
            choice.set_active_id(self.settings.values[key])
            automatic.pack_start(label(title), False, False, 0)
            automatic.pack_start(choice, False, False, 0)
        automatic.pack_start(label("应用规则：每行填写 进程名=模式，按顺序匹配。\n"
                                   "例如 firefox=balanced；模式：low-power / balanced / performance。"), False, False, 0)
        self.rules = Gtk.TextView()
        self.rules.set_size_request(-1, 90)
        self.rules.get_buffer().set_text("\n".join(r["process"] + "=" + r["profile"]
            for r in self.settings.values["applications"]))
        automatic.pack_start(self.rules, False, False, 0)
        automatic.pack_start(label("切换间隔 30 秒 · 手动优先 10 分钟"), False, False, 0)
        self.auto_status = label()
        automatic.pack_start(self.auto_status, False, False, 0)
        resume = Gtk.Button(label="恢复自动切换")
        resume.connect("clicked", lambda _: self.automation.resume())
        automatic.pack_start(resume, False, False, 0)
        desktop = section("桌面与记录", page)
        self.startup_toggle = Gtk.CheckButton(label="登录时启动")
        self.startup_toggle.set_active(autostart_path().exists())
        self.record_toggle = Gtk.CheckButton(label="记录电池历史（保留 30 天）")
        self.record_toggle.set_active(self.settings.values["record_battery"])
        desktop.pack_start(self.startup_toggle, False, False, 0)
        desktop.pack_start(self.record_toggle, False, False, 0)
        export = Gtk.Button(label="导出电池历史 CSV")
        export.connect("clicked", self.export_history)
        desktop.pack_start(export, False, False, 0)
        save = Gtk.Button(label="保存设置")
        save.get_style_context().add_class("suggested-action")
        save.connect("clicked", self.save_settings)
        page.pack_start(save, False, False, 0)

    def save_settings(self, _):
        if self.smoke_test:
            return
        try:
            buffer = self.rules.get_buffer()
            text = buffer.get_text(buffer.get_start_iter(), buffer.get_end_iter(), True)
            rules = []
            for line in text.splitlines():
                if not line.strip():
                    continue
                name, sep, profile = line.strip().partition("=")
                if not sep or not name.strip() or profile.strip() not in VALUES:
                    raise ValueError("应用规则格式应为 进程名=low-power/balanced/performance")
                rules.append({"process": name.strip(), "profile": profile.strip()})
            new_values = dict(self.settings.values, automation=self.auto_toggle.get_active(),
                ac_profile=self.ac_choice.get_active_id(), battery_profile=self.battery_choice.get_active_id(),
                applications=rules, record_battery=self.record_toggle.get_active())
            previous = self.settings.values
            self.settings.values = new_values
            try:
                self.settings.save()
                set_autostart(self.startup_toggle.get_active())
            except OSError:
                self.settings.values = previous
                self.settings.save()
                raise
            self.automation.resume()
            self.feedback("设置已保存")
        except (OSError, ValueError) as exc:
            self.feedback("保存失败：" + str(exc))

    def feedback(self, message):
        self.feedback_message = message
        self.feedback_until = time.monotonic() + 8
        self.refresh_status()
        self.recent.appendleft(datetime.datetime.now().strftime("%H:%M:%S") + "  " + message)
        self.feedback_label.set_text("\n".join(self.recent))

    def refresh_status(self):
        if self.busy:
            text = "正在应用设置…" if self.control_session.authorized else "等待管理员授权并应用设置…"
        elif time.monotonic() < self.feedback_until:
            text = self.feedback_message
        else:
            text = self.sample_status
        self.status.set_text(text)

    def poll(self):
        if self.closed:
            return False
        if not self.polling and not self.busy:
            self.polling = True
            def collect():
                try:
                    data = self.backend.snapshot()
                    processes = process_names() if self.settings.values["applications"] else set()
                    GLib.idle_add(self.update, data, processes)
                except Exception as exc:
                    GLib.idle_add(self.poll_error, str(exc))
            threading.Thread(target=collect, daemon=True).start()
        return True

    def poll_error(self, message):
        self.polling = False
        if not self.closed:
            self.sample_status = "读取失败：" + message
            self.temperature_chart.add([])
            for chart in self.resource_charts.values():
                chart.add([])
            self.refresh_status()
        return False

    def update(self, data, processes):
        self.polling = False
        if self.closed:
            return False
        self.snapshot = data
        identity, metrics = data["identity"], data["metrics"]
        self.identity_label.set_text(f"{identity.get('manufacturer', '')} {identity.get('model', '')}  /  {identity.get('bios', '')}\n"
                                     f"{identity.get('cpu', '')}\n{identity.get('os', '')} · {identity.get('kernel', '')}")
        self.cpu_info.set_text(number(metrics.get("cpu_mhz"), " MHz", 0))
        self.memory_info.set_text(number(metrics.get("memory_used_gib"), "") + " / " + number(metrics.get("memory_total_gib"), " GiB"))
        for name, key in (("CPU", "cpu_percent"), ("内存", "memory_percent"), ("GPU", "gpu_percent")):
            self.resource_charts[name].add([{"name": name, "value": metrics.get(key)}])
        self.temperature_chart.add(metrics.get("temperatures", []))
        sensors = [s["name"] + "  " + number(s.get("rpm"), " RPM", 0) for s in metrics.get("fans", [])]
        self.thermal_label.set_text("\n".join(sensors))
        self.thermal_label.set_visible(bool(sensors))
        batteries = []
        for battery in data["batteries"]:
            batteries.append(f"{battery['name']} · {battery.get('status', '')} · {number(battery.get('percent'), '%')}\n"
                + "健康度 " + number(battery.get("health_percent"), "%") + "  ·  功率 " + number(battery.get("power_w"), " W")
                + "  ·  循环 " + number(battery.get("cycles"), "", 0) + "\n容量 "
                + number(battery.get("energy_now_wh"), " Wh") + " / " + number(battery.get("energy_full_wh"), " Wh")
                + "  ·  设计容量 " + number(battery.get("energy_design_wh"), " Wh"))
        self.battery_label.set_text("\n\n".join(batteries) or "未检测到系统电池")
        signature = json.dumps(data["controls"], sort_keys=True)
        if signature != self.control_signature and not self.busy:
            self.update_controls(data["controls"])
            self.control_signature = signature
        self.inventory.clear()
        for item in data["inventory"]:
            self.inventory.append([str(item.get(k, "")) for k in ("category", "name", "details")])
        marks = {"ok": "正常", "warning": "需关注", "unknown": "暂无数据"}
        self.health_label.set_text("\n\n".join(f"[{marks.get(h['level'], h['level'])}] {h['name']}：{h['detail']}" for h in data["health"]))
        self.sample_status = "监控中 · 更新于 " + datetime.datetime.now().strftime("%H:%M:%S")
        if self.settings.values["record_battery"] and not self.smoke_test:
            try:
                self.history.add(data["batteries"])
            except OSError as exc:
                self.sample_status += " · 电池历史写入失败：" + str(exc)
        profile = data["controls"].get("profile", {})
        online = next((b.get("online") for b in data["batteries"] if b.get("online") is not None), None)
        if not self.busy and not self.smoke_test:
            target = self.automation.evaluate(self.settings.values, profile.get("value"), online, processes)
            if target:
                self.apply_control("profile", target, automatic=True)
        self.auto_status.set_text(self.automation.status)
        self.refresh_status()
        self.smoke_ok = True
        return False

    def update_controls(self, controls):
        for widget, handler in self.control_handlers:
            widget.disconnect(handler)
        self.control_handlers.clear()
        for child in self.control_rows.get_children():
            self.control_rows.remove(child)
        self.editors.clear()
        for name, state in controls.items():
            row = Gtk.Box(spacing=12)
            title = label(LABELS.get(name, name))
            title.set_size_request(170, -1)
            row.pack_start(title, False, False, 0)
            if state["available"] and name != "fan_dust":
                if state["choices"] == ["0", "1"]:
                    selector = Gtk.Switch(valign=Gtk.Align.CENTER, halign=Gtk.Align.END)
                    selector.set_active(state["value"] == "1")
                    handler = selector.connect("notify::active", lambda widget, _, n=name:
                        self.request_control(n, "1" if widget.get_active() else "0"))
                elif name == "profile" or len(state["choices"]) <= 4:
                    captions = dict(PROFILE_CHOICES) if name == "profile" else {}
                    selector = ChoiceButtons((value, captions.get(value, control_value(name, value)))
                                             for value in state["choices"])
                    selector.set_active_id(state["value"])
                    handler = selector.connect("changed", lambda widget, n=name:
                        self.request_control(n, widget.get_active_id()))
                else:
                    selector = Gtk.Button(label=control_value(name, state["value"]))
                    next_value = state["choices"][(state["choices"].index(state["value"]) + 1) % len(state["choices"])]
                    handler = selector.connect("clicked", lambda _, n=name, v=next_value: self.request_control(n, v))
                self.control_handlers.append((selector, handler))
                row.pack_start(selector, True, True, 0)
                self.editors[name] = (selector,)
                row.set_tooltip_text("当前：" + control_value(name, state["value"]))
            else:
                row.pack_start(label(state.get("reason") or "暂不可用"), True, True, 0)
            self.control_rows.pack_start(row, False, False, 0)
        self.control_rows.show_all()

    def request_control(self, name, value):
        if self.closed:
            return
        current = self.snapshot["controls"][name]["value"] if self.snapshot else None
        if current != value:
            self.apply_control(name, value)

    def set_controls_sensitive(self, sensitive):
        for widgets in self.editors.values():
            for widget in widgets:
                widget.set_sensitive(sensitive)

    def apply_control(self, name, value, automatic=False):
        if self.busy or value is None or self.smoke_test:
            return
        if not automatic:
            self.automation.pause()
        self.busy = True
        self.set_controls_sensitive(False)
        self.refresh_status()
        def work():
            try:
                actual = self.control_session.set_control(name, value)
                GLib.idle_add(self.control_done, name, actual, True, automatic)
            except Exception as exc:
                GLib.idle_add(self.control_done, name, str(exc), False, automatic)
        threading.Thread(target=work, daemon=True).start()

    def control_done(self, name, value, success, automatic):
        self.busy = False
        if automatic:
            self.automation.complete(value, success)
        if self.closed:
            return False
        self.control_signature = None
        self.set_controls_sensitive(True)
        self.feedback((LABELS.get(name, name) + "已设为" + control_value(name, value)) if success else "设置失败：" + value)
        self.poll()
        return False

    def inventory_visible(self, model, iterator, _):
        query = self.search.get_text().casefold()
        return query in " ".join(model[iterator]).casefold()

    def filter_inventory(self, _):
        self.filtered.refilter()

    def choose_file(self, title, default):
        dialog = Gtk.FileChooserDialog(title=title, parent=self, action=Gtk.FileChooserAction.SAVE)
        dialog.add_buttons("取消", Gtk.ResponseType.CANCEL, "导出", Gtk.ResponseType.OK)
        dialog.set_current_name(default)
        dialog.set_do_overwrite_confirmation(True)
        response = dialog.run()
        path = dialog.get_filename() if response == Gtk.ResponseType.OK else None
        dialog.destroy()
        return path

    def export(self, _):
        if self.snapshot is None:
            self.feedback("尚无可导出的报告")
            return
        path = self.choose_file("导出硬件报告（.json 或 .html）", "xiaoxin-report.html")
        if path:
            try:
                export_report(self.snapshot, path)
                self.feedback("报告已导出：" + path)
            except (OSError, ValueError) as exc:
                self.feedback("导出失败：" + str(exc))

    def export_history(self, _):
        path = self.choose_file("导出电池历史", "xiaoxin-battery.csv")
        if path:
            try:
                self.history.export(path)
                self.feedback("电池历史已导出：" + path)
            except (OSError, ValueError) as exc:
                self.feedback("导出失败：" + str(exc))

    def finish_smoke(self):
        display = Gdk.Display.get_default()
        chart_draws = self.temperature_chart.draw_count + sum(chart.draw_count for chart in self.resource_charts.values())
        self.smoke_ok = self.smoke_ok and chart_draws > 0
        print(json.dumps({"smoke_test": self.smoke_ok, "display": type(display).__name__,
                          "controls": list(self.editors), "chart_draws": chart_draws,
                          "resource_charts": list(self.resource_charts),
                          "scale_factor": self.get_scale_factor(),
                          "window_size": tuple(self.get_size())}, ensure_ascii=False), flush=True)
        self.destroy()
        return False

    def on_close(self, _):
        self.closed = True
        self.control_session.close()
        Gtk.main_quit()


def run(smoke_test=False):
    GLib.set_prgname("fancontroller")
    GLib.set_application_name(APP_NAME)
    Gdk.set_program_class("fancontroller")
    ok, _ = Gtk.init_check()
    if not ok:
        raise RuntimeError("无法连接图形会话；请在桌面终端启动，或使用 --status / --json")
    provider = Gtk.CssProvider()
    provider.load_from_data(b"headerbar.compact-titlebar {min-height:30px;padding:2px 6px;}"
                            b"headerbar.compact-titlebar button {min-height:24px;min-width:24px;padding:0 6px;margin:0;}"
                            b"headerbar.compact-titlebar .compact-tabs button {min-width:0;padding:0 10px;}"
                            b".card {border:1px solid alpha(@theme_fg_color,0.13);border-radius:10px;}"
                            b".section-title {font-weight:bold;font-size:1.15em;}"
                            b"progressbar progress {min-height:8px;}")
    Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
    window = Window(smoke_test)
    Gtk.main()
    return 0 if not smoke_test or window.smoke_ok else 1
