using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FanController.Models;
using FanController.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => Run(args).GetAwaiter().GetResult();
    private static async Task<int> Run(string[] args)
    {
        if (args is ["--startup"])
        {
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "scheduled-startup-marker.txt"), "Startup=True;Admin=" + admin);
            return 0;
        }
        if (args is ["--live-inventory", var reportDirectory])
        {
            Directory.CreateDirectory(reportDirectory);
            var live = new HardwareInventoryReader().Scan();
            HardwareReportExporter.Export(live, Path.Combine(reportDirectory, "hardware.json"));
            HardwareReportExporter.Export(live, Path.Combine(reportDirectory, "hardware.html"));
            Console.WriteLine(JsonSerializer.Serialize(new { Devices = live.Devices.Count, Categories = live.Devices.GroupBy(d => d.Category).Select(g => new { Name = g.Key, Count = g.Count() }), live.Checks, live.Issues }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Must(live.Devices.Any(d => d.Category == "处理器") && live.Devices.Any(d => d.Category == "内存") && live.Devices.Any(d => d.Category == "硬盘"), "Core hardware inventory missing");
            return 0;
        }
        if (args is ["--live-startup", var directory]) return await LiveStartup(directory);
        string isolated = Path.Combine(Path.GetTempPath(), "FanController-SystemChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated); AppSettings.ConfigDirectoryOverride = isolated;
        int passed = 0;
        void Check(string name, Action check) { check(); Console.WriteLine("PASS " + name); passed++; }
        async Task AsyncCheck(string name, Func<Task> check) { await check(); Console.WriteLine("PASS " + name); passed++; }
        try
        {
            Check("logon definition scopes user, elevation, batteries, timeout and escaped paths", () =>
            {
                string xml = StartupRegistrationService.BuildDefinition(@"C:\Program Files\A & B\FanController.exe", "S-1-5-21-123");
                var root = XDocument.Parse(xml); XNamespace n = root.Root!.Name.Namespace;
                Must(root.Descendants(n + "Command").Single().Value.Contains("A & B") && root.Descendants(n + "Arguments").Single().Value == "--startup", "Command line escaped incorrectly");
                Must(root.Descendants(n + "LogonType").Single().Value == "InteractiveToken" && root.Descendants(n + "RunLevel").Single().Value == "HighestAvailable", "Interactive elevation missing");
                Must(root.Descendants(n + "UserId").All(e => e.Value == "S-1-5-21-123") && root.Descendants(n + "DisallowStartIfOnBatteries").Single().Value == "false"
                    && root.Descendants(n + "StopIfGoingOnBatteries").Single().Value == "false" && root.Descendants(n + "ExecutionTimeLimit").Single().Value == "PT0S", "Logon restrictions wrong");
            });
            await AsyncCheck("enabling startup persists preset and verifies registration", async () =>
            {
                var store = new FakeTaskStore(); var service = Registration(store); var settings = new AppSettings();
                var result = await service.SaveAsync(settings, true, PowerPreset.Balanced);
                Must(result.Success && service.Read().Enabled && service.Read().MatchesApplication && AppSettings.Load().StartupPowerPreset == PowerPreset.Balanced, "Startup not saved");
            });
            await AsyncCheck("disabling startup removes task and retains chosen preset", async () =>
            {
                var store = new FakeTaskStore(); var service = Registration(store); var settings = new AppSettings();
                await service.SaveAsync(settings, true, PowerPreset.Quiet); var result = await service.SaveAsync(settings, false, PowerPreset.Quiet);
                Must(result.Success && store.Xml == null && !AppSettings.Load().StartWithWindows && settings.StartupPowerPreset == PowerPreset.Quiet, "Startup not removed");
            });
            await AsyncCheck("task write failure keeps saved preferences", async () =>
            {
                var store = new FakeTaskStore { FailWrite = true }; var settings = new AppSettings { StartupPowerPreset = PowerPreset.Quiet }; settings.Save();
                var result = await Registration(store).SaveAsync(settings, true, PowerPreset.Sustained);
                Must(!result.Success && !settings.StartWithWindows && settings.StartupPowerPreset == PowerPreset.Quiet && !AppSettings.Load().StartWithWindows && store.Xml == null, "Task failure applied configuration");
            });
            await AsyncCheck("configuration write failure restores prior task and preferences", async () =>
            {
                var store = new FakeTaskStore(); var service = Registration(store); var settings = new AppSettings(); await service.SaveAsync(settings, true, PowerPreset.Quiet);
                string previous = store.Xml!; string blocker = Path.Combine(isolated, "blocker"); File.WriteAllText(blocker, "file"); AppSettings.ConfigDirectoryOverride = Path.Combine(blocker, "invalid");
                try { var result = await service.SaveAsync(settings, false, PowerPreset.Sustained); Must(!result.Success && store.Xml == previous && settings.StartWithWindows && settings.StartupPowerPreset == PowerPreset.Quiet, "Failed save lost registration"); }
                finally { AppSettings.ConfigDirectoryOverride = isolated; }
            });
            await AsyncCheck("registration readback mismatch rolls back instead of claiming success", async () =>
            {
                var store = new FakeTaskStore { DropWrite = true }; var settings = new AppSettings();
                var result = await Registration(store).SaveAsync(settings, true, null); Must(!result.Success && !settings.StartWithWindows && store.Xml == null, "Missing task treated as saved");
            });
            await AsyncCheck("invalid startup preset never changes a task", async () =>
            {
                var store = new FakeTaskStore(); var result = await Registration(store).SaveAsync(new(), true, (PowerPreset)999); Must(!result.Success && store.Writes == 0, "Invalid mode wrote task");
            });
            Check("inventory preserves hardware fields and zero counters", () =>
            {
                var report = new HardwareInventoryReader(Fixture()).Scan();
                Must(report.Devices.Any(d => d.Category == "处理器" && d.Name == "Test CPU") && report.Devices.Any(d => d.Category == "内存"), "Inventory fields missing");
                var disk = report.Devices.First(d => d.Name == "Test SSD");
                Must(disk.Fields.Any(f => f.Name == "寿命消耗" && f.Value == "0 %") && disk.Fields.Any(f => f.Name == "不可纠正读错误" && f.Value == "--"), "Zero and missing data confused");
            });
            Check("missing storage health never inherits a generic OK result", () =>
            {
                var fixture = Fixture(); fixture.Rows.Remove("MSFT_PhysicalDisk"); var report = new HardwareInventoryReader(fixture).Scan();
                Must(report.Checks.Any(c => c.Name.Contains("存储健康") && c.Status == HardwareStatus.Unknown), "Unknown disk passed health");
            });
            Check("device error and disk space warnings remain separate", () =>
            {
                var fixture = Fixture(); fixture.Rows["Win32_PnPEntity"][0]["ConfigManagerErrorCode"] = 28; fixture.Rows["Win32_LogicalDisk"][0]["FreeSpace"] = 1024L;
                var report = new HardwareInventoryReader(fixture).Scan(); Must(report.Checks.Count(c => c.Status == HardwareStatus.Attention) >= 2 && report.Devices.Any(d => d.Category == "异常设备"), "Health warnings missing");
            });
            Check("disk matching refuses mismatched serial numbers", () => Must(!HardwareInventoryReader.MatchDisk(Row(("Index", 0), ("Size", 100L), ("SerialNumber", "A")), Row(("DeviceId", "0"), ("Size", 100L), ("SerialNumber", "B"))), "Health attached to wrong disk"));
            Check("partial provider failure preserves other categories and records source", () =>
            {
                var fixture = Fixture(); fixture.FailClass = "Win32_Processor"; var report = new HardwareInventoryReader(fixture).Scan();
                Must(report.Issues.Any(i => i.Source == "处理器") && report.Devices.Any(d => d.Category == "内存"), "One provider discarded report");
            });
            Check("cancelled inventory never starts provider queries", () =>
            {
                var fixture = Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { new HardwareInventoryReader(fixture).Scan(cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
                catch (OperationCanceledException) { Must(fixture.Queries == 0, "Cancelled scan probed hardware"); }
            });
            Check("HTML export escapes device content and JSON retains semantic status", () =>
            {
                var report = new HardwareInventoryReader(Fixture()).Scan(); report = report with { Computer = "<script>alert('x')</script>" };
                string html = HardwareReportExporter.Html(report), json = HardwareReportExporter.Json(report);
                Must(!html.Contains("<script>") && html.Contains("&lt;script&gt;") && html.Contains("charset='utf-8'"), "Unsafe HTML report");
                var restored = JsonSerializer.Deserialize<HardwareInventoryReport>(json, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                Must(restored?.Checks.Count == report.Checks.Count && restored.Devices.Count == report.Devices.Count, "JSON lost report");
            });
            Check("invalid persisted startup mode is normalized", () =>
            {
                var settings = new AppSettings { StartupPowerPreset = (PowerPreset)456 }; settings.Save(); Must(AppSettings.Load().StartupPowerPreset == null, "Invalid startup mode survived load");
            });
            Console.WriteLine($"{passed} startup settings and inventory checks passed; fake providers only."); return 0;
        }
        finally { AppSettings.ConfigDirectoryOverride = null; }
    }

    private static async Task<int> LiveStartup(string directory)
    {
        Directory.CreateDirectory(directory); AppSettings.ConfigDirectoryOverride = directory;
        string name = "FanController.TestStartup." + Guid.NewGuid().ToString("N"), executable = Environment.ProcessPath!;
        var store = new ScheduledTaskStore(); var service = new StartupRegistrationService(store, executable, WindowsIdentity.GetCurrent().User!.Value, name);
        string marker = Path.Combine(AppContext.BaseDirectory, "scheduled-startup-marker.txt"); if (File.Exists(marker)) File.Delete(marker);
        try
        {
            var settings = new AppSettings(); var result = await service.SaveAsync(settings, true, PowerPreset.Quiet);
            Must(result.Success && service.Read().Enabled && service.Read().MatchesApplication, result.Message);
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\"), task = folder.GetTask(name), running = task.Run(null);
            try
            {
                for (int i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(100);
                Must(File.Exists(marker) && File.ReadAllText(marker) == "Startup=True;Admin=True", "Scheduled process did not run interactively with elevation");
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(running); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(task); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(scheduler); }
            result = await service.SaveAsync(settings, false, PowerPreset.Quiet); Must(result.Success && store.Read(name) == null, "Test task not disabled");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS actual scheduled task create, XML readback, elevated --startup invocation and removal");
            Console.WriteLine("PASS actual scheduled task create, XML readback, elevated --startup invocation and removal"); return 0;
        }
        finally { store.Delete(name); AppSettings.ConfigDirectoryOverride = null; }
    }
    private static StartupRegistrationService Registration(FakeTaskStore store) => new(store, @"C:\Apps\FanController.exe", "S-1-5-21-123", "FanController.Test");
    private static void Must(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
    internal static FakeInventoryProbe Fixture() => new()
    {
        Rows = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Win32_Processor"] = [Row(("Name", "Test CPU"), ("NumberOfCores", 6), ("NumberOfLogicalProcessors", 12), ("Status", "OK"))],
            ["Win32_PhysicalMemory"] = [Row(("Manufacturer", "RAM"), ("Capacity", 16L * 1024 * 1024 * 1024), ("SMBIOSMemoryType", 26))],
            ["Win32_DiskDrive"] = [Row(("Index", 0), ("Model", "Test SSD"), ("SerialNumber", "DISK1"), ("Size", 512L * 1024 * 1024 * 1024), ("Status", "OK"))],
            ["MSFT_PhysicalDisk"] = [Row(("DeviceId", "0"), ("SerialNumber", "DISK1"), ("HealthStatus", 0), ("MediaType", 4), ("BusType", 17))],
            ["MSFT_StorageReliabilityCounter"] = [Row(("DeviceId", "0"), ("Wear", 0), ("Temperature", 35))],
            ["Win32_LogicalDisk"] = [Row(("DeviceID", "C:"), ("Size", 100L * 1024 * 1024 * 1024), ("FreeSpace", 50L * 1024 * 1024 * 1024))],
            ["Win32_PnPEntity"] = [Row(("Name", "Device"), ("DeviceID", "PCI\\TEST"), ("ConfigManagerErrorCode", 0))],
        }
    };
    internal sealed class FakeTaskStore : IStartupTaskStore
    {
        public string? Xml; public bool FailWrite, DropWrite; public int Writes;
        public string? Read(string name) => Xml;
        public void Write(string name, string definition) { Writes++; if (FailWrite) throw new IOException("Task write denied"); if (!DropWrite) Xml = definition; }
        public void Delete(string name) => Xml = null;
    }
    internal sealed class FakeInventoryProbe : IHardwareInventoryProbe
    {
        public Dictionary<string, List<Dictionary<string, object?>>> Rows = new(StringComparer.OrdinalIgnoreCase); public string? FailClass; public int Queries;
        public IReadOnlyList<Dictionary<string, object?>> Query(string scope, string query, CancellationToken cancellation)
        { Queries++; string name = Regex.Match(query, @"FROM\s+(\w+)", RegexOptions.IgnoreCase).Groups[1].Value; if (name == FailClass) throw new IOException("Provider unavailable"); return Rows.GetValueOrDefault(name) ?? new(); }
        public BatteryReading ReadBattery() => new() { Percent = 90, DesignWh = 45, FullChargeWh = 43, HealthPercent = 95, Cycles = 0 };
    }
}
