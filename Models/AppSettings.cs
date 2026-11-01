using System.IO;
using System.Text.Json;

namespace FanController.Models;

public class AppSettings
{
    /// <summary>传感器轮询间隔（毫秒）</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>历史曲线保留的采样点数</summary>
    public int HistoryPoints { get; set; } = 300;

    /// <summary>全速散热（除尘模式）最长持续时间（分钟），0 = 不限制</summary>
    public int MaxDustMinutes { get; set; } = 30;

    /// <summary>点窗口关闭按钮时：true = 隐藏到托盘继续运行，false = 直接退出</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>启用游戏内悬浮窗（热键 Ctrl+Alt+O 开关）</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>悬浮窗角落：0 左上 1 右上 2 左下 3 右下</summary>
    public int OverlayCorner { get; set; } = 0;

    /// <summary>仪表盘行顺序（自上而下），可在界面中调整</summary>
    public List<string> DashboardOrder { get; set; } = new()
    {
        "CpuTemp", "CpuPower", "GpuTemp", "GpuPower", "GpuClock", "GpuLoad",
        "CpuClock", "CpuLoad", "Battery", "SysInfo",
    };

    public string ConfigDir => Path.Combine(
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
                if (s != null) return s;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 保存失败不致命
        }
    }
}
