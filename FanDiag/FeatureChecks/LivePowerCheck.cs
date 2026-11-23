using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FanController.Models;
using FanController.Services;

internal static class LivePowerCheck
{
    internal sealed record Sample(DateTimeOffset Time, string Stage, PowerLimits Limits,
        float? ActualPptW, float? TemperatureC, string RawTable);
    internal sealed class Report
    {
        public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? Finished { get; set; }
        public int LoadThreads { get; set; }
        public PowerLimits? Before { get; set; }
        public PowerLimits? Requested { get; set; }
        public PowerLimits? Restored { get; set; }
        public bool LimitsHeld { get; set; }
        public bool RestoreConfirmed { get; set; }
        public float? BaselineMeanW { get; set; }
        public float? QuietMeanW { get; set; }
        public List<Sample> Samples { get; } = new();
        public List<string> Messages { get; } = new();
        public string? Error { get; set; }
    }

    public static int Run(string executable, string evidencePath)
    {
        var report = new Report();
        var service = new PowerTuningService(executable);
        var runner = new RyzenAdjRunner();
        var load = new List<Thread>();
        using var cancellation = new CancellationTokenSource();
        bool passed = false;
        void Save() => File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        void ReadSample(string stage, PowerLimits? expected = null)
        {
            var result = runner.Run(executable, new[] { "--info" });
            if (result.ExitCode != 0) throw new InvalidOperationException(PowerTuningService.DescribeFailure(result));
            var limits = PowerTuningService.Parse(result.Output) ?? throw new InvalidOperationException("Power table missing");
            float? Value(string name)
            {
                var match = Regex.Match(result.Output, @"\|\s*" + Regex.Escape(name) + @"\s*\|\s*([0-9]+(?:\.[0-9]+)?)\s*\|");
                return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : null;
            }
            var sample = new Sample(DateTimeOffset.Now, stage, limits, Value("PPT VALUE FAST"), Value("THM VALUE CORE"), result.Output);
            report.Samples.Add(sample); Save();
            Console.WriteLine($"{stage}: {limits}; measured PPT={sample.ActualPptW:0.00} W; temperature={sample.TemperatureC:0.0} C");
            if (sample.TemperatureC is >= 95) throw new InvalidOperationException("Stopped load at 95 C");
            if (expected != null && !expected.Matches(limits)) throw new InvalidOperationException("Limits changed unexpectedly: " + limits);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            if (!service.Refresh()) throw new InvalidOperationException(service.Message);
            report.Before = service.Current!;
            report.Requested = new(Math.Min(15, report.Before.FastW), Math.Min(12, report.Before.SlowW));
            Save(); // Persist restoration values before any hardware setting.
            ReadSample("IdleBefore", report.Before);
            report.LoadThreads = Math.Clamp(Environment.ProcessorCount, 1, 12);
            for (int index = 0; index < report.LoadThreads; index++)
            {
                var thread = new Thread(() =>
                {
                    double value = 1.2345;
                    while (!cancellation.IsCancellationRequested)
                    {
                        for (int i = 0; i < 2000; i++) value = Math.Sqrt(value + 1.234567);
                        GC.KeepAlive(value);
                    }
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BoundedPowerCheck" };
                load.Add(thread); thread.Start();
            }
            for (int i = 1; i <= 3; i++) { Thread.Sleep(2000); ReadSample("LoadBefore" + i, report.Before); }
            if (!service.Apply(PowerPreset.Quiet)) throw new InvalidOperationException(service.Message);
            report.Messages.Add(service.Message); Save();
            ReadSample("QuietImmediate", report.Requested);
            for (int i = 1; i <= 4; i++) { Thread.Sleep(3000); ReadSample("QuietAfter" + i * 3 + "s", report.Requested); }
            report.LimitsHeld = true;
            report.BaselineMeanW = Mean(report.Samples.Where(s => s.Stage.StartsWith("LoadBefore")));
            report.QuietMeanW = Mean(report.Samples.Where(s => s.Stage.StartsWith("QuietAfter")));
            passed = true;
        }
        catch (Exception ex) { report.Error = ex.Message; Console.WriteLine("CHECK FAILED: " + ex.Message); }
        finally
        {
            cancellation.Cancel();
            foreach (var thread in load) thread.Join(1500);
            if (service.Original != null)
            {
                bool restored = service.Restore(); report.Messages.Add(service.Message);
                if (!restored && service.Original != null) { Thread.Sleep(500); service.Restore(); report.Messages.Add(service.Message); }
            }
            if (report.Before != null)
            {
                Thread.Sleep(2000);
                try
                {
                    if (!service.Refresh()) throw new InvalidOperationException(service.Message);
                    report.Restored = service.Current;
                    report.RestoreConfirmed = report.Restored != null && report.Before.Matches(report.Restored);
                    ReadSample("AfterRestore", report.Before);
                }
                catch (Exception ex) { report.RestoreConfirmed = false; report.Messages.Add("Restore verification: " + ex.Message); }
            }
            report.Finished = DateTimeOffset.Now;
            Save();
        }
        Console.WriteLine($"LimitsHeld={report.LimitsHeld}; RestoreConfirmed={report.RestoreConfirmed}; baseline mean={report.BaselineMeanW:0.00} W; quiet mean={report.QuietMeanW:0.00} W");
        return passed && report.RestoreConfirmed ? 0 : 1;
    }

    internal sealed record SoakSample(DateTimeOffset Time, double ElapsedSeconds, string Stage, PowerLimits Limits,
        float? ThermalWallC, float? FastW, float? SlowW, float? TemperatureC);
    internal sealed class SoakReport
    {
        public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? Finished { get; set; }
        public int LoadSeconds { get; set; }
        public int IdleSeconds { get; set; }
        public int LoadThreads { get; set; }
        public bool? PowerOnline { get; set; }
        public PowerLimits? Before { get; set; }
        public PowerLimits? Requested { get; set; }
        public PowerLimits? Restored { get; set; }
        public double? ApplySeconds { get; set; }
        public bool? LimitsHeldUnderLoad { get; set; }
        public bool? LimitsHeldIdle { get; set; }
        /// <summary>Seconds after the preset was applied when the firmware limits first stopped matching; null when they never did.</summary>
        public double? FirstRevertSeconds { get; set; }
        public bool ThermalWallUnchanged { get; set; }
        public float? BaselineMeanFastW { get; set; }
        public float? BaselineMeanSlowW { get; set; }
        public float? QuietMeanFastW { get; set; }
        public float? QuietMeanSlowW { get; set; }
        public float? QuietMaxFastW { get; set; }
        public float? MaxTemperatureC { get; set; }
        public bool RestoreConfirmed { get; set; }
        public List<SoakSample> Samples { get; } = new();
        public List<string> Messages { get; } = new();
        public string? Error { get; set; }
    }

    /// <summary>Holds the Quiet preset under load and then at idle to detect limits being re-asserted shortly after the write.
    /// Only --info, --fast-limit and --slow-limit ever leave this process; the thermal wall is read, never written.</summary>
    public static int Soak(string executable, string evidencePath, int loadSeconds, int idleSeconds)
    {
        var report = new SoakReport { LoadSeconds = loadSeconds, IdleSeconds = idleSeconds };
        var service = new PowerTuningService(executable);
        var runner = new RyzenAdjRunner();
        var load = new List<Thread>();
        using var cancellation = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        bool passed = false;
        void Sample(string stage)
        {
            var result = runner.Run(executable, new[] { "--info" });
            if (result.ExitCode != 0) throw new InvalidOperationException(PowerTuningService.DescribeFailure(result));
            var sample = new SoakSample(DateTimeOffset.Now, watch.Elapsed.TotalSeconds, stage,
                PowerTuningService.Parse(result.Output) ?? throw new InvalidOperationException("Power table missing"),
                PowerTuningService.ParseThermalLimit(result.Output), TableValue(result.Output, "PPT VALUE FAST"),
                TableValue(result.Output, "PPT VALUE SLOW"), TableValue(result.Output, "THM VALUE CORE"));
            report.Samples.Add(sample); SaveJson(evidencePath, report);
            Console.WriteLine($"[{sample.ElapsedSeconds,6:0.0}s] {stage,-13} limit {sample.Limits.FastW,5:0.#}/{sample.Limits.SlowW,5:0.#} W   actual {sample.FastW,6:0.00}/{sample.SlowW,6:0.00} W   {sample.TemperatureC,5:0.0} C   wall {sample.ThermalWallC,3:0} C");
            if (sample.TemperatureC >= 95) throw new InvalidOperationException("Stopped load at 95 C");
        }
        void Check(SoakSample sample)
        {
            if (report.FirstRevertSeconds != null || sample.Limits.Matches(report.Requested!)) return;
            report.FirstRevertSeconds = sample.ElapsedSeconds - report.ApplySeconds!.Value;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            report.PowerOnline = BatteryReader.PowerOnline();
            if (!service.Refresh()) throw new InvalidOperationException(service.Message);
            report.Before = service.Current!; report.Requested = service.Preview(PowerPreset.Quiet); SaveJson(evidencePath, report); // Persist restoration values before any hardware setting.
            Sample("IdleBefore");
            report.LoadThreads = Math.Clamp(Environment.ProcessorCount, 1, 12);
            for (int index = 0; index < report.LoadThreads; index++)
            {
                var thread = new Thread(() =>
                {
                    double value = 1.2345;
                    while (!cancellation.IsCancellationRequested)
                    {
                        for (int i = 0; i < 2000; i++) value = Math.Sqrt(value + 1.234567);
                        GC.KeepAlive(value);
                    }
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SoakPowerCheck" };
                load.Add(thread); thread.Start();
            }
            for (int i = 1; i <= 3; i++) { Thread.Sleep(2000); Sample("LoadBefore" + i); }
            if (!service.Apply(PowerPreset.Quiet)) throw new InvalidOperationException(service.Message);
            report.Messages.Add(service.Message); report.ApplySeconds = watch.Elapsed.TotalSeconds; SaveJson(evidencePath, report);
            Sample("QuietApplied");
            // Denser sampling right after the write, where a short-lived setting would show.
            while (report.FirstRevertSeconds == null && watch.Elapsed.TotalSeconds - report.ApplySeconds.Value < loadSeconds)
            {
                double since = watch.Elapsed.TotalSeconds - report.ApplySeconds.Value;
                Thread.Sleep(since < 30 ? 2000 : 5000); Sample("QuietLoad"); Check(report.Samples[^1]);
            }
            report.LimitsHeldUnderLoad = report.FirstRevertSeconds == null;
            cancellation.Cancel(); foreach (var thread in load) thread.Join(1500);
            for (int i = 0; i < idleSeconds / 10 && report.FirstRevertSeconds == null; i++)
            { Thread.Sleep(10000); Sample("QuietIdle"); Check(report.Samples[^1]); }
            report.LimitsHeldIdle = report.FirstRevertSeconds == null;
            passed = true;
        }
        catch (Exception ex) { report.Error = ex.Message; Console.WriteLine("CHECK FAILED: " + ex.Message); }
        finally
        {
            cancellation.Cancel();
            foreach (var thread in load) thread.Join(1500);
            if (service.Original != null)
            {
                bool restored = service.Restore(); report.Messages.Add(service.Message);
                if (!restored && service.Original != null) { Thread.Sleep(500); service.Restore(); report.Messages.Add(service.Message); }
            }
            if (report.Before != null)
            {
                Thread.Sleep(2000);
                try
                {
                    if (!service.Refresh()) throw new InvalidOperationException(service.Message);
                    report.Restored = service.Current;
                    report.RestoreConfirmed = report.Restored != null && report.Before.Matches(report.Restored);
                    Sample("AfterRestore");
                }
                catch (Exception ex) { report.RestoreConfirmed = false; report.Messages.Add("Restore verification: " + ex.Message); }
            }
            var walls = report.Samples.Where(s => s.ThermalWallC.HasValue).Select(s => s.ThermalWallC!.Value).Distinct().ToArray();
            report.ThermalWallUnchanged = walls.Length == 1;
            var baseline = report.Samples.Where(s => s.Stage.StartsWith("LoadBefore", StringComparison.Ordinal)).ToArray();
            var quiet = report.Samples.Where(s => s.Stage == "QuietLoad").ToArray();
            report.BaselineMeanFastW = MeanOf(baseline, s => s.FastW); report.BaselineMeanSlowW = MeanOf(baseline, s => s.SlowW);
            report.QuietMeanFastW = MeanOf(quiet, s => s.FastW); report.QuietMeanSlowW = MeanOf(quiet, s => s.SlowW);
            report.QuietMaxFastW = quiet.Any(s => s.FastW.HasValue) ? quiet.Max(s => s.FastW) : null;
            report.MaxTemperatureC = report.Samples.Any(s => s.TemperatureC.HasValue) ? report.Samples.Max(s => s.TemperatureC) : null;
            report.Finished = DateTimeOffset.Now; SaveJson(evidencePath, report);
        }
        Console.WriteLine($"LimitsHeldUnderLoad={report.LimitsHeldUnderLoad}; LimitsHeldIdle={report.LimitsHeldIdle}; FirstRevertSeconds={report.FirstRevertSeconds}; "
            + $"baseline {report.BaselineMeanFastW:0.00}/{report.BaselineMeanSlowW:0.00} W; quiet {report.QuietMeanFastW:0.00}/{report.QuietMeanSlowW:0.00} W (max {report.QuietMaxFastW:0.00} W); "
            + $"max temp {report.MaxTemperatureC:0.0} C; wall unchanged={report.ThermalWallUnchanged}; RestoreConfirmed={report.RestoreConfirmed}");
        return passed && report.RestoreConfirmed && report.LimitsHeldUnderLoad == true && report.LimitsHeldIdle == true ? 0 : 1;
    }

    internal sealed class TriggerReport
    {
        public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? Finished { get; set; }
        public ThermalProfile BaselineProfile { get; set; }
        public ThermalProfile SwitchedProfile { get; set; }
        public PowerLimits? Before { get; set; }
        public PowerLimits? Requested { get; set; }
        public PowerLimits? AfterApply { get; set; }
        public PowerLimits? AfterSwitch { get; set; }
        public PowerLimits? AfterSwitchBack { get; set; }
        public PowerLimits? Restored { get; set; }
        /// <summary>True when the preset limits were still in effect after an Fn+Q profile change.</summary>
        public bool SurvivedProfileSwitch { get; set; }
        public bool SurvivedSwitchBack { get; set; }
        public bool ProfileRestored { get; set; }
        public bool RestoreConfirmed { get; set; }
        public List<string> Messages { get; } = new();
        public string? Error { get; set; }
    }

    /// <summary>Checks whether an Fn+Q profile change (the hotkey and the automation both do this) re-asserts the firmware power limits.</summary>
    public static int ProfileTrigger(string executable, string evidencePath)
    {
        var report = new TriggerReport();
        var power = new PowerTuningService(executable); var profiles = new ThermalProfileService(); var runner = new RyzenAdjRunner();
        bool passed = false;
        PowerLimits Read(string stage)
        {
            var result = runner.Run(executable, new[] { "--info" });
            if (result.ExitCode != 0) throw new InvalidOperationException(PowerTuningService.DescribeFailure(result));
            var limits = PowerTuningService.Parse(result.Output) ?? throw new InvalidOperationException("Power table missing");
            Console.WriteLine($"{stage}: {limits}; wall {PowerTuningService.ParseThermalLimit(result.Output):0} C"); return limits;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            if (!profiles.Refresh()) throw new InvalidOperationException(profiles.LastMessage);
            report.BaselineProfile = profiles.CurrentProfile;
            report.SwitchedProfile = report.BaselineProfile == ThermalProfile.IntelligentCooling ? ThermalProfile.ExtremePerformance : ThermalProfile.IntelligentCooling;
            if (!power.Refresh()) throw new InvalidOperationException(power.Message);
            report.Before = power.Current; SaveJson(evidencePath, report);
            if (!power.Apply(PowerPreset.Quiet)) throw new InvalidOperationException(power.Message);
            report.Requested = power.Target; report.AfterApply = Read("Applied Quiet"); SaveJson(evidencePath, report);
            if (!profiles.SetProfile(report.SwitchedProfile)) throw new InvalidOperationException(profiles.LastMessage);
            Thread.Sleep(3000);
            report.AfterSwitch = Read("After Fn+Q switch to " + report.SwitchedProfile);
            report.SurvivedProfileSwitch = report.AfterSwitch.Matches(report.Requested!); SaveJson(evidencePath, report);
            if (!profiles.SetProfile(report.BaselineProfile)) throw new InvalidOperationException(profiles.LastMessage);
            Thread.Sleep(3000);
            report.AfterSwitchBack = Read("After Fn+Q switch back to " + report.BaselineProfile);
            report.SurvivedSwitchBack = report.AfterSwitchBack.Matches(report.Requested!);
            passed = true;
        }
        catch (Exception ex) { report.Error = ex.Message; Console.WriteLine("CHECK FAILED: " + ex.Message); }
        finally
        {
            // The user-visible Fn+Q mode goes back first: restoring it may itself re-assert the firmware limits.
            report.ProfileRestored = !ProfileLabels.Valid(report.BaselineProfile) || profiles.CurrentProfile == report.BaselineProfile
                || profiles.SetProfile(report.BaselineProfile);
            report.Messages.Add("Fn+Q：" + profiles.LastMessage);
            if (power.Original != null)
            {
                if (!power.Restore() && power.Original != null) power.Restore();
                report.Messages.Add(power.Message);
            }
            if (report.Before != null)
            {
                Thread.Sleep(2000);
                try
                {
                    if (!power.Refresh()) throw new InvalidOperationException(power.Message);
                    report.Restored = power.Current; report.RestoreConfirmed = report.Before.Matches(report.Restored!); Read("After restore");
                }
                catch (Exception ex) { report.RestoreConfirmed = false; report.Messages.Add("Restore verification: " + ex.Message); }
            }
            report.Finished = DateTimeOffset.Now; SaveJson(evidencePath, report);
        }
        Console.WriteLine($"SurvivedProfileSwitch={report.SurvivedProfileSwitch}; SurvivedSwitchBack={report.SurvivedSwitchBack}; ProfileRestored={report.ProfileRestored}; RestoreConfirmed={report.RestoreConfirmed}");
        return passed && report.ProfileRestored && report.RestoreConfirmed ? 0 : 1;
    }

    internal sealed class BaselineReport
    {
        public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? Finished { get; set; }
        public PowerLimits? Firmware { get; set; }
        public PowerLimits? SessionACurrent { get; set; }
        public string SessionAMessage { get; set; } = "";
        public PowerLimits? Remembered { get; set; }
        public PowerLimits? SessionBRead { get; set; }
        public PowerLimits? SessionBBalanced { get; set; }
        public PowerLimits? SessionBBaseline { get; set; }
        public string SessionBNote { get; set; } = "";
        public PowerLimits? SessionBRestored { get; set; }
        public PowerLimits? FinalIndependentRead { get; set; }
        public List<string> Checks { get; set; } = new();
        public List<string> Messages { get; set; } = new();
    }

    /// <summary>真机验证恢复基准：A 运行应用“安静”后不恢复（模拟上次遗留）；B 运行读到 15/12 时应以记录的固件值为基准，
    /// “均衡”必须真正回到 25/18，“恢复”必须回到固件值。结束时功耗限制恢复为开始时的固件值。</summary>
    public static int BaselineRecovery(string executable, string evidencePath)
    {
        var report = new BaselineReport(); int failures = 0;
        Directory.CreateDirectory(evidencePath); string file = Path.Combine(evidencePath, "power-baseline.json");
        void Check(bool ok, string what)
        {
            report.Checks.Add((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        }
        PowerLimits Read(string stage)
        {
            var result = new RyzenAdjRunner().Run(executable, new[] { "--info" });
            if (result.ExitCode != 0) throw new InvalidOperationException("Read " + stage + ": " + PowerTuningService.DescribeFailure(result));
            var limits = PowerTuningService.Parse(result.Output) ?? throw new InvalidOperationException("Read " + stage + ": power table missing");
            Console.WriteLine($"{stage}: {limits}"); report.Messages.Add($"{stage}: {limits}"); return limits;
        }
        try
        {
            report.Firmware = Read("Firmware"); SaveJson(file, report);
            PowerLimits? stored = null;
            var sessionA = new PowerTuningService(executable, default, null, () => stored, l => stored = l);
            bool appliedA = sessionA.Apply(PowerPreset.Quiet); report.SessionACurrent = sessionA.Current; report.SessionAMessage = sessionA.Message;
            Check(appliedA && sessionA.Current!.Matches(PowerTuningService.RequestedLimits(PowerPreset.Quiet)), "session A applied Quiet without restoring: " + sessionA.Current);
            report.Remembered = stored;
            Check(stored is { } remembered && remembered.Matches(report.Firmware), "session A remembered the firmware limits: " + stored);
            SaveJson(file, report);
            // 模拟上次运行异常退出后的新一次运行：固件里仍是 15/12
            var sessionB = new PowerTuningService(executable, default, null, () => stored, l => stored = l);
            bool readB = sessionB.Refresh(); report.SessionBRead = sessionB.Current;
            Check(readB && sessionB.Current!.Matches(PowerTuningService.RequestedLimits(PowerPreset.Quiet)), "session B read the leftover Quiet: " + sessionB.Current);
            bool balanced = sessionB.Apply(PowerPreset.Balanced); report.SessionBBalanced = sessionB.Current;
            report.SessionBBaseline = sessionB.Original; report.SessionBNote = sessionB.BaselineNote;
            Check(balanced && sessionB.Current!.Matches(PowerTuningService.RequestedLimits(PowerPreset.Balanced)), "session B Balanced was not clamped to the leftover: " + sessionB.Current);
            Check(sessionB.Original is { } baseline && baseline.Matches(report.Firmware) && sessionB.BaselineNote.Length > 0, "session B restore baseline is the firmware value: " + sessionB.Original);
            SaveJson(file, report);
            bool restored = sessionB.Restore(); report.SessionBRestored = sessionB.Current;
            Check(restored && sessionB.Current!.Matches(report.Firmware), "session B restore returned to firmware limits: " + sessionB.Current + " / " + sessionB.Message);
        }
        catch (Exception ex) { failures++; report.Messages.Add(ex.ToString()); Console.WriteLine("FAIL " + ex.Message); }
        finally
        {
            try
            {
                report.FinalIndependentRead = Read("Final");
                Check(report.Firmware != null && report.FinalIndependentRead.Matches(report.Firmware), "final independent read matches the firmware limits");
            }
            catch (Exception ex) { failures++; report.Messages.Add("Final read: " + ex.Message); Console.WriteLine("FAIL final read: " + ex.Message); }
            report.Finished = DateTimeOffset.Now; SaveJson(file, report);
        }
        Console.WriteLine($"{report.Checks.Count(c => c.StartsWith("PASS"))}/{report.Checks.Count} baseline checks passed.");
        return failures == 0 ? 0 : 1;
    }

    // The failure mode under investigation is a sudden power cut: keep the evidence file complete after every step.
    private static void SaveJson(string path, object report)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    private static float? TableValue(string text, string name)
    {
        var match = Regex.Match(text, @"\|\s*" + Regex.Escape(name) + @"\s*\|\s*([0-9]+(?:\.[0-9]+)?)\s*\|");
        return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : null;
    }
    private static float? MeanOf(SoakSample[] samples, Func<SoakSample, float?> pick)
    {
        var values = samples.Select(pick).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return values.Length > 0 ? values.Average() : null;
    }

    private static float? Mean(IEnumerable<Sample> samples)
    {
        var watts = samples.Where(s => s.ActualPptW.HasValue).Select(s => s.ActualPptW!.Value).ToArray();
        return watts.Length > 0 ? watts.Average() : null;
    }

    public static int VerifyAdditionalPresets(string executable, string evidencePath)
    {
        var service = new PowerTuningService(executable);
        var stages = new List<object>();
        var report = new Dictionary<string, object?> { ["Started"] = DateTimeOffset.Now, ["Stages"] = stages };
        PowerLimits? before = null;
        bool passed = false, restored = false;
        void Save() => File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            if (!service.Refresh()) throw new InvalidOperationException(service.Message);
            before = service.Current!; report["Before"] = before; Save();
            foreach (var preset in new[] { PowerPreset.Balanced, PowerPreset.Sustained })
            {
                var target = preset == PowerPreset.Balanced ? new PowerLimits(25, 18) : new PowerLimits(30, 25);
                var expected = new PowerLimits(Math.Min(target.FastW, before.FastW), Math.Min(target.SlowW, before.SlowW));
                if (!service.Apply(preset)) throw new InvalidOperationException(service.Message);
                var immediate = service.Current;
                Thread.Sleep(3000);
                if (!service.Refresh() || !expected.Matches(service.Current!)) throw new InvalidOperationException("Delayed preset readback mismatch: " + service.Message);
                stages.Add(new { Preset = preset.ToString(), Expected = expected, Immediate = immediate, Delayed = service.Current, Verified = true }); Save();
                Console.WriteLine(preset + " verified: " + service.Current);
            }
            passed = true;
        }
        catch (Exception ex) { report["Error"] = ex.Message; Console.WriteLine("CHECK FAILED: " + ex.Message); }
        finally
        {
            if (service.Original != null)
            {
                if (!service.Restore() && service.Original != null) service.Restore();
                report["RestoreMessage"] = service.Message;
            }
            if (before != null)
            {
                Thread.Sleep(2000);
                restored = service.Refresh() && before.Matches(service.Current!);
                report["Restored"] = service.Current;
            }
            report["RestoreConfirmed"] = restored; report["Finished"] = DateTimeOffset.Now; Save();
        }
        Console.WriteLine("RestoreConfirmed=" + restored);
        return passed && restored ? 0 : 1;
    }
}
