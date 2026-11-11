using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FanController.Services;

public sealed record SleepRecord(DateTimeOffset Start, DateTimeOffset End, float? PercentDrop, float? EnergyWh, string Note)
{
    public string When => Start.ToLocalTime().ToString("MM-dd HH:mm");
    public string Duration => $"{(End - Start).TotalHours:0.00} 小时";
    public string Drop => PercentDrop is float p ? $"{p:+0.0;-0.0;0} 个百分点" : "--";
    public string Energy => EnergyWh is float w ? $"{w:+0.00;-0.00;0} Wh" : "--";
}

public sealed class BatteryHistoryService
{
    private readonly string _directory;
    private readonly Queue<BatteryReading> _recent = new();
    private DateTimeOffset _lastLogged = DateTimeOffset.MinValue;
    private DateOnly? _cleanupDate;
    private BatteryReading? _suspend;
    private bool _awaitResume;
    public List<SleepRecord> SleepRecords { get; } = new();
    public string LastError { get; private set; } = "";
    public BatteryHistoryService(string directory)
    {
        _directory = directory;
        try
        {
            if (File.Exists(SleepPath)) SleepRecords.AddRange((JsonSerializer.Deserialize<List<SleepRecord>>(File.ReadAllText(SleepPath)) ?? new()).TakeLast(100));
        }
        catch (Exception ex) { LastError = "睡眠记录读取失败：" + ex.Message; }
    }
    private string SleepPath => Path.Combine(_directory, "sleep.json");

    public void Add(BatteryReading reading, bool record)
    {
        if (_recent.TryPeek(out var first) && (reading.Time < first.Time || _recent.Last().DeviceId != reading.DeviceId)) _recent.Clear();
        _recent.Enqueue(reading);
        while (_recent.Count > 600 || _recent.TryPeek(out first) && (reading.Time - first.Time).TotalMinutes > 5) _recent.Dequeue();
        if (!record) { _suspend = null; _awaitResume = false; return; }
        if (_awaitResume && _suspend != null)
        {
            var baseline = _suspend; _suspend = null; _awaitResume = false;
            if (reading.Time > baseline.Time)
            {
                bool same = baseline.DeviceId.Length > 0 && baseline.DeviceId == reading.DeviceId;
                bool batteryOnly = same && baseline.PowerOnline == false && reading.PowerOnline == false;
                SleepRecords.Add(new(baseline.Time, reading.Time, same ? baseline.Percent - reading.Percent : null,
                    batteryOnly ? baseline.RemainingWh - reading.RemainingWh : null,
                    batteryOnly ? "睡眠前后净变化；正值为减少" : "电源状态变化或数据缺失，仅记录可用净变化"));
                if (SleepRecords.Count > 100) SleepRecords.RemoveAt(0);
                TryWrite(() => { Directory.CreateDirectory(_directory); File.WriteAllText(SleepPath, JsonSerializer.Serialize(SleepRecords)); });
            }
        }
        if ((reading.Time - _lastLogged).TotalSeconds < 30) return;
        _lastLogged = reading.Time;
        TryWrite(() =>
        {
            Directory.CreateDirectory(_directory);
            var day = DateOnly.FromDateTime(reading.Time.LocalDateTime);
            string path = Path.Combine(_directory, $"battery-{day:yyyy-MM-dd}.csv");
            if (!File.Exists(path)) File.WriteAllText(path, Header + Environment.NewLine, Encoding.UTF8);
            File.AppendAllText(path, Csv(reading) + Environment.NewLine, Encoding.UTF8);
            if (_cleanupDate != day)
            {
                _cleanupDate = day;
                foreach (var file in Directory.EnumerateFiles(_directory, "battery-????-??-??.csv"))
                    if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file)[8..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) && date < day.AddDays(-29)) File.Delete(file);
            }
        });
    }

    public void Suspend(BatteryReading? latest, bool record, DateTimeOffset? eventTime = null)
    {
        var time = eventTime ?? DateTimeOffset.Now;
        _suspend = record && latest != null && (time - latest.Time).Duration().TotalSeconds < 30 ? latest with { Time = time } : null;
        _awaitResume = false; _recent.Clear();
    }
    public void Resume() { _awaitResume = _suspend != null; _recent.Clear(); }
    public void StopRecording() { _suspend = null; _awaitResume = false; }

    public float? AverageDischargeW
    {
        get
        {
            double energy = 0, seconds = 0;
            BatteryReading? previous = null;
            foreach (var sample in _recent)
            {
                if (previous != null)
                {
                    double dt = (sample.Time - previous.Time).TotalSeconds;
                    if (dt is > 0 and <= 15 && sample.PowerOnline == false && previous.PowerOnline == false
                        && sample.PowerW is > 0 && previous.PowerW is > 0)
                    { energy += (sample.PowerW.Value + previous.PowerW.Value) / 2 * dt; seconds += dt; }
                }
                previous = sample;
            }
            return seconds >= 20 ? (float)(energy / seconds) : null;
        }
    }

    public void Export(string destination)
    {
        var files = Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory, "battery-????-??-??.csv").Order().ToArray() : Array.Empty<string>();
        if (files.Any(f => Path.GetFullPath(f).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("请选择历史记录目录以外的导出文件");
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));
        writer.WriteLine(Header);
        foreach (string file in files)
            foreach (string line in File.ReadLines(file).Skip(1)) writer.WriteLine(line);
    }
    private const string Header = "Time,Percent,DischargeW,DesignWh,FullChargeWh,RemainingWh,HealthPercent,Cycles,PowerOnline";
    private static string Csv(BatteryReading b) => string.Join(",", b.Time.ToString("O"), F(b.Percent), F(b.PowerW), F(b.DesignWh),
        F(b.FullChargeWh), F(b.RemainingWh), F(b.HealthPercent), b.Cycles?.ToString(CultureInfo.InvariantCulture) ?? "", b.PowerOnline?.ToString() ?? "");
    private static string F(float? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    private void TryWrite(Action action) { try { action(); LastError = ""; } catch (Exception ex) { LastError = "电池记录写入失败：" + ex.Message; } }
}
