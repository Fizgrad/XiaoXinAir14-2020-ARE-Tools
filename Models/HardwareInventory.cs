namespace FanController.Models;

public enum HardwareStatus { Normal, Attention, Error, Unknown }
public sealed record HardwareField(string Name, string Value);
public sealed record HardwareItem(string Category, string Name, HardwareStatus Status, string State, IReadOnlyList<HardwareField> Fields);
public sealed record HardwareHealthCheck(string Name, HardwareStatus Status, string Detail);
public sealed record HardwareSourceIssue(string Source, string Detail);
public sealed record HardwareInventoryReport(DateTimeOffset GeneratedAt, string Computer, string AppVersion,
    IReadOnlyList<HardwareItem> Devices, IReadOnlyList<HardwareHealthCheck> Checks, IReadOnlyList<HardwareSourceIssue> Issues);
