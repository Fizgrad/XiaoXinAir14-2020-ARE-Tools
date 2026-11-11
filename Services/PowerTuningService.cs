using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FanController.Services;

public sealed record PowerLimits(float FastW, float SlowW, float TemperatureC)
{
    public override string ToString() => $"短时 {FastW:0.#} W · 持续 {SlowW:0.#} W · 温度上限 {TemperatureC:0.#} °C";
    internal bool Valid => float.IsFinite(FastW) && float.IsFinite(SlowW) && float.IsFinite(TemperatureC)
        && SlowW > 0 && SlowW <= FastW && FastW <= 100 && TemperatureC is >= 40 and <= 110;
    internal bool Matches(PowerLimits other) => Math.Abs(FastW - other.FastW) < 0.2f
        && Math.Abs(SlowW - other.SlowW) < 0.2f && Math.Abs(TemperatureC - other.TemperatureC) < 1;
}
public enum PowerPreset { Quiet, Balanced, Sustained }
internal sealed record CommandResult(int ExitCode, string Output);
internal interface IRyzenAdjRunner { CommandResult Run(string path, IReadOnlyList<string> arguments); }

/// <summary>Opt-in RyzenAdj adapter. Reads actual limits, clamps presets to the OEM baseline and verifies writes.</summary>
public sealed class PowerTuningService
{
    private readonly IRyzenAdjRunner _runner;
    private readonly Lazy<(bool Supported, string Description)> _identity;
    public string ExecutablePath { get; private set; }
    public PowerLimits? Current { get; private set; }
    public PowerLimits? Original { get; private set; }
    public PowerLimits? Target { get; private set; }
    public string Message { get; private set; } = "点击读取参数后启用功耗预设";
    public bool Ready => Current != null;
    public PowerTuningService(string path, CancellationToken cancellation = default, Action<string>? diagnosticLog = null)
        : this(path, new RyzenAdjRunner(cancellation, diagnosticLog), ThermalProfileService.ReadMachineIdentity) { }
    internal PowerTuningService(string path, IRyzenAdjRunner runner, Func<(bool Supported, string Description)> identity)
    { ExecutablePath = path; _runner = runner; _identity = new(identity); }

    public void SetExecutable(string path)
    {
        if (Original != null) throw new InvalidOperationException("请先恢复本次调整前参数，再更换 RyzenAdj");
        ExecutablePath = Path.GetFullPath(path); Current = null; Target = null;
        Message = "已选择 RyzenAdj，点击读取参数检查支持情况";
    }

    public bool Refresh()
    {
        try
        {
            Current = Read();
            Message = Target != null && !Target.Matches(Current) ? "检测到参数已被固件或其他工具改变；未自动重写" : "功耗参数已读取";
            return true;
        }
        catch (Exception ex) { Current = null; Message = ex.Message; return false; }
    }

    public bool Apply(PowerPreset preset)
    {
        if (!Enum.IsDefined(preset)) { Message = "未知功耗预设"; return false; }
        try
        {
            var before = Read();
            Original ??= before;
            // A preset can restore a previous reduction, but never exceed the captured firmware limits.
            var target = Preview(preset);
            try
            {
                WriteAndVerify(target); Target = target;
                Message = "预设已应用并回读确认：" + target; return true;
            }
            catch (Exception ex)
            {
                string rollback;
                try { WriteAndVerify(before); Target = before; rollback = "已恢复操作前参数"; }
                catch (Exception restoreError)
                {
                    rollback = "恢复命令异常：" + restoreError.Message;
                    // A native crash can happen after the SMU accepted a setting. Query independently.
                    try
                    {
                        Current = Read();
                        Target = null;
                        rollback += before.Matches(Current) ? "；独立回读已确认恢复操作前参数" : "；实际回读：" + Current;
                    }
                    catch (Exception readError) { Current = null; rollback += "；状态读取失败：" + readError.Message; }
                }
                Message = "预设未确认：" + ex.Message + "；" + rollback; return false;
            }
        }
        catch (Exception ex) { Current = null; Message = ex.Message; return false; }
    }

    public static PowerLimits RequestedLimits(PowerPreset preset) => preset switch
    {
        PowerPreset.Quiet => new(15, 12, 70), PowerPreset.Balanced => new(25, 18, 80),
        PowerPreset.Sustained => new(30, 25, 85), _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    public PowerLimits Preview(PowerPreset preset)
    {
        var requested = RequestedLimits(preset); var baseline = Original ?? Current;
        return baseline == null ? requested : new(Math.Min(requested.FastW, baseline.FastW),
            Math.Min(requested.SlowW, baseline.SlowW), Math.Min(requested.TemperatureC, baseline.TemperatureC));
    }

    public bool Restore()
    {
        if (Original == null) { Message = "本次运行没有可恢复的参数"; return false; }
        try { WriteAndVerify(Original); Original = null; Target = null; Message = "已恢复本次首次调整前参数并回读确认"; return true; }
        catch (Exception ex)
        {
            string actual;
            try { Current = Read(); actual = "；实际回读：" + Current; }
            catch (Exception readError) { Current = null; actual = "；状态读取失败：" + readError.Message; }
            Message = "恢复参数未确认：" + ex.Message + actual; return false;
        }
    }

    private PowerLimits Read()
    {
        if (!_identity.Value.Supported) throw new InvalidOperationException("机型或 BIOS 未适配功耗预设：" + _identity.Value.Description);
        var result = _runner.Run(ExecutablePath, new[] { "--info" });
        if (result.ExitCode != 0) throw new InvalidOperationException("RyzenAdj 读取失败：" + DescribeFailure(result));
        return Parse(result.Output) ?? throw new InvalidOperationException("RyzenAdj 未返回有效的 Renoir 功耗表，保留固件设置");
    }
    internal void WriteAndVerify(PowerLimits limits)
    {
        if (!limits.Valid) throw new InvalidOperationException("功耗参数无效，未发送设置");
        var result = _runner.Run(ExecutablePath, new[]
        {
            // RyzenAdj v0.19.0 Windows cleanup dereferences the PM-table mapping even for
            // setters. --info initializes that mapping before writes; without it cleanup
            // crashes at ryzenadj.exe+0xE0ED after settings may already have been accepted.
            "--info",
            "--fast-limit=" + Math.Round(limits.FastW * 1000).ToString(CultureInfo.InvariantCulture),
            "--slow-limit=" + Math.Round(limits.SlowW * 1000).ToString(CultureInfo.InvariantCulture),
            "--tctl-temp=" + Math.Round(limits.TemperatureC).ToString(CultureInfo.InvariantCulture),
        });
        if (result.ExitCode != 0) throw new InvalidOperationException("RyzenAdj 设置失败：" + DescribeFailure(result));
        Current = Read();
        if (!limits.Matches(Current)) throw new InvalidOperationException("即时回读未匹配：" + Current);
        Current = Read();
        if (!limits.Matches(Current)) throw new InvalidOperationException("再次回读未匹配：" + Current);
    }
    internal static PowerLimits? Parse(string text)
    {
        if (!Regex.IsMatch(text, @"CPU Family:\s*Renoir\b", RegexOptions.IgnoreCase)) return null;
        float? Value(string name)
        {
            var match = Regex.Match(text, @"\|\s*" + Regex.Escape(name) + @"\s*\|\s*([0-9]+(?:\.[0-9]+)?)\s*\|");
            return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) ? n : null;
        }
        var fast = Value("PPT LIMIT FAST"); var slow = Value("PPT LIMIT SLOW"); var temp = Value("THM LIMIT CORE");
        if (fast == null || slow == null || temp == null) return null;
        var result = new PowerLimits(fast.Value, slow.Value, temp.Value);
        return result.Valid ? result : null;
    }
    private static string Short(string output) => output.Length > 800 ? output[..800] : output;
    internal static string DescribeFailure(CommandResult result)
    {
        uint code = unchecked((uint)result.ExitCode);
        string detail = code switch
        {
            0xC0000005 => "访问违规，RyzenAdj 进程崩溃；设置可能已生效，需独立回读",
            0xC0000135 => "缺少运行依赖，请保留完整解压目录",
            0xC0000142 => "依赖初始化失败",
            _ => "进程未正常完成",
        };
        string output = Short(result.Output.Trim());
        return $"退出码 0x{code:X8}（{detail}）" + (output.Length > 0 ? "；" + output : "；进程未返回错误文本");
    }
}

internal sealed class RyzenAdjRunner : IRyzenAdjRunner
{
    private readonly CancellationToken _cancellation;
    private readonly Action<string>? _diagnosticLog;
    public RyzenAdjRunner(CancellationToken cancellation = default, Action<string>? diagnosticLog = null)
    { _cancellation = cancellation; _diagnosticLog = diagnosticLog; }
    public CommandResult Run(string path, IReadOnlyList<string> arguments)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("未找到 RyzenAdj，请选择完整解压目录中的 ryzenadj.exe");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 RyzenAdj");
        using var cancellation = _cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { try { process.Kill(true); } catch { } throw new TimeoutException("RyzenAdj 超时，操作未确认"); }
        _cancellation.ThrowIfCancellationRequested();
        var result = new CommandResult(process.ExitCode, stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult());
        if (result.ExitCode != 0)
            _diagnosticLog?.Invoke("RyzenAdj " + string.Join(" ", arguments) + "：" + PowerTuningService.DescribeFailure(result));
        return result;
    }
}
