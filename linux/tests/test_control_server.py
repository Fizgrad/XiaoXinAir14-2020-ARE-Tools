import io
import json
import unittest

from fancontroller.control_server import MAX_LINE_BYTES, MAX_REQUEST_ID, serve


class FakeBackend:
    def __init__(self):
        self.calls = []

    def set_control(self, name, value):
        self.calls.append((name, value))
        if value == "fail":
            raise OSError("Hardware readback failed")
        return value


class Output(io.StringIO):
    def __init__(self):
        super().__init__()
        self.flush_count = 0

    def flush(self):
        self.flush_count += 1
        return super().flush()


def command(identifier=1, control="profile", value="balanced"):
    return {"id": identifier, "control": control, "value": value}


class ControlServerTests(unittest.TestCase):
    def run_server(self, requests):
        if isinstance(requests, bytes):
            payload = requests
        else:
            payload = "".join(json.dumps(request) + "\n" for request in requests).encode("utf-8")
        backend, output = FakeBackend(), Output()
        status = serve(backend, io.BytesIO(payload), output)
        responses = [json.loads(line) for line in output.getvalue().splitlines()]
        return status, backend, responses, output

    def test_eof_emits_ready_without_hardware_operations(self):
        status, backend, responses, output = self.run_server([])
        self.assertEqual(status, 0)
        self.assertEqual(backend.calls, [])
        self.assertEqual(responses, [{"type": "ready", "protocol": 1}])
        self.assertEqual(output.flush_count, 1)

    def test_ready_is_flushed_before_first_bounded_input_read(self):
        output, backend = Output(), FakeBackend()

        class Input:
            def readline(inner, limit):
                self.assertEqual(limit, MAX_LINE_BYTES + 1)
                self.assertEqual(output.flush_count, 1)
                self.assertEqual(json.loads(output.getvalue()), {"type": "ready", "protocol": 1})
                self.assertEqual(backend.calls, [])
                return b""

        self.assertEqual(serve(backend, Input(), output), 0)

    def test_multiple_commands_use_same_backend_and_flush_each_reply(self):
        status, backend, responses, output = self.run_server([
            command(1, "profile", "performance"), command(2, "conservation", "1"),
            command(MAX_REQUEST_ID, "keyboard_backlight", "0"),
        ])
        self.assertEqual(status, 0)
        self.assertEqual(backend.calls, [("profile", "performance"), ("conservation", "1"), ("keyboard_backlight", "0")])
        self.assertEqual(responses[1:], [
            {"id": 1, "success": True, "result": "performance"},
            {"id": 2, "success": True, "result": "1"},
            {"id": MAX_REQUEST_ID, "success": True, "result": "0"},
        ])
        self.assertEqual(output.flush_count, 4)

    def test_backend_error_reports_request_id_and_session_continues(self):
        status, backend, responses, _ = self.run_server([command(1, value="fail"), command(2, value="balanced")])
        self.assertEqual(status, 0)
        self.assertEqual(len(backend.calls), 2)
        self.assertEqual(responses[1], {"id": 1, "success": False, "error": "Hardware readback failed"})
        self.assertEqual(responses[2], {"id": 2, "success": True, "result": "balanced"})

    def assert_rejected(self, payload):
        status, backend, responses, _ = self.run_server(payload)
        self.assertEqual(status, 1)
        self.assertEqual(backend.calls, [])
        self.assertEqual(len(responses), 2)
        self.assertEqual(responses[1]["id"], None)
        self.assertIs(responses[1]["success"], False)
        self.assertTrue(responses[1]["error"])

    def test_invalid_id_including_bool_is_rejected(self):
        for identifier in (None, True, False, 0, -1, MAX_REQUEST_ID + 1, 1.0, "1", [], {}):
            with self.subTest(identifier=identifier):
                self.assert_rejected([command(identifier)])

    def test_exact_schema_and_field_types_are_required(self):
        malformed = [None, [], "request", {}, {"id": 1, "control": "profile"},
                     dict(command(), extra="ignored"), command(control=None), command(control=["profile"]),
                     command(value=1), command(value=True), command(value=None), command(value={}), command(value=[])]
        for request in malformed:
            with self.subTest(request=request):
                self.assert_rejected([request])

    def test_unknown_control_and_paths_are_rejected(self):
        for control in ("fan_dust", "../../etc/passwd", "/sys/firmware/acpi/platform_profile", "profile\x00", ""):
            with self.subTest(control=control):
                self.assert_rejected([command(control=control)])

    def test_malformed_json_utf8_and_duplicate_keys_are_rejected(self):
        for payload in (b"\n", b"{bad json}\n", b"\xff\n", b"{}{}\n",
                        b'{"id":1,"id":2,"control":"profile","value":"balanced"}\n'):
            with self.subTest(payload=payload):
                self.assert_rejected(payload)

    def test_oversized_line_is_rejected_without_consuming_following_commands(self):
        self.assert_rejected(b" " * (MAX_LINE_BYTES + 1) + b"\n" + json.dumps(command()).encode() + b"\n")

    def test_line_exactly_at_limit_is_accepted(self):
        payload = json.dumps(command()).encode()
        payload += b" " * (MAX_LINE_BYTES - len(payload) - 1) + b"\n"
        self.assertEqual(len(payload), MAX_LINE_BYTES)
        status, backend, responses, _ = self.run_server(payload)
        self.assertEqual(status, 0)
        self.assertEqual(backend.calls, [("profile", "balanced")])
        self.assertEqual(responses[1], {"id": 1, "success": True, "result": "balanced"})

    def test_deeply_nested_json_is_a_protocol_error(self):
        self.assert_rejected(b"[" * 1000 + b"0" + b"]" * 1000 + b"\n")

    def test_protocol_error_ends_session_before_queued_valid_command(self):
        self.assert_rejected([command(identifier=0), command(identifier=1)])

    def test_closed_response_pipe_exits_cleanly(self):
        class ClosedOutput:
            def write(self, value):
                raise BrokenPipeError()

        backend = FakeBackend()
        self.assertEqual(serve(backend, io.BytesIO(b""), ClosedOutput()), 0)
        self.assertEqual(backend.calls, [])


if __name__ == "__main__":
    unittest.main()
