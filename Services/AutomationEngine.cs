using FanController.Models;

namespace FanController.Services;

public sealed record ProfileDecision(ThermalProfile Profile, string Reason);

/// <summary>Pure state machine: a decision is committed only after firmware readback succeeds.</summary>
public sealed class AutomationEngine
{
    private readonly object _gate = new();
    private bool _enabled, _ownsProfile;
    private ThermalProfile _baseline, _lastObserved, _lastApplied, _candidate;
    private DateTimeOffset _candidateSince, _lastAttempt = DateTimeOffset.MinValue, _pauseUntil;
    public string Status { get; private set; } = "自动场景未启用";

    public void Pause(DateTimeOffset now, int minutes)
    {
        lock (_gate) { _pauseUntil = now.AddMinutes(minutes); _ownsProfile = false; _candidate = ThermalProfile.Unknown;
            if (ProfileLabels.Valid(_lastObserved)) _baseline = _lastObserved;
            Status = $"手动优先，暂停自动切换至 {_pauseUntil:HH:mm:ss}"; }
    }
    public void Resume() { lock (_gate) { _pauseUntil = DateTimeOffset.MinValue; _candidate = ThermalProfile.Unknown; _lastAttempt = DateTimeOffset.MinValue; } }

    public ProfileDecision? Evaluate(AutomationSettings settings, ThermalProfile current, bool? online,
        IReadOnlySet<string>? processes, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!ProfileLabels.Valid(current)) { Status = "等待有效的 Fn+Q 模式回读"; return null; }
            if (!settings.Enabled)
            {
                _enabled = false;
                if (_ownsProfile && current == _lastApplied && current != _baseline && ProfileLabels.Valid(_baseline))
                {
                    if ((now - _lastAttempt).TotalSeconds < settings.CooldownSeconds) { Status = "已停用，等待切换间隔后恢复原模式"; return null; }
                    _lastAttempt = now; return new(_baseline, "关闭自动场景，恢复启用前模式");
                }
                _ownsProfile = false; Status = "自动场景未启用"; return null;
            }
            if (!_enabled)
            {
                _enabled = true; _baseline = current; _lastObserved = current; _ownsProfile = false;
                _candidate = ThermalProfile.Unknown; _lastAttempt = DateTimeOffset.MinValue;
            }
            if (current != _lastObserved)
            {
                Pause(now, settings.ManualPauseMinutes);
                _baseline = current;
            }
            _lastObserved = current;
            if (now < _pauseUntil) { Status = $"手动优先，暂停自动切换至 {_pauseUntil:HH:mm:ss}"; return null; }
            if (settings.Applications.Count > 0 && processes == null) { Status = "进程列表读取失败，保留当前模式"; return null; }
            var app = settings.Applications.FirstOrDefault(r => processes?.Contains(r.ProcessName) == true);
            ThermalProfile desired;
            string reason;
            if (app != null) { desired = app.Profile; reason = "应用：" + app.ProcessName + ".exe"; }
            else if (settings.SwitchOnPower)
            {
                if (online == null) { Status = "电源状态未知，保留当前模式"; return null; }
                desired = online.Value ? settings.AcProfile : settings.BatteryProfile;
                reason = online.Value ? "接通电源" : "使用电池";
            }
            else { desired = _baseline; reason = "应用已退出，恢复原模式"; }
            if (desired == current) { _candidate = ThermalProfile.Unknown; Status = reason + " · " + ProfileLabels.Name(current); return null; }
            if (_candidate != desired) { _candidate = desired; _candidateSince = now; }
            if ((now - _candidateSince).TotalSeconds < 3) { Status = "正在确认场景：" + reason; return null; }
            if ((now - _lastAttempt).TotalSeconds < settings.CooldownSeconds) { Status = "等待切换间隔：" + reason; return null; }
            _lastAttempt = now;
            return new(desired, reason);
        }
    }

    public void Complete(ProfileDecision decision, bool success, string message)
    {
        lock (_gate)
        {
            if (success) { _lastApplied = _lastObserved = decision.Profile; _ownsProfile = decision.Profile != _baseline; _candidate = ThermalProfile.Unknown; }
            Status = success ? decision.Reason + " → " + ProfileLabels.Name(decision.Profile) : "自动切换失败：" + message;
        }
    }
}
