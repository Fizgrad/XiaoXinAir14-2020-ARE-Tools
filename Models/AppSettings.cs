using System.IO;
using System.Text.Json;
using FanController.Services;

namespace FanController.Models;

public class AppSettings
{
    // In-process diagnostics can isolate settings without modifying the user's configuration.
    internal static string? ConfigDirectoryOverride { get; set; }
    /// <summary>传感器轮询间隔（毫秒）</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>历史曲线保留的采样点数</summary>
    public int HistoryPoints { get; set; } = 300;

    /// <summary>点窗口关闭按钮时：true = 隐藏到托盘继续运行，false = 直接退出</summary>
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public PowerPreset? StartupPowerPreset { get; set; }

    /// <summary>启用游戏内悬浮窗（热键 Ctrl+Alt+O 开关）</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>悬浮窗角落：0 左上 1 右上 2 左下 3 右下</summary>
    public int OverlayCorner { get; set; } = 0;

    public AutomationSettings Automation { get; set; } = new();
    public int BacklightIdleSeconds { get; set; } = 0;
    public bool RecordBatteryHistory { get; set; } = true;
    public string RyzenAdjPath { get; set; } = "";

    /// <summary>仪表盘行顺序（自上而下），可在界面中调整</summary>
    public List<string> DashboardOrder { get; set; } = new()
    {
        "CpuTemp", "CpuPower", "GpuTemp", "GpuPower", "GpuClock", "GpuLoad",
        "CpuClock", "CpuLoad", "Battery", "SysInfo",
    };

    public string ConfigDir => ConfigDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanController");

    public string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            var path = new AppSettings().ConfigPath;
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s != null)
                {
                    s.Automation ??= new();
                    s.Automation.Normalize();
                    s.DashboardOrder ??= new();
                    s.RyzenAdjPath ??= "";
                    s.BacklightIdleSeconds = Math.Clamp(s.BacklightIdleSeconds, 0, 3600);
                    if (s.StartupPowerPreset.HasValue && !Enum.IsDefined(s.StartupPowerPreset.Value)) s.StartupPowerPreset = null;
                    return s;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    public void Save() => TrySave(out _);

    public bool TrySave(out string error)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(ConfigDir);
            temporary = Path.Combine(ConfigDir, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(temporary, ConfigPath, true);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally { if (temporary != null) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } } }
    }
}
