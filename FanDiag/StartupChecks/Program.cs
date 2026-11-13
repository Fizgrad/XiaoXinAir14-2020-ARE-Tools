using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace StartupChecks
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 4 && args[0] == "child")
            {
                string prefix = args[1], directory = args[2], mode = args[3];
                ChildState.Directory = directory;
                ChildState.Mode = mode;
                FanController.App.LogFileOverride = Path.Combine(directory, $"app-{Environment.ProcessId}.log");
                var app = new FanController.App(prefix);
                app.InitializeComponent();
                int result = app.Run();
                ChildState.Mark("process-exit", result.ToString());
                return result;
            }

            string checkDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "exploration", "startup-checks", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(checkDirectory);
            string instancePrefix = "FanController.StartupChecks." + Guid.NewGuid().ToString("N");
            var children = new List<Process>();
            try
            {
                var primary = StartChild(instancePrefix, checkDirectory, "primary");
                children.Add(primary);
                WaitFor(primary, Path.Combine(checkDirectory, $"ready-{primary.Id}.txt"));
                Require(File.ReadAllText(Path.Combine(checkDirectory, $"ready-{primary.Id}.txt")) == "MainWindowAssigned=True;Hidden=True",
                    "Primary window was not assigned before showing, or was not hidden");
                Console.WriteLine("PASS primary explicitly owns the main window and can hide with an overlay");

                // Retain the named object after the owner exits: a new process must acquire
                // the unowned mutex rather than reject it merely because the object exists.
                using var retainedMutex = Mutex.OpenExisting(instancePrefix + ".SingleInstance.v1");
                for (int wake = 1; wake <= 2; wake++)
                {
                    var secondary = StartChild(instancePrefix, checkDirectory, "secondary");
                    children.Add(secondary);
                    Require(secondary.WaitForExit(10000) && secondary.ExitCode == 0, "Secondary failed to exit cleanly");
                    Require(!File.Exists(Path.Combine(checkDirectory, $"window-created-{secondary.Id}.txt")),
                        "Secondary launch constructed a main window");
                    string marker = Path.Combine(checkDirectory, $"wake-{primary.Id}-{wake}.txt");
                    WaitFor(primary, marker);
                    Require(File.ReadAllText(marker) == "UIThread=True;Visible=True;MainWindowAssigned=True",
                        "Wake did not execute on the UI thread or restore the real main window");
                }
                Console.WriteLine("PASS repeated secondary launches restore the hidden window on its UI thread without creating duplicates");

                File.WriteAllText(Path.Combine(checkDirectory, "close-command.txt"), "close");
                Require(primary.WaitForExit(10000) && primary.ExitCode == 0, "Closing the main window left the overlay process running");
                Require(File.Exists(Path.Combine(checkDirectory, $"overlay-closed-{primary.Id}.txt")),
                    "Overlay was left open after main-window shutdown");
                Require(File.ReadAllText(Path.Combine(checkDirectory, $"before-close-{primary.Id}.txt")) == "OverlayVisible=True",
                    "Lifecycle check did not include a live overlay");
                Console.WriteLine("PASS closing the main window also exits with an open overlay");

                var restarted = StartChild(instancePrefix, checkDirectory, "restart");
                children.Add(restarted);
                Require(restarted.WaitForExit(10000) && restarted.ExitCode == 0, "Restart failed after main-window shutdown");
                Require(File.Exists(Path.Combine(checkDirectory, $"window-created-{restarted.Id}.txt")),
                    "Restart rejected an existing but unowned mutex");
                Console.WriteLine("PASS restart acquires an existing unowned mutex and creates a fresh main window");

                var explicitExit = StartChild(instancePrefix, checkDirectory, "explicit-exit");
                children.Add(explicitExit);
                Require(explicitExit.WaitForExit(10000) && explicitExit.ExitCode == 0, "Explicit application exit left its process running");
                Require(File.Exists(Path.Combine(checkDirectory, $"main-closed-{explicitExit.Id}.txt"))
                    && File.Exists(Path.Combine(checkDirectory, $"overlay-closed-{explicitExit.Id}.txt")),
                    "Explicit application exit did not close both main and overlay windows");
                Console.WriteLine("PASS explicit application exit closes the main window and live overlay");
                var automatic = StartChild(instancePrefix, checkDirectory, "startup-flag"); children.Add(automatic);
                Require(automatic.WaitForExit(10000) && automatic.ExitCode == 0, "Startup argument launch failed");
                Require(File.ReadAllText(Path.Combine(checkDirectory, $"startup-launch-{automatic.Id}.txt")) == "Startup=True;Visible=True", "Startup flag or visible main window was lost");
                Console.WriteLine("PASS logon startup argument is recognized and the main window is shown");
                Console.WriteLine("6 startup checks passed; all windows stayed offscreen and no hardware driver was opened.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("Check artifacts: " + checkDirectory);
                return 1;
            }
            finally
            {
                foreach (var child in children)
                {
                    // Only processes created by this harness are terminated on a failed check.
                    try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
                    child.Dispose();
                }
            }
        }

        private static Process StartChild(string prefix, string directory, string mode)
        {
            string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No process host");
            var start = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (string argument in new[] { "child", prefix, directory, mode }) start.ArgumentList.Add(argument);
            if (mode == "startup-flag") start.ArgumentList.Add("--startup");
            return Process.Start(start) ?? throw new InvalidOperationException("Failed to start check process");
        }

        private static void WaitFor(Process child, string marker)
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
            Require(File.Exists(marker), "Timed out waiting for " + Path.GetFileName(marker));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    internal static class ChildState
    {
        internal static string Directory = "";
        internal static string Mode = "";
        internal static void Mark(string name, string content = "") =>
            File.WriteAllText(Path.Combine(Directory, $"{name}-{Environment.ProcessId}.txt"), content);
    }
}

namespace FanController
{
    // The actual App startup code is linked into this project. Only the window and
    // hardware services are replaced; no real controller or user settings are used.
    public sealed class MainWindow : Window
    {
        private Window? _overlay;
        private DispatcherTimer? _commands;
        private int _wakeCount;

        public MainWindow()
        {
            StartupChecks.ChildState.Mark("window-created");
            Left = Top = -30000;
            Width = Height = 100;
            ShowInTaskbar = false;
            Title = "FanController startup check";
            Loaded += (_, _) =>
            {
                _overlay = new Window { Left = -30000, Top = -30000, Width = 50, Height = 50, ShowInTaskbar = false };
                _overlay.Closed += (_, _) => StartupChecks.ChildState.Mark("overlay-closed");
                _overlay.Show();
                if (StartupChecks.ChildState.Mode == "startup-flag")
                {
                    StartupChecks.ChildState.Mark("startup-launch", $"Startup={((App)Application.Current).IsStartupLaunch};Visible={IsVisible}");
                    Dispatcher.BeginInvoke(Close); return;
                }
                Hide();
                StartupChecks.ChildState.Mark("ready", $"MainWindowAssigned={ReferenceEquals(Application.Current.MainWindow, this)};Hidden={!IsVisible}");
                if (StartupChecks.ChildState.Mode == "restart")
                {
                    Dispatcher.BeginInvoke(Close);
                    return;
                }
                if (StartupChecks.ChildState.Mode == "explicit-exit")
                {
                    Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
                    return;
                }
                _commands = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) =>
                {
                    if (!File.Exists(Path.Combine(StartupChecks.ChildState.Directory, "close-command.txt"))) return;
                    _commands!.Stop();
                    StartupChecks.ChildState.Mark("before-close", $"OverlayVisible={_overlay.IsVisible}");
                    Close();
                }, Dispatcher);
                _commands.Start();
            };
            Closed += (_, _) =>
            {
                _commands?.Stop();
                StartupChecks.ChildState.Mark("main-closed");
            };
        }

        public void ShowFromTray()
        {
            bool uiThread = Dispatcher.CheckAccess();
            Show();
            WindowState = WindowState.Normal;
            _wakeCount++;
            File.WriteAllText(Path.Combine(StartupChecks.ChildState.Directory, $"wake-{Environment.ProcessId}-{_wakeCount}.txt"),
                $"UIThread={uiThread};Visible={IsVisible};MainWindowAssigned={ReferenceEquals(Application.Current.MainWindow, this)}");
            Dispatcher.BeginInvoke(Hide, DispatcherPriority.ApplicationIdle);
        }
    }
}

namespace FanController.Services
{
    internal sealed class StartupRegistrationService
    {
        public StartupRegistrationService(string executable) { }
        public void RemoveForInstallation() => throw new InvalidOperationException("Startup checks must not change logon tasks");
    }
    internal static class EnergyDrvFan
    {
        public static bool IsRunning => false;
        public static bool RestorePending => false;
        public static void Stop(string reason) => throw new InvalidOperationException("The startup checks must not control hardware");
    }
}
