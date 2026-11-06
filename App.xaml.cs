using System.IO;
using System.Threading;
using System.Windows;

namespace FanController;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "FanController.log");

    internal static string? LogFileOverride { get; set; }
    private readonly string _instancePrefix;
    private readonly ManualResetEvent _stopping = new(false);
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showWaiter;
    private volatile bool _exiting;

    public App() : this("FanController") { }

    internal App(string instancePrefix) => _instancePrefix = instancePrefix;

    public static void Log(string msg)
    {
        try { File.AppendAllText(LogFileOverride ?? LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [PID {Environment.ProcessId}] {msg}\n"); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("=== 启动 === " + AppContext.BaseDirectory);

        DispatcherUnhandledException += (s, ev) =>
        {
            Log("UI未处理异常: " + ev.Exception);
            MessageBox.Show("程序出错：" + ev.Exception.Message + "\n详情见 " + LogPath,
                "FanController", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            Log("致命异常: " + ev.ExceptionObject);

        try
        {
            // Acquire ownership rather than relying on whether the named object already exists.
            _instanceMutex = new Mutex(false, _instancePrefix + ".SingleInstance.v1");
            try { _ownsInstanceMutex = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstanceMutex = true; }
            if (!_ownsInstanceMutex)
            {
                Log("检测到已有实例，唤醒后退出（不创建窗口）");
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(_instancePrefix + ".ShowWindow.v1");
                    existing.Set();
                }
                catch (Exception ex) { Log("唤醒已有实例失败: " + ex.Message); }
                Shutdown();
                return;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _instancePrefix + ".ShowWindow.v1");
            _showWaiter = new Thread(WaitShowRequests) { IsBackground = true, Name = "ShowWaiter" };
            _showWaiter.Start();
            base.OnStartup(e);

            // No StartupUri: duplicate launches must never construct another window or overlay.
            MainWindow = new MainWindow();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            Log("启动失败: " + ex);
            MessageBox.Show("启动失败：" + ex.Message + "\n详情见 " + LogPath,
                "FanController", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void WaitShowRequests()
    {
        var signals = new WaitHandle[] { _stopping, _showEvent! };
        try
        {
            while (WaitHandle.WaitAny(signals) == 1)
            {
                if (_exiting || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() =>
                {
                    // Application.MainWindow itself is bound to the UI thread.
                    if (!_exiting && MainWindow is MainWindow mw)
                    {
                        mw.ShowFromTray();
                        Log("已唤醒主窗口");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            if (!_exiting) Log("窗口唤醒线程异常: " + ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _stopping.Set();
        Log("=== 退出 code=" + e.ApplicationExitCode + " ===");
        try
        {
            if (Services.EnergyDrvFan.IsRunning || Services.EnergyDrvFan.RestorePending)
                Services.EnergyDrvFan.Stop("程序退出");
        }
        catch (Exception ex) { Log("退出时恢复风扇失败: " + ex.Message); }
        try { _showWaiter?.Join(500); } catch { }
        try { _showEvent?.Dispose(); } catch { }
        _stopping.Dispose();
        if (_ownsInstanceMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
