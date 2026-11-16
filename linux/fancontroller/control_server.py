"""A bounded JSON-lines protocol for one authorized GUI helper process."""

import json


CONTROLS = frozenset(("profile", "conservation", "usb_charging", "fn_lock", "keyboard_backlight"))
MAX_LINE_BYTES = 2048
MAX_REQUEST_ID = 2**31 - 1


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate request field")
        result[key] = value
    return result


def _request(line):
    if len(line) > MAX_LINE_BYTES:
        raise ValueError("Request exceeds maximum line length")
    request = json.loads(line.decode("utf-8"), object_pairs_hook=_object)
    if not isinstance(request, dict) or request.keys() != {"id", "control", "value"}:
        raise ValueError("Request must contain exactly id, control, and value")
    if type(request["id"]) is not int or not 1 <= request["id"] <= MAX_REQUEST_ID:
        raise ValueError("Invalid request id")
    if not isinstance(request["control"], str) or request["control"] not in CONTROLS:
        raise ValueError("Unknown control")
    if not isinstance(request["value"], str):
        raise ValueError("Control value must be a string")
    return request


def serve(backend, input_binary, output_text) -> int:
    """Serve explicit controls over inherited pipes until the GUI closes input.

    The packaged helper verifies its effective UID and constructs the backend.
    No writes or hardware probes are initiated by starting this protocol.
    """
    def reply(message):
        output_text.write(json.dumps(message, separators=(",", ":")) + "\n")
        output_text.flush()

    try:
        reply({"type": "ready", "protocol": 1})
        while True:
            line = input_binary.readline(MAX_LINE_BYTES + 1)
            if line == b"":
                return 0
            try:
                request = _request(line)
            except (ValueError, RecursionError) as exc:
                reply({"id": None, "success": False, "error": str(exc)})
                return 1
            try:
                result = backend.set_control(request["control"], request["value"])
                if not isinstance(result, str):
                    raise TypeError("Control readback must be a string")
            except Exception as exc:
                reply({"id": request["id"], "success": False, "error": str(exc)})
            else:
                reply({"id": request["id"], "success": True, "result": result})
    except BrokenPipeError:
        # Closing the GUI also closes its response pipe; leave no helper behind.
        return 0
