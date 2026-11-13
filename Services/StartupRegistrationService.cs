using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using FanController.Models;

namespace FanController.Services;

public sealed record StartupRegistration(bool Enabled, bool MatchesApplication, string ExecutablePath);
public sealed record StartupSaveResult(bool Success, string Message);
internal interface IStartupTaskStore
{
    string? Read(string name);
    void Write(string name, string definition);
    void Delete(string name);
}

public sealed class StartupRegistrationService
{
    public const string TaskPrefix = "FanController.Startup.";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly IStartupTaskStore _store;
    private readonly string _executable, _userId;
    public string TaskName { get; }

    public StartupRegistrationService(string executable)
        : this(new ScheduledTaskStore(), executable, WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法读取当前 Windows 账户"), null) { }
    internal StartupRegistrationService(IStartupTaskStore store, string executable, string userId, string? taskName)
    { _store = store; _executable = Path.GetFullPath(executable); _userId = userId; TaskName = taskName ?? TaskPrefix + userId; }

    public StartupRegistration Read()
    {
        string? definition = _store.Read(TaskName);
        if (definition == null) return new(false, false, "");
        var root = XDocument.Parse(definition).Root!;
        string command = root.Descendants(Ns + "Exec").Elements(Ns + "Command").FirstOrDefault()?.Value ?? "";
        bool enabled = !string.Equals(root.Element(Ns + "Settings")?.Element(Ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase)
            && root.Descendants(Ns + "LogonTrigger").Any(t => !string.Equals(t.Element(Ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase));
        bool matches = string.Equals(command, _executable, StringComparison.OrdinalIgnoreCase)
            && root.Descendants(Ns + "Exec").Elements(Ns + "Arguments").Any(a => a.Value == "--startup")
            && root.Descendants(Ns + "Principal").Any(p => p.Element(Ns + "UserId")?.Value == _userId
                && p.Element(Ns + "LogonType")?.Value == "InteractiveToken" && p.Element(Ns + "RunLevel")?.Value == "HighestAvailable");
        return new(enabled, matches, command);
    }

    public async Task<StartupSaveResult> SaveAsync(AppSettings settings, bool enabled, PowerPreset? preset)
    {
        if (preset.HasValue && !Enum.IsDefined(preset.Value)) return new(false, "无效的启动功耗预设");
        bool previousEnabled = settings.StartWithWindows; var previousPreset = settings.StartupPowerPreset;
        string? previousDefinition = null; bool captured = false;
        try
        {
            previousDefinition = await Task.Run(() => _store.Read(TaskName)); captured = true;
            await Task.Run(() =>
            {
                if (enabled) _store.Write(TaskName, BuildDefinition(_executable, _userId));
                else if (previousDefinition != null) _store.Delete(TaskName);
                var actual = Read();
                if (actual.Enabled != enabled || (enabled && !actual.MatchesApplication)) throw new InvalidOperationException("启动项保存后检查失败");
            });
            settings.StartWithWindows = enabled; settings.StartupPowerPreset = preset;
            if (!settings.TrySave(out string error)) throw new IOException(error);
            return new(true, enabled ? "登录 Windows 后显示主窗口" : "开机启动已关闭");
        }
        catch (Exception ex)
        {
            settings.StartWithWindows = previousEnabled; settings.StartupPowerPreset = previousPreset;
            string detail = ex.Message;
            if (captured)
            {
                try { await Task.Run(() => { if (previousDefinition == null) _store.Delete(TaskName); else _store.Write(TaskName, previousDefinition); }); }
                catch (Exception restoreError) { detail += "；恢复启动项失败：" + restoreError.Message; }
            }
            return new(false, detail);
        }
    }

    internal static string BuildDefinition(string executable, string userId) => new XDocument(
        new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", "FanController 登录启动")),
            new XElement(Ns + "Triggers", new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", true), new XElement(Ns + "UserId", userId), new XElement(Ns + "Delay", "PT10S"))),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "CurrentUser"),
                new XElement(Ns + "UserId", userId), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", false), new XElement(Ns + "StopIfGoingOnBatteries", false),
                new XElement(Ns + "AllowHardTerminate", false), new XElement(Ns + "StartWhenAvailable", true),
                new XElement(Ns + "Enabled", true), new XElement(Ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(Ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(Ns + "Exec",
                new XElement(Ns + "Command", executable), new XElement(Ns + "Arguments", "--startup"),
                new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(executable)))))).ToString();

    public void RemoveForInstallation() => ScheduledTaskStore.RemoveForExecutable(_executable);
}

internal sealed class ScheduledTaskStore : IStartupTaskStore
{
    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows 计划任务不可用"));
            ((dynamic)service!).Connect(); folder = ((dynamic)service!).GetFolder("\\"); return action((dynamic)folder);
        }
        finally { if (folder != null) Marshal.FinalReleaseComObject(folder); if (service != null) Marshal.FinalReleaseComObject(service); }
    }
    public string? Read(string name) => WithFolder<string?>(folder =>
    {
        object? task = null;
        try { task = folder.GetTask(name); return (string)((dynamic)task).Xml; }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
        finally { if (task != null) Marshal.FinalReleaseComObject(task); }
    });
    public void Write(string name, string definition) => WithFolder(folder =>
    {
        object task = folder.RegisterTask(name, definition, 6, null, null, 3, null);
        Marshal.FinalReleaseComObject(task); return true;
    });
    public void Delete(string name) => WithFolder(folder =>
    {
        try { folder.DeleteTask(name, 0); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
        return true;
    });
    internal static void RemoveForExecutable(string executable) => WithFolder(folder =>
    {
        object tasks = folder.GetTasks(1);
        try
        {
            for (int i = ((dynamic)tasks).Count; i >= 1; i--)
            {
                object task = ((dynamic)tasks).Item(i);
                try
                {
                    string name = ((dynamic)task).Name;
                    if (!name.StartsWith(StartupRegistrationService.TaskPrefix, StringComparison.Ordinal)) continue;
                    var definition = XDocument.Parse((string)((dynamic)task).Xml);
                    if (definition.Descendants().Any(n => n.Name.LocalName == "Command" && n.Value.Equals(executable, StringComparison.OrdinalIgnoreCase))) folder.DeleteTask(name, 0);
                }
                finally { Marshal.FinalReleaseComObject(task); }
            }
        }
        finally { Marshal.FinalReleaseComObject(tasks); }
        return true;
    });
}
