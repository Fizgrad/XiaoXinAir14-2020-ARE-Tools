using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FanController.Services;

public sealed record PowerLimits(float FastW, float SlowW)
{
    public override string ToString() => $"短时 {FastW:0.#} W · 持续 {SlowW:0.#} W";
    // 温度上限(tctl-temp)已移除：写入 SMU 温度墙会与固件策略冲突并导致整机突然断电。
    internal bool Valid => float.IsFinite(FastW) && float.IsFinite(SlowW)
        && FastW is > 0 and <= 100 && SlowW is > 0 and <= 100;
    internal bool Matches(PowerLimits other) => Math.Abs(FastW - other.FastW) < 0.2f
        && Math.Abs(SlowW - other.SlowW) < 0.2f;
}
public enum PowerPreset { Quiet, Balanced, Sustained }
internal sealed record CommandResult(int ExitCode, string Output);
internal interface IRyzenAdjRunner { CommandResult Run(string path, IReadOnlyList<string> arguments); }

/// <summary>Opt-in RyzenAdj adapter. Reads actual limits, clamps presets to the OEM baseline and verifies writes.
/// 仅调节 PPT 短时/持续功耗，不写入温度墙（tctl-temp），温度保护交由固件。</summary>
public sealed class PowerTuningService
{
    private readonly IRyzenAdjRunner _runner;
    private readonly IRyzenAdjRunner _rollbackRunner;
    private readonly Lazy<(bool Supported, string Description)> _identity;
    private readonly Action<string>? _diagnosticLog;
    // 固件自身限值的持久化通道（由界面注入，服务本身不依赖设置类型）。
    private readonly Func<PowerLimits?>? _readFirmwareBaseline;
    private readonly Action<PowerLimits>? _writeFirmwareBaseline;
    public string ExecutablePath { get; private set; }
    public PowerLimits? Current { get; private set; }
    /// <summary>恢复基准：首次应用预设时的固件限值；若当前值是上次遗留的预设，则取此前记录的固件值。</summary>
    public PowerLimits? Original { get; private set; }
    public PowerLimits? Target { get; private set; }
    /// <summary>恢复基准的判定说明（仅在当前限值被判定为上次遗留的预设时非空）。</summary>
    public string BaselineNote { get; private set; } = "";
    public string Message { get; private set; } = "点击读取参数后启用功耗预设";
    public bool Ready => Current != null;
    public PowerTuningService(string path, CancellationToken cancellation = default, Action<string>? diagnosticLog = null,
        Func<PowerLimits?>? readFirmwareBaseline = null, Action<PowerLimits>? writeFirmwareBaseline = null)
        : this(path, new RyzenAdjRunner(cancellation, diagnosticLog), ThermalProfileService.ReadMachineIdentity, diagnosticLog,
            new RyzenAdjRunner(CancellationToken.None, diagnosticLog), readFirmwareBaseline, writeFirmwareBaseline) { }
    internal PowerTuningService(string path, IRyzenAdjRunner runner, Func<(bool Supported, string Description)> identity,
        Action<string>? diagnosticLog = null, IRyzenAdjRunner? rollbackRunner = null,
        Func<PowerLimits?>? readFirmwareBaseline = null, Action<PowerLimits>? writeFirmwareBaseline = null)
    {
        ExecutablePath = path; _runner = runner; _rollbackRunner = rollbackRunner ?? runner;
        _identity = new(identity); _diagnosticLog = diagnosticLog;
        _readFirmwareBaseline = readFirmwareBaseline; _writeFirmwareBaseline = writeFirmwareBaseline;
    }

    public void SetExecutable(string path)
    {
        if (Original != null) throw new InvalidOperationException("请先恢复本次调整前参数，再更换 RyzenAdj");
        ExecutablePath = Path.GetFullPath(path); Current = null; Target = null; BaselineNote = "";
        Message = "已选择 RyzenAdj，点击读取参数检查支持情况";
    }

    // Measured on DTCN27WW: the preset does not expire on its own (held for 4 min under load and 1 min
    // idle), but any Fn+Q profile change (hotkey, vendor tool or this app's automation) makes the
    // firmware re-assert its own PPT limits and silently discards it. This only reports; it never rewrites.
    public bool Refresh()
    {
        try
        {
            Current = Read();
            Message = Target != null && !Target.Matches(Current)
                ? "上次设置的预设已被固件覆盖（通常是 Fn+Q 散热模式切换）；未自动重写，可手动重新应用"
                : Target != null ? "功耗参数已读取，与上次设置的预设一致" : "功耗参数已读取";
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
            Original ??= CaptureBaseline(before);
            // A preset can restore a previous reduction, but never exceed the captured firmware limits.
            var target = Preview(preset);
            try
            {
                WriteAndVerify(target); Target = target;
                Message = "预设已应用并回读确认：" + target + (BaselineNote.Length > 0 ? "；" + BaselineNote : ""); return true;
            }
            catch (Exception ex)
            {
                string rollback;
                // Rollback must run even when the caller's token was cancelled (e.g. shutdown
                // while a write was in flight), so it uses a runner without cancellation.
                try { WriteAndVerify(before, _rollbackRunner); Target = before; rollback = "已恢复操作前参数"; }
                catch (Exception restoreError)
                {
                    rollback = "恢复命令异常：" + restoreError.Message;
                    // A native crash can happen after the SMU accepted a setting. Query independently.
                    try
                    {
                        Current = Read(_rollbackRunner);
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
        PowerPreset.Quiet => new(15, 12), PowerPreset.Balanced => new(25, 18),
        PowerPreset.Sustained => new(30, 25), _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    public PowerLimits Preview(PowerPreset preset)
    {
        var requested = RequestedLimits(preset); var baseline = Original ?? Current;
        return baseline == null ? requested : new(Math.Min(requested.FastW, baseline.FastW),
            Math.Min(requested.SlowW, baseline.SlowW));
    }

    public bool Restore()
    {
        if (Original == null) { Message = "本次运行没有可恢复的参数"; return false; }
        try { WriteAndVerify(Original); Original = null; Target = null; BaselineNote = ""; Message = "已恢复本次首次调整前参数并回读确认"; return true; }
        catch (Exception ex)
        {
            string actual;
            try { Current = Read(_rollbackRunner); actual = "；实际回读：" + Current; }
            catch (Exception readError) { Current = null; actual = "；状态读取失败：" + readError.Message; }
            Message = "恢复参数未确认：" + ex.Message + actual; return false;
        }
    }

    private PowerLimits Read(IRyzenAdjRunner? runner = null)
    {
        var source = runner ?? _runner;
        if (!_identity.Value.Supported) throw new InvalidOperationException("机型或 BIOS 未适配功耗预设：" + _identity.Value.Description);
        var result = source.Run(ExecutablePath, new[] { "--info" });
        if (result.ExitCode != 0) throw new InvalidOperationException("RyzenAdj 读取失败：" + DescribeFailure(result));
        var limits = Parse(result.Output);
        if (limits != null) { RememberFirmwareLimits(limits); return limits; }
        _diagnosticLog?.Invoke("RyzenAdj --info 解析失败，原始输出：\n" + (result.Output.Length <= 8192 ? result.Output : result.Output[..8192]));
        throw new InvalidOperationException("RyzenAdj 返回的功耗参数格式或数值无效");
    }

    /// <summary>限值恰好等于某个预设，视为预设值（固件自身的限值不会与预设完全相同）。</summary>
    internal static bool LooksLikePreset(PowerLimits limits) => Enum.GetValues<PowerPreset>().Any(p => RequestedLimits(p).Matches(limits));

    /// <summary>读到不等于任何预设的限值时，视为固件自身下发并交给界面持久化。
    /// 持久化失败不影响读写，只失去跨运行的恢复基准。</summary>
    private void RememberFirmwareLimits(PowerLimits limits)
    {
        if (_writeFirmwareBaseline == null || !limits.Valid || LooksLikePreset(limits)) return;
        try
        {
            if (_readFirmwareBaseline?.Invoke() is { } known && known.Matches(limits)) return;
            _writeFirmwareBaseline(limits);
        }
        catch { }
    }

    /// <summary>确定恢复基准。预设会保留到重启或 Fn+Q 切换，所以新一次运行读到的“当前值”可能正是上次遗留的预设；
    /// 若直接拿它当基准，整组预设都会被钳在遗留值上，“恢复”也只能恢复到遗留值。</summary>
    private PowerLimits CaptureBaseline(PowerLimits before)
    {
        BaselineNote = "";
        if (!LooksLikePreset(before)) return before;
        var firmware = _readFirmwareBaseline?.Invoke();
        if (firmware is { } f && f.Valid && (f.FastW > before.FastW || f.SlowW > before.SlowW))
        {
            BaselineNote = "当前限值与预设相同，判定为上次运行遗留；恢复基准采用此前记录的固件值 " + f;
            return f;
        }
        BaselineNote = "当前限值与预设相同，可能是上次运行遗留，本次以它为恢复基准；如需回到固件默认，请切换一次 Fn+Q 后重新读取参数";
        return before;
    }
    internal void WriteAndVerify(PowerLimits limits, IRyzenAdjRunner? runner = null)
    {
        var source = runner ?? _runner;
        if (!limits.Valid) throw new InvalidOperationException("功耗参数无效，未发送设置");
        var result = source.Run(ExecutablePath, new[]
        {
            // RyzenAdj v0.19.0 Windows cleanup dereferences the PM-table mapping even for
            // setters. --info initializes that mapping before writes; without it cleanup
            // crashes at ryzenadj.exe+0xE0ED after settings may already have been accepted.
            "--info",
            "--fast-limit=" + Math.Round(limits.FastW * 1000).ToString(CultureInfo.InvariantCulture),
            "--slow-limit=" + Math.Round(limits.SlowW * 1000).ToString(CultureInfo.InvariantCulture),
            // 不再下发 --tctl-temp：改写 SMU 温度墙会让固件失去原有的温度保护节奏，
            // 在负载切换时整机突然断电。温度保护完全交给固件。
        });
        if (result.ExitCode != 0) throw new InvalidOperationException("RyzenAdj 设置失败：" + DescribeFailure(result));
        Current = Read(runner);
        if (!limits.Matches(Current)) throw new InvalidOperationException("即时回读未匹配：" + Current);
        Current = Read(runner);
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
        var fast = Value("PPT LIMIT FAST"); var slow = Value("PPT LIMIT SLOW");
        if (fast == null || slow == null) return null;
        var result = new PowerLimits(fast.Value, slow.Value);
        return result.Valid ? result : null;
    }

    /// <summary>只读：固件当前的 THM LIMIT CORE（温度墙）。仅用于硬件信息页展示，从不写入。</summary>
    internal static float? ParseThermalLimit(string text)
    {
        var match = Regex.Match(text, @"\|\s*THM LIMIT CORE\s*\|\s*([0-9]+(?:\.[0-9]+)?)\s*\|");
        if (!match.Success || !float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return null;
        return value is >= 40 and <= 110 ? value : null;
    }

    /// <summary>只读读取固件温度墙：只执行 RyzenAdj --info，不发送任何设置项。</summary>
    public static (float? LimitC, string Message) ReadFirmwareThermalLimit(string executablePath)
        => ReadFirmwareThermalLimit(executablePath, new RyzenAdjRunner());

    internal static (float? LimitC, string Message) ReadFirmwareThermalLimit(string executablePath, IRyzenAdjRunner runner)
    {
        try
        {
            var result = runner.Run(executablePath, new[] { "--info" });
            if (result.ExitCode != 0) return (null, "RyzenAdj 读取失败：" + DescribeFailure(result));
            var limit = ParseThermalLimit(result.Output);
            return limit.HasValue ? (limit, "") : (null, "RyzenAdj --info 未返回有效的 THM LIMIT CORE");
        }
        catch (Exception ex) { return (null, ex.Message); }
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
