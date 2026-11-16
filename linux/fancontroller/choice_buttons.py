"""Visible, directly selectable alternatives with a ComboBox-like value API."""

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import GObject, Gtk


class ChoiceButtons(Gtk.Box):
    __gsignals__ = {"changed": (GObject.SignalFlags.RUN_FIRST, None, ())}

    def __init__(self, choices):
        choices = list(choices)
        if not choices:
            raise ValueError("At least one choice is required")
        seen = set()
        for value, caption in choices:
            if not isinstance(value, str) or not value or value in seen:
                raise ValueError("Choice identifiers must be unique nonempty strings")
            if not isinstance(caption, str):
                raise ValueError("Choice captions must be strings")
            seen.add(value)

        super().__init__(orientation=Gtk.Orientation.HORIZONTAL, spacing=0)
        self.get_style_context().add_class("linked")
        self._buttons = {}
        self._handlers = []
        self._destroying = False
        self._active_id = choices[0][0]
        group = None
        for value, caption in choices:
            button = Gtk.RadioButton.new_with_label_from_widget(group, caption)
            button.set_mode(False)
            self.pack_start(button, True, True, 0)
            self._buttons[value] = button
            group = button
        self._buttons[self._active_id].set_active(True)
        # Group creation/initialization must not look like a user selection.
        for value, button in self._buttons.items():
            handler = button.connect("toggled", self._toggled, value)
            self._handlers.append((button, handler))
        self.connect("destroy", self._on_destroy)

    def get_active_id(self):
        return self._active_id

    def set_active_id(self, value):
        if not isinstance(value, str) or value not in self._buttons:
            raise ValueError("Unknown choice identifier")
        if not self._destroying and value != self._active_id:
            self._buttons[value].set_active(True)

    def _toggled(self, button, value):
        if self._destroying or not button.get_active() or value == self._active_id:
            return
        self._active_id = value
        self.emit("changed")

    def _on_destroy(self, _):
        self._destroying = True
        for button, handler in self._handlers:
            button.disconnect(handler)
        self._handlers.clear()
