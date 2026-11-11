using FanController.Services;

namespace FanController.Models;

public sealed class ApplicationRule
{
    public string ProcessName { get; set; } = "";
    public ThermalProfile Profile { get; set; } = ThermalProfile.ExtremePerformance;
    public string ModeName => ProfileLabels.Name(Profile);
}

public sealed class AutomationSettings
{
    public bool Enabled { get; set; }
    public bool SwitchOnPower { get; set; } = true;
    public ThermalProfile AcProfile { get; set; } = ThermalProfile.IntelligentCooling;
    public ThermalProfile BatteryProfile { get; set; } = ThermalProfile.BatterySaving;
    public int CooldownSeconds { get; set; } = 30;
    public int ManualPauseMinutes { get; set; } = 10;
    public List<ApplicationRule> Applications { get; set; } = new();

    public void Normalize()
    {
        CooldownSeconds = Math.Clamp(CooldownSeconds, 10, 600);
        ManualPauseMinutes = Math.Clamp(ManualPauseMinutes, 1, 120);
        if (!ProfileLabels.Valid(AcProfile)) AcProfile = ThermalProfile.IntelligentCooling;
        if (!ProfileLabels.Valid(BatteryProfile)) BatteryProfile = ThermalProfile.BatterySaving;
        Applications = (Applications ?? new()).Where(r => r != null && ProfileLabels.Valid(r.Profile))
            .Select(r => new ApplicationRule { ProcessName = NormalizeProcess(r.ProcessName), Profile = r.Profile })
            .Where(r => r.ProcessName.Length > 0).DistinctBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(50).ToList();
    }

    public static string NormalizeProcess(string? name)
    {
        name = (name ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length is > 0 and <= 100 && name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0
            ? name : "";
    }
}

public static class ProfileLabels
{
    public static bool Valid(ThermalProfile profile) => profile is ThermalProfile.BatterySaving
        or ThermalProfile.IntelligentCooling or ThermalProfile.ExtremePerformance;
    public static string Name(ThermalProfile profile) => profile switch
    {
        ThermalProfile.BatterySaving => "节能", ThermalProfile.IntelligentCooling => "智能散热",
        ThermalProfile.ExtremePerformance => "性能", _ => "未知",
    };
}
