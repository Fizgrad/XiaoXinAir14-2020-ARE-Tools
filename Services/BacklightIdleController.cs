namespace FanController.Services;

public sealed class BacklightIdleController
{
    public bool OwnsOff { get; private set; }
    private bool _suppressed;
    private double _lastIdle;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _attemptAt;
    public void ManualChange() { OwnsOff = false; _suppressed = true; }
    public bool? Evaluate(int timeout, double idleSeconds, bool? lightOn, DateTimeOffset now)
    {
        if (idleSeconds < _lastIdle) _suppressed = false;
        _lastIdle = idleSeconds;
        if (lightOn == null) return null;
        if (OwnsOff && lightOn == true) { OwnsOff = false; _suppressed = true; }
        bool? desired = OwnsOff && (timeout == 0 || idleSeconds < timeout) ? true
            : !OwnsOff && !_suppressed && timeout > 0 && idleSeconds >= timeout && lightOn == true ? false : null;
        if (desired.HasValue && (now - _lastAttempt).TotalSeconds >= 30) { _attemptAt = now; return desired; }
        return null;
    }
    public void Complete(bool target, bool success)
    { if (success) { OwnsOff = !target; _lastAttempt = DateTimeOffset.MinValue; } else _lastAttempt = _attemptAt; }
}
