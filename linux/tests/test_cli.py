import contextlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

from fancontroller import cli


class CliTests(unittest.TestCase):
    def setUp(self):
        backend = Mock()
        backend.controls.return_value = {
            "profile": dict(available=True, choices=["low-power", "balanced", "performance"]),
            "conservation": dict(available=False, reason="not supported")}
        backend.snapshot.return_value = {"identity": {}, "controls": {}, "health": []}
        self.backend = backend
        self.backend_patch = patch.object(cli, "Backend", return_value=backend)
        self.backend_patch.start()
        self.addCleanup(self.backend_patch.stop)
        self.run_patch = patch.object(cli.subprocess, "run")
        self.run = self.run_patch.start()
        self.addCleanup(self.run_patch.stop)

    def test_invalid_name_values_and_unsupported_control_never_authorize(self):
        for name, value in (("/tmp/file", "1"), ("profile", "balanced\nperformance"),
                            ("profile", "$(id)"), ("profile", None), ("conservation", "1")):
            with self.assertRaises(ValueError):
                cli.change_control(name, value)
        self.run.assert_not_called()

    def test_valid_request_invokes_fixed_helper_without_shell(self):
        self.run.return_value = subprocess.CompletedProcess([], 0, '{"success":true,"result":"balanced"}', "")
        with patch.object(Path, "is_file", return_value=True), patch.object(cli.shutil, "which", return_value="/usr/bin/pkexec"):
            self.assertEqual(cli.change_control("profile", "balanced"), "balanced")
        self.assertEqual(self.run.call_args.args[0], ["/usr/bin/pkexec", cli.HELPER, "profile", "balanced"])
        self.assertNotIn("shell", self.run.call_args.kwargs)

    def test_cancelled_authorization_returns_clear_failure(self):
        for code in (126, 127):
            self.run.return_value = subprocess.CompletedProcess([], code, "", "")
            with patch.object(Path, "is_file", return_value=True), patch.object(cli.shutil, "which", return_value="/usr/bin/pkexec"):
                with self.assertRaisesRegex(RuntimeError, "授权"):
                    cli.change_control("profile", "balanced")

    def test_invalid_helper_response_fails_closed(self):
        for response in ("garbage", "[]", "{}", '{"success":true}',
                         '{"success":true,"result":"unexpected"}',
                         '{"success":true,"result":"low-power"}',
                         '{"success":"false","result":"balanced"}'):
            self.run.return_value = subprocess.CompletedProcess([], 0, response, "")
            with patch.object(Path, "is_file", return_value=True), patch.object(cli.shutil, "which", return_value="/usr/bin/pkexec"):
                with self.assertRaises(RuntimeError):
                    cli.change_control("profile", "balanced")

    def test_headless_json_never_loads_gui_or_authorizes(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(["--json"]), 0)
        self.assertEqual(json.loads(output.getvalue()), self.backend.snapshot.return_value)
        self.run.assert_not_called()

    def test_invalid_cli_set_is_reported_without_authorization(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cli.main(["--set", "profile", "invalid"]), 1)
        self.run.assert_not_called()

    def test_html_report_escapes_device_strings(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "report.html"
            cli.export_report({"device": '<script>alert("x")</script>'}, destination)
            report = destination.read_text()
            self.assertNotIn("<script>", report)
            self.assertIn("&lt;script&gt;", report)
            json_path = Path(directory) / "report.json"
            cli.export_report({"temperature": None}, json_path)
            self.assertEqual(json.loads(json_path.read_text()), {"temperature": None})

    def test_report_rejects_invalid_extension_before_writing(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "report.txt"
            with self.assertRaises(ValueError):
                cli.export_report({}, destination)
            self.assertFalse(destination.exists())


if __name__ == "__main__":
    unittest.main()
