using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            report.Requested = new(Math.Min(15, report.Before.FastW), Math.Min(12, report.Before.SlowW), Math.Min(70, report.Before.TemperatureC));
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
                var target = preset == PowerPreset.Balanced ? new PowerLimits(25, 18, 80) : new PowerLimits(30, 25, 85);
                var expected = new PowerLimits(Math.Min(target.FastW, before.FastW), Math.Min(target.SlowW, before.SlowW), Math.Min(target.TemperatureC, before.TemperatureC));
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
