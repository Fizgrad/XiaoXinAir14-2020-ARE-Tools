using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FanController.Services;

namespace FanController;

/// <summary>
/// 游戏内悬浮窗：透明、置顶、点击穿透、不抢焦点、不出现在 Alt-Tab。
/// 边框无边框窗口模式下有效；独占全屏游戏会绕过 DWM，无法覆盖（不注入钩子）。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);

    /// <summary>角落：0 左上 1 右上 2 左下 3 右下</summary>
    public int Corner { get; set; }

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, ex | WsExTransparent | WsExToolWindow);
    }

    public void ApplyPosition()
    {
        var wa = SystemParameters.WorkArea;
        const double m = 16;
        double w = ActualWidth > 0 ? ActualWidth : 260;
        double h = ActualHeight > 0 ? ActualHeight : 60;
        Left = (Corner % 2 == 0) ? wa.Left + m : wa.Right - w - m;
        Top = (Corner < 2) ? wa.Top + m : wa.Bottom - h - m;
    }

    public void Update(SensorSnapshot s)
    {
        OvCpu.Text = $"CPU {Temp(s.CpuTemp)} {Power(s.PackagePower)} {Clock(s.CpuClockMHz)} {Pct(s.CpuLoad)}";
        OvGpu.Text = $"GPU {Temp(s.GpuTemp)} {Power(s.GpuPowerW)} {Clock(s.GpuClockMHz)} {Pct(s.GpuLoad)}";
    }

    private static string Temp(float? v) => v == null ? "  --°C" : $"{v,4:0}°C";
    private static string Power(float? v) => v == null ? "  --.-W" : $"{v,5:0.0}W";
    private static string Clock(float? mhz) => mhz == null ? "  --.--GHz" : $"{mhz / 1000f,6:0.00}GHz";
    private static string Pct(float? v) => v == null ? "  --%" : $"{v,4:0}%";
}
