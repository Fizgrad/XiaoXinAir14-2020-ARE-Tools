using System.IO;
using System.Threading;
using System.Windows;

namespace FanController;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "FanController.log");

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showWaiter;

    public static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("=== 启动 ===");

        // 单实例：已有实例运行时唤醒它并退出本进程（避免托盘出现第二个图标）
        _instanceMutex = new Mutex(true, "FanController.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            Log("检测到已有实例，唤醒后退出");
            try
            {
                using var existing = EventWaitHandle.OpenExisting("FanController.ShowWindow.v1");
                existing.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "FanController.ShowWindow.v1");
        _showWaiter = new Thread(WaitShowRequests) { IsBackground = true, Name = "ShowWaiter" };
        _showWaiter.Start();

        DispatcherUnhandledException += (s, ev) =>
        {
            Log("UI未处理异常: " + ev.Exception);
            MessageBox.Show("程序出错：" + ev.Exception.Message + "\n详情见 " + LogPath,
                "FanController", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            Log("致命异常: " + ev.ExceptionObject);
        base.OnStartup(e);
    }

    private void WaitShowRequests()
    {
        while (true)
        {
            try
            {
                _showEvent!.WaitOne();
                if (MainWindow is MainWindow mw)
                    Dispatcher.BeginInvoke(mw.ShowFromTray);
            }
            catch
            {
                return;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("=== 退出 code=" + e.ApplicationExitCode + " ===");
        try { _showEvent?.Dispose(); } catch { }
        try { _instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
