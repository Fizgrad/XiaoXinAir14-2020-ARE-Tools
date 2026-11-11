using System.Globalization;
using System.Text.Json;
using FanController.Models;
using FanController.Services;

if (args is ["--probe-readonly"])
{
    var devices = new LenovoDeviceService(); var state = devices.Refresh();
    var battery = BatteryReader.Read(); var profiles = new ThermalProfileService(); profiles.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new { state.BatteryMask, state.DeviceMask, state.Charging, state.RapidSupported,
        FnLock = state.Value(DeviceSwitch.FnLock), UsbAc = state.Value(DeviceSwitch.UsbAc), UsbBattery = state.Value(DeviceSwitch.UsbBattery),
        Backlight = state.Value(DeviceSwitch.Backlight), state.Message,
        Battery = new { battery.Percent, battery.PowerW, battery.DesignWh, battery.FullChargeWh, battery.RemainingWh, battery.HealthPercent, battery.Cycles, battery.PowerOnline, battery.Message },
        Profile = profiles.CurrentProfile, ProfileMessage = profiles.LastMessage }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args is ["--power-readonly", var executable])
{
    var power = new PowerTuningService(Path.GetFullPath(executable));
    if (!power.Refresh()) throw new InvalidOperationException(power.Message);
    Console.WriteLine(power.Current); Console.WriteLine(power.Message); return;
}
if (args is ["--power-verify-current", var verifyExecutable])
{
    var power = new PowerTuningService(Path.GetFullPath(verifyExecutable));
    if (!power.Refresh()) throw new InvalidOperationException(power.Message);
    var original = power.Current!;
    Console.WriteLine("Before: " + original);
    // Deliberately writes only the three values that were just read, with --info initialization.
    power.WriteAndVerify(original);
    Console.WriteLine("Verified current limits unchanged: " + power.Current);
    return;
}
if (args is ["--power-test-quiet", var testExecutable, var evidencePath])
{
    Environment.ExitCode = LivePowerCheck.Run(Path.GetFullPath(testExecutable), Path.GetFullPath(evidencePath));
    return;
}
if (args is ["--power-test-other-presets", var presetsExecutable, var presetsEvidencePath])
{
    Environment.ExitCode = LivePowerCheck.VerifyAdditionalPresets(Path.GetFullPath(presetsExecutable), Path.GetFullPath(presetsEvidencePath));
    return;
}
if (args.Length > 0) throw new ArgumentException("Unknown check mode");

var checks = new (string Name, Action Run)[]
{
    ("unverified machines perform no device calls", Unsupported),
    ("refresh is read-only and capability-gates switches", ReadOnly),
    ("charging modes are mutually exclusive with readback", ChargeModes),
    ("unsupported rapid charge sends no setters", UnsupportedRapid),
    ("partial settings and stale readback never claim success", ChargeFailures),
    ("device switch commands verify actual state", DeviceCommands),
    ("disabled automation does not change modes", Disabled),
    ("power transitions debounce and observe cooldown", PowerAutomation),
    ("app priority and exit restore the power scene", AppAutomation),
    ("app exit restores baseline with power rules disabled", AppBaseline),
    ("manual Fn+Q takes priority and preserves manual baseline", ManualPriority),
    ("returning manually to an earlier auto mode is detected", ManualPreviousMode),
    ("turning automation off restores only owned changes", StopAutomation),
    ("unknown power and failed process reads cannot trigger writes", MissingInputs),
    ("backlight restores immediately after owned idle-off", Backlight),
    ("manual backlight changes suppress automatic wake", ManualBacklight),
    ("battery bytes decode sign, health and unknown fields", BatteryDecode),
    ("relative capacity is never displayed as watts or Wh", RelativeBattery),
    ("average discharge is weighted and excludes sampling gaps", BatteryAverage),
    ("CSV is invariant, retains blanks and protects source files", BatteryCsv),
    ("sleep history respects opt-out and computes net loss", BatterySleep),
    ("power parser rejects other CPUs and incomplete tables", PowerParser),
    ("power presets clamp to OEM limits and restore original", PowerApply),
    ("partial power failure rolls back before reporting failure", PowerRollback),
    ("power readback mismatch is rejected", PowerMismatch),
    ("every power setter initializes the Windows PM mapping", PowerInitialization),
    ("native crashes show exit code even with empty output", PowerCrashError),
    ("independent readback recovers state after native crashes", PowerCrashRecovery),
    ("configuration normalizes invalid rules and ranges", Settings),
};
int failures = 0;
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine("PASS " + check.Name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + check.Name + ": " + ex.Message); }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} feature checks passed; fake transports only.");
Environment.ExitCode = failures == 0 ? 0 : 1;

static void Require(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
static DateTimeOffset T(int seconds = 0) => new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.FromHours(8)).AddSeconds(seconds);
static HashSet<string> Processes(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
static (LenovoDeviceService S, FakeEnergy D) Rig()
{ var d = new FakeEnergy(); return (new(d, () => (true, "test")), d); }
static void Unsupported()
{
    var d = new FakeEnergy(); var s = new LenovoDeviceService(d, () => (false, "other"));
    Require(!s.Refresh().BatteryAvailable && !s.SetChargingMode(ChargingMode.Conservation) && !s.SetSwitch(DeviceSwitch.UsbAc, true), "Unsupported setting accepted");
    Require(d.Calls.Count == 0, "Unsupported machine reached driver");
}
static void ReadOnly()
{
    var (s, d) = Rig(); var state = s.Refresh();
    Require(d.Calls.All(c => c.Query) && d.Calls.Select(c => c.Command).SequenceEqual(new byte[] { 255, 2 }), "Refresh sent setters");
    Require(state.RapidSupported && state.Supported(DeviceSwitch.FnLock), "Capabilities not recognized");
    d.Device = 0; Require(!s.SetSwitch(DeviceSwitch.Backlight, true) && d.Calls.All(c => c.Query), "Unsupported backlight was written");
}
static void ChargeModes()
{
    foreach (var mode in new[] { ChargingMode.Normal, ChargingMode.Conservation, ChargingMode.Rapid })
    {
        var (s, d) = Rig(); d.Battery |= 0x24;
        Require(s.SetChargingMode(mode) && s.State.Charging == mode && !s.State.ConflictingChargeModes, "Charging state not verified");
        byte[] expected = mode switch { ChargingMode.Normal => new byte[] { 8, 5 }, ChargingMode.Conservation => new byte[] { 8, 3 }, _ => new byte[] { 5, 7 } };
        Require(d.Calls.Where(c => !c.Query).Select(c => c.Command).SequenceEqual(expected), "Wrong charge sequence");
        Require(d.Calls.Last().Query, "Missing readback");
    }
}
static void UnsupportedRapid()
{ var (s, d) = Rig(); d.Battery = 0; Require(!s.SetChargingMode(ChargingMode.Rapid) && d.Calls.All(c => c.Query), "Rapid unsupported but written"); }
static void ChargeFailures()
{
    var (s, d) = Rig(); d.FailCommand = 3;
    Require(!s.SetChargingMode(ChargingMode.Conservation) && s.State.Charging == ChargingMode.Normal, "Partial charge write claimed success");
    d.FailCommand = null; d.Freeze = true;
    Require(!s.SetChargingMode(ChargingMode.Rapid), "Stale readback accepted");
    d.FailReads = true; Require(!s.Refresh().BatteryAvailable && !s.SetChargingMode(ChargingMode.Normal), "Failed refresh left a usable stale mode");
}
static void DeviceCommands()
{
    foreach (var feature in Enum.GetValues<DeviceSwitch>())
    {
        var (s, d) = Rig(); Require(s.SetSwitch(feature, true) && s.State.Value(feature) == true, "Switch enable failed");
        Require(s.SetSwitch(feature, false) && s.State.Value(feature) == false, "Switch disable failed");
        d.Freeze = true; Require(!s.SetSwitch(feature, true), "Unverified switch accepted");
    }
}
static AutomationSettings Auto(bool power = true) => new() { Enabled = true, SwitchOnPower = power, CooldownSeconds = 10, ManualPauseMinutes = 1 };
static void Disabled()
{ var e = new AutomationEngine(); Require(e.Evaluate(new(), ThermalProfile.IntelligentCooling, false, Processes(), T()) == null, "Disabled automation wrote mode"); }
static void PowerAutomation()
{
    var e = new AutomationEngine(); var s = Auto();
    Require(e.Evaluate(s, ThermalProfile.IntelligentCooling, false, Processes(), T()) == null, "No debounce");
    var d = e.Evaluate(s, ThermalProfile.IntelligentCooling, false, Processes(), T(3));
    Require(d?.Profile == ThermalProfile.BatterySaving, "Battery rule ignored"); e.Complete(d!, true, "");
    Require(e.Evaluate(s, ThermalProfile.BatterySaving, true, Processes(), T(4)) == null, "Cooldown ignored");
    Require(e.Evaluate(s, ThermalProfile.BatterySaving, true, Processes(), T(7)) == null, "Cooldown too short");
    Require(e.Evaluate(s, ThermalProfile.BatterySaving, true, Processes(), T(13))?.Profile == ThermalProfile.IntelligentCooling, "AC rule failed");
}
static void AppAutomation()
{
    var e = new AutomationEngine(); var s = Auto();
    s.Applications.Add(new() { ProcessName = "game", Profile = ThermalProfile.ExtremePerformance });
    s.Applications.Add(new() { ProcessName = "editor", Profile = ThermalProfile.BatterySaving });
    e.Evaluate(s, ThermalProfile.IntelligentCooling, true, Processes("GAME", "editor"), T());
    var d = e.Evaluate(s, ThermalProfile.IntelligentCooling, true, Processes("GAME", "editor"), T(3));
    Require(d?.Profile == ThermalProfile.ExtremePerformance, "Rule precedence or case matching wrong"); e.Complete(d!, true, "");
    e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T(13));
    Require(e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T(16))?.Profile == ThermalProfile.BatterySaving, "App exit did not use battery rule");
}
static void AppBaseline()
{
    var e = new AutomationEngine(); var s = Auto(false); s.Applications.Add(new() { ProcessName = "game" });
    e.Evaluate(s, ThermalProfile.IntelligentCooling, null, Processes("game"), T());
    var d = e.Evaluate(s, ThermalProfile.IntelligentCooling, null, Processes("game"), T(3)); e.Complete(d!, true, "");
    e.Evaluate(s, ThermalProfile.ExtremePerformance, null, Processes(), T(13));
    Require(e.Evaluate(s, ThermalProfile.ExtremePerformance, null, Processes(), T(16))?.Profile == ThermalProfile.IntelligentCooling, "Baseline not restored");
}
static void ManualPriority()
{
    var e = new AutomationEngine(); var s = Auto();
    e.Evaluate(s, ThermalProfile.IntelligentCooling, true, Processes(), T());
    Require(e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T(1)) == null, "External Fn+Q overwritten");
    Require(e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T(40)) == null, "Manual pause expired too early");
    s.Enabled = false; Require(e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T(70)) == null, "Manual profile restored away");
}
static void StopAutomation()
{
    var e = new AutomationEngine(); var s = Auto(); e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T());
    var d = e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T(3)); e.Complete(d!, true, "");
    s.Enabled = false;
    Require(e.Evaluate(s, ThermalProfile.BatterySaving, false, Processes(), T(13))?.Profile == ThermalProfile.ExtremePerformance, "Owned change not restored");
}
static void ManualPreviousMode()
{
    var e = new AutomationEngine(); var s = Auto(); e.Evaluate(s, ThermalProfile.IntelligentCooling, false, Processes(), T());
    var d = e.Evaluate(s, ThermalProfile.IntelligentCooling, false, Processes(), T(3)); e.Complete(d!, true, "");
    e.Evaluate(s, ThermalProfile.ExtremePerformance, false, Processes(), T(4));
    e.Evaluate(s, ThermalProfile.BatterySaving, true, Processes(), T(70));
    Require(e.Status.StartsWith("手动优先"), "Manual return to last applied mode was ignored");
}
static void MissingInputs()
{
    var e = new AutomationEngine(); var s = Auto();
    Require(e.Evaluate(s, ThermalProfile.IntelligentCooling, null, Processes(), T()) == null, "Guessed AC state");
    s.Applications.Add(new() { ProcessName = "game" });
    Require(e.Evaluate(s, ThermalProfile.IntelligentCooling, false, null, T(20)) == null, "Guessed missing processes");
}
static void Backlight()
{
    var e = new BacklightIdleController(); Require(e.Evaluate(30, 31, true, T()) == false, "Idle did not switch off"); e.Complete(false, true);
    Require(e.Evaluate(30, 1, false, T(1)) == true, "Activity did not restore immediately"); e.Complete(true, true);
    Require(!e.OwnsOff && e.Evaluate(30, 31, false, T(40)) == null, "Manually off light was turned on");
}
static void ManualBacklight()
{
    var e = new BacklightIdleController(); e.Evaluate(30, 31, true, T()); e.Complete(false, true); e.ManualChange();
    Require(e.Evaluate(30, 0, false, T(1)) == null, "Manual off auto-restored");
    e.Evaluate(30, 31, true, T(40)); e.Complete(false, true);
    Require(e.Evaluate(30, 32, true, T(41)) == null && !e.OwnsOff, "Manual on ignored");
}
static (byte[] Info, byte[] Status) BatteryBytes(bool relative = false)
{
    var info = new byte[36]; var status = new byte[16];
    BitConverter.GetBytes(relative ? 0xC0000000u : 0x80000000u).CopyTo(info, 0);
    BitConverter.GetBytes(56000u).CopyTo(info, 12); BitConverter.GetBytes(44800u).CopyTo(info, 16);
    BitConverter.GetBytes(300u).CopyTo(info, 32); BitConverter.GetBytes(2u).CopyTo(status, 0);
    BitConverter.GetBytes(22400u).CopyTo(status, 4); BitConverter.GetBytes(-12000).CopyTo(status, 12); return (info, status);
}
static void BatteryDecode()
{
    var (info, status) = BatteryBytes(); var b = BatteryReader.Decode(info, status, new(), "test");
    Require(b.Percent == 50 && b.PowerW == 12 && b.DesignWh == 56 && b.HealthPercent == 80 && b.Cycles == 300, "Wrong battery decode");
    BitConverter.GetBytes(int.MinValue).CopyTo(status, 12); BitConverter.GetBytes(0u).CopyTo(info, 32);
    b = BatteryReader.Decode(info, status, new(), "test"); Require(b.PowerW == null && b.Cycles == null, "Unknown fields invented");
    BitConverter.GetBytes(5000).CopyTo(status, 12); Require(BatteryReader.Decode(info, status, new(), "test").PowerW == -5, "Charging sign wrong");
}
static void RelativeBattery()
{ var (i, s) = BatteryBytes(true); var b = BatteryReader.Decode(i, s, new(), "test"); Require(b.PowerW == null && b.DesignWh == null && b.RemainingWh == null && b.Percent == 50, "Relative capacity treated as absolute"); }
static string CheckDir() { var p = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "exploration", "feature-checks", Guid.NewGuid().ToString("N")); return Path.GetFullPath(p); }
static BatteryReading Sample(int second, float power = 10) => new() { Time = T(second), DeviceId = "test", Percent = 80, RemainingWh = 40, PowerW = power, PowerOnline = false };
static void BatteryAverage()
{
    var h = new BatteryHistoryService(CheckDir()); h.Add(Sample(0, 10), false); h.Add(Sample(10, 10), false); h.Add(Sample(20, 20), false);
    Require(h.AverageDischargeW == 12.5f, "Discharge average not weighted");
    var gap = new BatteryHistoryService(CheckDir()); gap.Add(Sample(0), false); gap.Add(Sample(100), false);
    Require(gap.AverageDischargeW == null, "Sampling gap counted as active discharge");
}
static void BatteryCsv()
{
    string dir = CheckDir(); var h = new BatteryHistoryService(dir); var old = CultureInfo.CurrentCulture;
    try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); h.Add(Sample(0, 10.5f), true); }
    finally { CultureInfo.CurrentCulture = old; }
    string file = Directory.GetFiles(dir, "battery-*.csv").Single(); Require(File.ReadAllText(file).Contains(",10.5,"), "CSV used local decimal separator");
    bool rejected = false; try { h.Export(file); } catch (IOException) { rejected = true; }
    Require(rejected && File.ReadAllLines(file).Length == 2, "Export overwrote source data");
    string export = Path.Combine(dir, "export.csv"); h.Export(export); Require(File.ReadAllLines(export).Length == 2, "Export row missing");
}
static void BatterySleep()
{
    string dir = CheckDir(); var h = new BatteryHistoryService(dir); var before = Sample(0);
    h.Suspend(before, true, T()); h.Resume(); h.Add(Sample(3600) with { Percent = 77, RemainingWh = 38.5f }, true);
    Require(h.SleepRecords.Single().PercentDrop == 3 && h.SleepRecords[0].EnergyWh == 1.5f, "Sleep net loss wrong");
    h.Suspend(Sample(3600), false, T(3600)); h.Resume(); h.Add(Sample(7200), false);
    Require(h.SleepRecords.Count == 1, "Opted-out sleep recorded");
    var disabledDir = CheckDir(); var off = new BatteryHistoryService(disabledDir); off.Add(before, false);
    Require(!Directory.Exists(disabledDir), "Opted-out records written");
}
static string Table(PowerLimits p, string family = "Renoir") => $"CPU Family: {family}\n| PPT LIMIT FAST | {p.FastW.ToString(CultureInfo.InvariantCulture)} | fast-limit |\n| PPT LIMIT SLOW | {p.SlowW.ToString(CultureInfo.InvariantCulture)} | slow-limit |\n| THM LIMIT CORE | {p.TemperatureC.ToString(CultureInfo.InvariantCulture)} | tctl-temp |";
static void PowerParser()
{
    var p = new PowerLimits(30, 25, 95); Require(PowerTuningService.Parse(Table(p)) == p, "Valid power table rejected");
    Require(PowerTuningService.Parse(Table(p, "Cezanne")) == null && PowerTuningService.Parse("CPU Family: Renoir") == null, "Invalid power table accepted");
    Require(PowerTuningService.Parse(Table(new(0, 0, 0))) == null, "Zero limits accepted");
}
static (PowerTuningService S, FakeRyzen D) PowerRig()
{ var d = new FakeRyzen(); return (new("C:\\test\\ryzenadj.exe", d, () => (true, "test")), d); }
static void PowerApply()
{
    var (s, d) = PowerRig(); var original = d.Current;
    Require(s.Apply(PowerPreset.Sustained) && s.Current == new PowerLimits(22, 20, 85), "Preset exceeded OEM limits");
    Require(d.Arguments.All(a => a.All(v => !v.Contains("stapm") && !v.Contains("gfx"))), "Unsupported Renoir tuning option sent");
    Require(s.Restore() && s.Current == original && s.Original == null, "Original limits not restored");
    var unsupported = new PowerTuningService("unused", d, () => (false, "other")); int calls = d.Arguments.Count;
    Require(!unsupported.Apply(PowerPreset.Quiet) && d.Arguments.Count == calls, "Unverified CPU reached tuning runner");
}
static void PowerRollback()
{
    var (s, d) = PowerRig(); var original = d.Current; d.FailNextWrite = true;
    Require(!s.Apply(PowerPreset.Quiet) && d.Current == original && s.Message.Contains("已恢复操作前参数"), "Partial power write not rolled back");
}
static void PowerMismatch()
{ var (s, d) = PowerRig(); d.Freeze = true; Require(!s.Apply(PowerPreset.Quiet), "Stale power limits accepted"); }
static void PowerInitialization()
{
    var (s, d) = PowerRig(); Require(s.Apply(PowerPreset.Quiet) && s.Restore(), "Power operation failed");
    Require(d.Arguments.Where(a => a.Any(v => v.StartsWith("--fast-limit="))).All(a => a.Contains("--info")),
        "Setter or restore did not initialize the PM mapping before native cleanup");
}
static void PowerCrashError()
{
    string message = PowerTuningService.DescribeFailure(new(unchecked((int)0xC0000005), "\n"));
    Require(message.Contains("0xC0000005") && message.Contains("访问违规") && message.Contains("独立回读"), "Empty crash output hid native error");
}
static void PowerCrashRecovery()
{
    var (s, d) = PowerRig(); var original = d.Current; d.CrashAfterWrite = true;
    Require(!s.Apply(PowerPreset.Quiet) && s.Current == original && s.Message.Contains("独立回读已确认")
        && s.Message.Contains("0xC0000005"), "Actual limits were lost after native write/restore crashes");
}
static void Settings()
{
    var s = new AutomationSettings { AcProfile = (ThermalProfile)999, CooldownSeconds = 0, ManualPauseMinutes = 999,
        Applications = new() { new() { ProcessName = "GAME.exe" }, new() { ProcessName = "game" }, new() { ProcessName = "../bad" }, new() { ProcessName = "test", Profile = (ThermalProfile)999 } } };
    s.Normalize(); Require(s.Applications.Count == 1 && s.Applications[0].ProcessName == "GAME" && s.CooldownSeconds == 10
        && s.ManualPauseMinutes == 120 && s.AcProfile == ThermalProfile.IntelligentCooling, "Invalid settings not normalized");
}

sealed class FakeEnergy : IEnergyTransport
{
    public uint Battery = 0x20000, Device = 0x4250;
    public bool Freeze, FailReads;
    public byte? FailCommand;
    public List<(uint Ioctl, byte Command, bool Query)> Calls { get; } = new();
    public bool TryCall(uint ioctl, byte command, bool query, out uint reply, out string error)
    {
        Calls.Add((ioctl, command, query)); reply = 0; error = "fake failure";
        if (query && FailReads || !query && FailCommand == command) return false;
        if (query) { reply = ioctl == LenovoDeviceService.BatteryIoctl ? Battery : Device; error = ""; return true; }
        if (!Freeze)
        {
            if (ioctl == LenovoDeviceService.BatteryIoctl)
                Battery = command switch { 3 => Battery | 0x20, 5 => Battery & ~0x20u, 7 => Battery | 4, 8 => Battery & ~4u, _ => Battery };
            else Device = command switch { 8 => Device | 0x20, 9 => Device & ~0x20u, 10 => Device | 0x80, 11 => Device & ~0x80u,
                14 => Device | 0x400, 15 => Device & ~0x400u, 19 => Device | 0x8000, 18 => Device & ~0x8000u, _ => Device };
        }
        error = ""; return true;
    }
}
sealed class FakeRyzen : IRyzenAdjRunner
{
    public PowerLimits Current = new(22, 20, 95);
    public bool FailNextWrite, Freeze, CrashAfterWrite;
    public List<string[]> Arguments { get; } = new();
    public CommandResult Run(string path, IReadOnlyList<string> arguments)
    {
        Arguments.Add(arguments.ToArray());
        if (arguments.SequenceEqual(new[] { "--info" })) return new(0, $"CPU Family: Renoir\n| PPT LIMIT FAST | {Current.FastW.ToString(CultureInfo.InvariantCulture)} | fast-limit |\n| PPT LIMIT SLOW | {Current.SlowW.ToString(CultureInfo.InvariantCulture)} | slow-limit |\n| THM LIMIT CORE | {Current.TemperatureC.ToString(CultureInfo.InvariantCulture)} | tctl-temp |");
        float Value(string prefix) => float.Parse(arguments.Single(a => a.StartsWith(prefix)).Split('=')[1], CultureInfo.InvariantCulture);
        if (FailNextWrite) { Current = Current with { FastW = Value("--fast-limit") / 1000 }; FailNextWrite = false; return new(1, "partial failure"); }
        if (!Freeze) Current = new(Value("--fast-limit") / 1000, Value("--slow-limit") / 1000, Value("--tctl-temp"));
        if (CrashAfterWrite) return new(unchecked((int)0xC0000005), "");
        return new(0, "Success");
    }
}
