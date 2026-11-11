using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using FanController;
using FanController.Models;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--trigger-only"]) { Thread.Sleep(90000); return 0; }
        if (args.Length is < 3 or > 4 || args[0] != "--run") throw new ArgumentException("Use --run <repo> <report-directory> [comma-separated filter]");
        string repo = args[1], output = args[2];
        if (!FanController.Services.MonitorService.IsAdministrator()) throw new InvalidOperationException("Run hardware checks as administrator");
        repo = Path.GetFullPath(repo); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        using var mutex = new Mutex(false, "FanController.SingleInstance.v1");
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new InvalidOperationException("Close FanController before running live checks");
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var resources = XDocument.Load(Path.Combine(repo, "App.xaml")).Root!.Element(ns + "Application.Resources")!;
            var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x), resources.Elements());
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown,
                Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()) };
            // A plain Application avoids invoking the product App.OnStartup from an offscreen test.
            typeof(AppSettings).GetProperty("ConfigDirectoryOverride", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, Path.Combine(output, "isolated-settings"));
            typeof(App).GetProperty("LogFileOverride", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, Path.Combine(output, "application.log"));
            new AppSettings { OverlayEnabled = false, CloseToTray = true, RecordBatteryHistory = true,
                RyzenAdjPath = Path.Combine(repo, "tools", "ryzenadj", "ryzenadj.exe") }.Save();
            var suite = new HardwareSuite(repo, output, args.Length == 4 ? args[3] : null);
            application.Dispatcher.BeginInvoke(async () =>
            {
                int result = await suite.Run();
                application.Shutdown(result);
            }, DispatcherPriority.ContextIdle);
            return application.Run();
        }
        finally { mutex.ReleaseMutex(); }
    }
}
