using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FanController;
using FanController.Models;
using FanController.Services;
using FanController.Views;
using FanController.Controls;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // No Application.Run, Start(), native probes, or hardware calls. Synthetic data only.
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = XDocument.Load(Path.Combine(repo, "App.xaml")).Root!.Element(ns + "Application.Resources")!;
        var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x), resources.Elements());
        // A product App queues OnStartup, even without Run(). Use a plain Application for layout checks.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown,
            Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()) };
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "exploration", "feature-ui"));
        Directory.CreateDirectory(directory);
        typeof(AppSettings).GetProperty("ConfigDirectoryOverride", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, Path.Combine(directory, "isolated-settings"));
        var checkView = new DeviceCenterView(); var checkSettings = new AppSettings { RecordBatteryHistory = false };
        checkView.Initialize(checkSettings, new ThermalProfileService(), () => { });
        CheckFeedback(checkView, checkSettings, directory);
        checkView.Stop();
        foreach (var (width, height) in new[] { (984, 580), (1124, 640) })
        {
            var view = new DeviceCenterView();
            var settings = new AppSettings { RecordBatteryHistory = false };
            settings.Automation.Applications.Add(new() { ProcessName = "devenv" });
            var profiles = new ThermalProfileService();
            typeof(ThermalProfileService).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(profiles, ThermalProfile.IntelligentCooling);
            view.Initialize(settings, profiles, () => { });
            view.OnSnapshot(new SensorSnapshot { Battery = new BatteryReading { Percent = 83, PowerW = 8.4f, DesignWh = 44.5f,
                FullChargeWh = 42.79f, RemainingWh = 35.6f, HealthPercent = 96.2f, Cycles = 61, PowerOnline = false, Message = "界面检查示例数据" } });
            var service = (LenovoDeviceService)typeof(DeviceCenterView).GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            typeof(LenovoDeviceService).GetProperty("State")!.SetValue(service, new LenovoDeviceState(0x100240, 0xCE70, "界面检查示例状态"));
            var power = (PowerTuningService)typeof(DeviceCenterView).GetField("_power", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            typeof(PowerTuningService).GetProperty("Current")!.SetValue(power, new PowerLimits(15, 12, 70));
            typeof(PowerTuningService).GetProperty("Original")!.SetValue(power, new PowerLimits(48, 37.5f, 100));
            typeof(PowerTuningService).GetProperty("Target")!.SetValue(power, new PowerLimits(15, 12, 70));
            typeof(DeviceCenterView).GetField("_deviceReadAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, DateTimeOffset.Now);
            typeof(DeviceCenterView).GetField("_powerReadAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, DateTimeOffset.Now);
            typeof(DeviceCenterView).GetMethod("PaintState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            typeof(DeviceCenterView).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object[] { false });
            var banner = new FeedbackBanner { Margin = new Thickness(12, 8, 12, 0) }; view.FeedbackPublished += banner.Show;
            Publish(view, FeedbackKind.Success, "安静办公已应用并确认", "短时 15 W · 持续 12 W · 温度上限 70°C；参数回读一致。（界面检查示例）");
            var panel = new DockPanel(); DockPanel.SetDock(banner, Dock.Top); panel.Children.Add(banner); panel.Children.Add(view);
            var root = new Border { Background = (Brush)app.Resources["BgBrush"], Child = panel, Width = width, Height = height };
            var window = new Window { Content = root, Width = width, Height = height, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
            window.Show();
            var tabs = (TabControl)view.FindName("FeatureTabs");
            var controls = new[] { new[] { "ChargeNormal", "ChargeConservation", "ChargeRapid", "BatteryHealth" },
                new[] { "AutomationEnabled", "AcProfile", "BatteryProfile", "ApplicationRules" },
                new[] { "FnLock", "UsbAc", "UsbBattery", "KeyboardLight", "BacklightTimeout" },
                new[] { "RyzenAdjPath", "PowerCurrent", "PresetControls", "RestorePower" } };
            for (int tab = 0; tab < tabs.Items.Count; tab++)
            {
                tabs.SelectedIndex = tab;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                foreach (var gridName in new[] { "ApplicationRules", "SleepHistory" })
                {
                    var grid = (DataGrid)view.FindName(gridName);
                    if (grid.IsVisible && grid.Columns.Any(c => c.ActualWidth < 50))
                        throw new InvalidOperationException(gridName + " has collapsed columns: " + grid.ActualWidth + " / " + string.Join(",", grid.Columns.Select(c => c.ActualWidth)));
                }
                foreach (string name in controls[tab])
                {
                    var control = (FrameworkElement)view.FindName(name);
                    var p = control.TranslatePoint(new Point(), root);
                    if (control.ActualWidth <= 0 || control.ActualHeight <= 0 || p.X < 0 || p.X + control.ActualWidth > width + 1)
                        throw new InvalidOperationException($"{name} clipped at {width}: {p} {control.ActualWidth}x{control.ActualHeight}");
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"tab-{tab}-{width}.png")); encoder.Save(output);
                Console.WriteLine($"PASS tab {tab} at {width}x{height}");
            }
            view.Stop();
            window.Close();
        }
        CheckWindowFrames(repo, directory, app);
        Console.WriteLine("8 feature-page layouts, 4 window layouts and 6 feedback checks passed; no hardware calls.");
        app.Shutdown(); return 0;
    }

    private static void CheckFeedback(DeviceCenterView view, AppSettings settings, string directory)
    {
        T Control<T>(string name) => (T)view.FindName(name);
        void Must(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Click(string content) => Buttons(view).First(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var banner = new FeedbackBanner(); view.FeedbackPublished += banner.Show;
        Publish(view, FeedbackKind.Success, "模式已确认", "当前生效值已回读");
        var first = view.LastFeedback;
        typeof(DeviceCenterView).GetMethod("PaintState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        Must(ReferenceEquals(first, view.LastFeedback) && banner.History.Count == 1, "Background rendering overwrote manual feedback");
        Console.WriteLine("PASS background refresh preserves result and time");

        Control<CheckBox>("AutomationEnabled").IsChecked = true;
        Must(!settings.Automation.Enabled && Control<TextBlock>("AutomationDraftState").Text.Contains("未保存"), "Draft changed active automation");
        Click("保存自动场景");
        Must(settings.Automation.Enabled && AppSettings.Load().Automation.Enabled && view.LastFeedback?.Kind == FeedbackKind.Success,
            "Saved automation did not confirm persisted state");
        Console.WriteLine("PASS unsaved edits and active configuration remain distinct");

        var property = typeof(AppSettings).GetProperty("ConfigDirectoryOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? original = property.GetValue(null); string blocker = Path.Combine(directory, "settings-blocker"); File.WriteAllText(blocker, "file");
        property.SetValue(null, Path.Combine(blocker, "cannot-create-directory"));
        try
        {
            Control<CheckBox>("AutomationEnabled").IsChecked = false; Click("保存自动场景");
            Must(settings.Automation.Enabled && view.LastFeedback?.Kind == FeedbackKind.Error
                && Control<TextBlock>("AutomationDraftState").Text.Contains("未保存"), "Save failure was shown as success or applied draft");
        }
        finally { property.SetValue(null, original); }
        Must(AppSettings.Load().Automation.Enabled, "Failed save overwrote existing settings");
        Console.WriteLine("PASS save failure keeps active settings and unsaved draft");

        Control<ComboBox>("BacklightTimeout").SelectedValue = 30;
        Must(settings.BacklightIdleSeconds == 0 && Control<TextBlock>("BacklightDraftState").Text.Contains("未保存"), "Backlight draft already applied");
        Control<Button>("SaveBacklight").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Must(settings.BacklightIdleSeconds == 30 && view.LastFeedback?.Kind == FeedbackKind.Success, "Backlight save feedback incorrect");
        Console.WriteLine("PASS backlight draft and saved policy are explicit");

        Publish(view, FeedbackKind.Pending, "处理中", "正在回读");
        Must(((ProgressBar)banner.FindName("Progress")).Visibility == Visibility.Visible, "Pending progress missing");
        Publish(view, FeedbackKind.Error, "设置未确认", "恢复已确认；当前 48 W / 37.5 W / 100°C");
        Must(banner.Current?.Kind == FeedbackKind.Error && ((ProgressBar)banner.FindName("Progress")).Visibility == Visibility.Collapsed
            && banner.History[0].Detail.Contains("恢复已确认"), "Failure details or history lost");
        Console.WriteLine("PASS pending, failure, recovery detail and history stay visible");

        var power = (PowerTuningService)typeof(DeviceCenterView).GetField("_power", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        typeof(PowerTuningService).GetProperty("Current")!.SetValue(power, new PowerLimits(20, 15, 75));
        typeof(PowerTuningService).GetProperty("Target")!.SetValue(power, new PowerLimits(15, 12, 70));
        typeof(DeviceCenterView).GetMethod("PaintState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        Must(Control<TextBlock>("PowerStatus").Text.Contains("偏离") && !Control<TextBlock>("QuietTitle").Text.Contains("当前")
            && banner.Current?.Kind == FeedbackKind.Error, "External override did not clear active preset or overwrote last result");
        Console.WriteLine("PASS current power override is distinct from last action");
    }

    private static void Publish(DeviceCenterView view, FeedbackKind kind, string title, string detail) =>
        typeof(DeviceCenterView).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object[] { kind, title, detail });

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) { if (d is Button b) yield return b; foreach (var nested in Buttons(d)) yield return nested; }
    }

    private static void CheckWindowFrames(string repo, string directory, Application app)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (width, height) in new[] { (1040, 720), (1180, 780) })
        foreach (int tab in new[] { 1, 2 })
        {
            var source = XDocument.Load(Path.Combine(repo, "MainWindow.xaml"));
            source.Root!.Attribute(x + "Class")!.Remove(); source.Root.Attribute("Icon")!.Remove();
            foreach (var attribute in source.Descendants().Attributes().Where(a => a.Name.LocalName is "Click" or "Checked" or "Unchecked" or "SelectionChanged").ToArray()) attribute.Remove();
            var namespaces = source.Root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value.StartsWith("clr-namespace:"))
                .ToDictionary(a => a.Value, a => a.Value + ";assembly=FanController");
            foreach (var element in source.Descendants())
                if (namespaces.TryGetValue(element.Name.NamespaceName, out string? resolved)) element.Name = XName.Get(element.Name.LocalName, resolved);
            foreach (var attribute in source.Root.Attributes().Where(a => a.IsNamespaceDeclaration && namespaces.ContainsKey(a.Value)).ToArray())
                attribute.Value = namespaces[attribute.Value];
            var window = (Window)XamlReader.Parse(source.ToString());
            window.Left = window.Top = -20000; window.ShowActivated = window.ShowInTaskbar = false;
            window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
            window.Width = width; window.Height = height;
            var banner = (FeedbackBanner)window.FindName("ActionFeedback");
            banner.Show(new(FeedbackKind.Success, "散热模式已切换为智能散热", "当前模式已通过固件回读确认。（界面检查示例）", DateTimeOffset.Now));
            ((TabControl)window.FindName("MainTabs")).SelectedIndex = tab;
            if (tab == 2) ((TabControl)((DeviceCenterView)window.FindName("ControlCenter")).FindName("FeatureTabs")).SelectedIndex = 2;
            window.Show(); window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var root = (FrameworkElement)window.Content; root.UpdateLayout();
            var p = banner.TranslatePoint(new Point(), root);
            if (p.X < 0 || banner.ActualWidth <= 0 || p.X + banner.ActualWidth > root.ActualWidth + 1)
                throw new InvalidOperationException("Window feedback banner clipped");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(directory, $"window-{tab}-{width}.png"))) encoder.Save(output);
            Console.WriteLine($"PASS window tab {tab} at {width}x{height}");
            window.Close();
        }
    }
}
