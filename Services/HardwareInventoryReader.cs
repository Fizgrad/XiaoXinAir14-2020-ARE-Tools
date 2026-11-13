using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using FanController.Models;

namespace FanController.Services;

internal interface IHardwareInventoryProbe
{
    IReadOnlyList<Dictionary<string, object?>> Query(string scope, string query, CancellationToken cancellation);
    BatteryReading ReadBattery();
}

internal sealed class WindowsHardwareInventoryProbe : IHardwareInventoryProbe
{
    public IReadOnlyList<Dictionary<string, object?>> Query(string scope, string query, CancellationToken cancellation)
    {
        using var search = new ManagementObjectSearcher(scope, query);
        search.Options.Timeout = TimeSpan.FromSeconds(12);
        using var rows = search.Get();
        var result = new List<Dictionary<string, object?>>();
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                cancellation.ThrowIfCancellationRequested();
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in row.Properties) values[property.Name] = property.Value;
                result.Add(values);
            }
        }
        if (result.Count == 0 && query.Contains("FROM MSFT_StorageReliabilityCounter", StringComparison.OrdinalIgnoreCase))
        {
            using var disks = new ManagementObjectSearcher(scope, "SELECT * FROM MSFT_PhysicalDisk");
            using var physicalDisks = disks.Get();
            using var commands = new ManagementClass(scope, "PS_StorageCmdlets", null);
            foreach (ManagementObject disk in physicalDisks)
            {
                using (disk)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var input = commands.GetMethodParameters("GetStorageReliabilityCounter"); input["PhysicalDisk"] = disk;
                    using var output = commands.InvokeMethod("GetStorageReliabilityCounter", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(12) });
                    if (output?["StorageReliabilityCounter"] is ManagementBaseObject counter)
                    {
                        using (counter)
                        {
                            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (PropertyData property in counter.Properties) values[property.Name] = property.Value;
                            result.Add(values);
                        }
                    }
                }
            }
        }
        return result;
    }
    public BatteryReading ReadBattery() => BatteryReader.Read();
}

public sealed class HardwareInventoryReader
{
    private readonly IHardwareInventoryProbe _probe;
    public HardwareInventoryReader() : this(new WindowsHardwareInventoryProbe()) { }
    internal HardwareInventoryReader(IHardwareInventoryProbe probe) => _probe = probe;

    public HardwareInventoryReport Scan(CancellationToken cancellation = default, IProgress<string>? progress = null)
    {
        var devices = new List<HardwareItem>(); var checks = new List<HardwareHealthCheck>(); var issues = new List<HardwareSourceIssue>();
        IReadOnlyList<Dictionary<string, object?>> Read(string source, string query, string scope = "root\\cimv2")
        {
            cancellation.ThrowIfCancellationRequested(); progress?.Report("正在读取" + source + "…");
            try { return _probe.Query(scope, query, cancellation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { issues.Add(new(source, ex.Message)); return Array.Empty<Dictionary<string, object?>>(); }
        }
        static HardwareField F(string name, object? value) => new(name, Display(value));
        void Add(string category, string name, Dictionary<string, object?> row, params HardwareField[] fields)
        {
            var status = DeviceStatus(row); devices.Add(new(category, Empty(name, category), status, status == HardwareStatus.Unknown ? "已识别" : StatusName(status), fields));
        }

        var systems = Read("整机与系统", "SELECT Manufacturer,Model,SystemType,TotalPhysicalMemory FROM Win32_ComputerSystem");
        foreach (var row in systems) Add("整机与系统", Text(row, "Manufacturer") + " " + Text(row, "Model"), row,
            F("制造商", Value(row, "Manufacturer")), F("型号", Value(row, "Model")), F("架构", Value(row, "SystemType")), F("物理内存", Bytes(Number(row, "TotalPhysicalMemory"))));
        foreach (var row in Read("操作系统", "SELECT Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime,TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"))
        {
            Add("整机与系统", Text(row, "Caption"), row, F("系统版本", Value(row, "Version")), F("内部版本", Value(row, "BuildNumber")),
                F("系统架构", Value(row, "OSArchitecture")), F("上次启动", Date(Value(row, "LastBootUpTime"))), F("可用内存", Bytes(Number(row, "FreePhysicalMemory") * 1024)));
        }
        foreach (var row in Read("处理器", "SELECT Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,SocketDesignation,ProcessorId,Status FROM Win32_Processor"))
            Add("处理器", Text(row, "Name"), row, F("制造商", Value(row, "Manufacturer")), F("核心 / 线程", Display(Value(row, "NumberOfCores")) + " / " + Display(Value(row, "NumberOfLogicalProcessors"))),
                F("最高标称频率", PositiveUnit(Number(row, "MaxClockSpeed"), " MHz")), F("插槽", Value(row, "SocketDesignation")), F("处理器 ID", Value(row, "ProcessorId")));
        foreach (var row in Read("主板", "SELECT Manufacturer,Product,Version,SerialNumber,Status FROM Win32_BaseBoard"))
            Add("主板与 BIOS", Text(row, "Product"), row, F("制造商", Value(row, "Manufacturer")), F("版本", Value(row, "Version")), F("序列号", Value(row, "SerialNumber")));
        foreach (var row in Read("BIOS", "SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate,SerialNumber,Status FROM Win32_BIOS"))
            Add("主板与 BIOS", "BIOS " + Text(row, "SMBIOSBIOSVersion"), row, F("厂商", Value(row, "Manufacturer")), F("版本", Value(row, "SMBIOSBIOSVersion")),
                F("发布日期", Date(Value(row, "ReleaseDate"))), F("序列号", Value(row, "SerialNumber")));
        foreach (var row in Read("内存", "SELECT Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType,DeviceLocator,FormFactor,SerialNumber,Status FROM Win32_PhysicalMemory"))
            Add("内存", Empty(Text(row, "Manufacturer") + " " + Text(row, "PartNumber"), "内存模块"), row,
                F("容量", Bytes(Number(row, "Capacity"))), F("类型", MemoryType(Number(row, "SMBIOSMemoryType"))), F("标称速度", PositiveUnit(Number(row, "Speed"), " MHz")),
                F("配置速度", PositiveUnit(Number(row, "ConfiguredClockSpeed"), " MHz")), F("位置", Value(row, "DeviceLocator")), F("型号", Value(row, "PartNumber")), F("序列号", Value(row, "SerialNumber")));
        foreach (var row in Read("显卡", "SELECT Name,AdapterCompatibility,AdapterRAM,DriverVersion,DriverDate,VideoProcessor,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,PNPDeviceID,Status,ConfigManagerErrorCode FROM Win32_VideoController"))
            Add("显卡", Text(row, "Name"), row, F("厂商", Value(row, "AdapterCompatibility")), F("图形处理器", Value(row, "VideoProcessor")), F("显存", Bytes(Number(row, "AdapterRAM"))),
                F("驱动版本", Value(row, "DriverVersion")), F("驱动日期", Date(Value(row, "DriverDate"))),
                F("当前输出", PositiveUnit(Number(row, "CurrentHorizontalResolution"), "") + " × " + PositiveUnit(Number(row, "CurrentVerticalResolution"), "") + " · " + PositiveUnit(Number(row, "CurrentRefreshRate"), " Hz")));

        var physical = Read("存储健康", "SELECT DeviceId,FriendlyName,SerialNumber,Size,MediaType,BusType,HealthStatus,OperationalStatus,FirmwareVersion FROM MSFT_PhysicalDisk", "root\\Microsoft\\Windows\\Storage");
        var reliability = Read("硬盘可靠性", "SELECT DeviceId,Temperature,TemperatureMax,Wear,PowerOnHours,ReadErrorsUncorrected,WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter", "root\\Microsoft\\Windows\\Storage");
        var disks = Read("硬盘", "SELECT Index,Model,SerialNumber,Size,InterfaceType,FirmwareRevision,PNPDeviceID,Status FROM Win32_DiskDrive");
        foreach (var row in disks)
        {
            var storage = physical.FirstOrDefault(p => MatchDisk(row, p));
            var counter = storage == null ? null : reliability.FirstOrDefault(c => Text(c, "DeviceId") == Text(storage, "DeviceId"));
            var fields = new List<HardwareField> { F("容量", Bytes(Number(row, "Size"))), F("序列号", Value(row, "SerialNumber")), F("固件", Value(row, "FirmwareRevision")),
                F("接口", storage == null ? Value(row, "InterfaceType") : BusType(Number(storage, "BusType"))), F("介质", storage == null ? null : MediaType(Number(storage, "MediaType"))) };
            if (counter != null)
            {
                fields.AddRange(new[] { F("温度", PositiveUnit(Number(counter, "Temperature"), " °C")), F("通电时间", NonnegativeUnit(Number(counter, "PowerOnHours"), " 小时")),
                    F("寿命消耗", NonnegativeUnit(Number(counter, "Wear"), " %")), F("不可纠正读错误", Value(counter, "ReadErrorsUncorrected")), F("不可纠正写错误", Value(counter, "WriteErrorsUncorrected")) });
                bool errors = Number(counter, "ReadErrorsUncorrected") > 0 || Number(counter, "WriteErrorsUncorrected") > 0;
                if (errors) checks.Add(new(Text(row, "Model") + " · 可靠性", HardwareStatus.Attention, "存在不可纠正读写错误"));
                if (Number(counter, "Wear") >= 100) checks.Add(new(Text(row, "Model") + " · 寿命消耗", HardwareStatus.Attention, "寿命消耗已达到 100%"));
            }
            else checks.Add(new(Text(row, "Model") + " · 可靠性计数", HardwareStatus.Unknown, "暂无可靠性数据"));
            var health = storage == null ? HardwareStatus.Unknown : StorageStatus(Number(storage, "HealthStatus"));
            devices.Add(new("硬盘", Empty(Text(row, "Model"), "物理硬盘"), health, StatusName(health), fields));
            checks.Add(new(Text(row, "Model") + " · 存储健康", health, health == HardwareStatus.Unknown ? "暂无健康数据" : "Windows 存储状态：" + StatusName(health)));
        }
        foreach (var row in Read("磁盘空间", "SELECT DeviceID,VolumeName,Size,FreeSpace,FileSystem,Status FROM Win32_LogicalDisk WHERE DriveType=3"))
        {
            double? size = Number(row, "Size"), free = Number(row, "FreeSpace"); double? percent = size > 0 && free.HasValue ? free / size * 100 : null;
            var status = percent.HasValue ? percent < 10 ? HardwareStatus.Attention : HardwareStatus.Normal : HardwareStatus.Unknown;
            devices.Add(new("硬盘", Text(row, "DeviceID") + " " + Text(row, "VolumeName"), status, status == HardwareStatus.Attention ? "空间不足" : StatusName(status),
                new[] { F("文件系统", Value(row, "FileSystem")), F("容量", Bytes(size)), F("可用空间", Bytes(free)), F("可用比例", NonnegativeUnit(percent, " %")) }));
            checks.Add(new(Text(row, "DeviceID") + " · 可用空间", status, percent.HasValue ? $"可用 {percent:0.0}% · 提醒阈值 10%" : "暂无空间数据"));
        }
        var predictions = Read("SMART 故障预测", "SELECT InstanceName,PredictFailure,Reason FROM MSStorageDriver_FailurePredictStatus", "root\\wmi");
        if (predictions.Count == 0) checks.Add(new("SMART 故障预测", HardwareStatus.Unknown, "暂无故障预测数据"));
        foreach (var row in predictions)
        {
            bool? failure = Value(row, "PredictFailure") as bool?;
            checks.Add(new("SMART · " + Text(row, "InstanceName"), failure.HasValue ? failure.Value ? HardwareStatus.Error : HardwareStatus.Normal : HardwareStatus.Unknown,
                failure.HasValue ? failure.Value ? "检测到故障预测信号" : "未触发故障预测" : "暂无故障预测数据"));
        }
        progress?.Report("正在读取电池…");
        try
        {
            var battery = _probe.ReadBattery();
            if (battery.DesignWh.HasValue || battery.Percent.HasValue)
            {
                var status = battery.HealthPercent.HasValue ? battery.HealthPercent < 80 ? HardwareStatus.Attention : HardwareStatus.Normal : HardwareStatus.Unknown;
                devices.Add(new("电池", "内置电池", status, StatusName(status), new[] { F("电量", NonnegativeUnit(battery.Percent, " %")), F("健康度", NonnegativeUnit(battery.HealthPercent, " %")),
                    F("设计容量", PositiveUnit(battery.DesignWh, " Wh")), F("满充容量", PositiveUnit(battery.FullChargeWh, " Wh")), F("循环次数", battery.Cycles), F("电池功率", battery.PowerW.HasValue ? battery.PowerW.Value.ToString("0.0") + " W" : null) }));
                checks.Add(new("电池容量保持率", status, battery.HealthPercent.HasValue ? $"{battery.HealthPercent:0.0}% · 提醒阈值 80%" : "暂无容量数据"));
            }
        }
        catch (Exception ex) { issues.Add(new("电池", ex.Message)); }

        foreach (var row in Read("显示器", "SELECT InstanceName,Active,ManufacturerName,ProductCodeID,SerialNumberID,UserFriendlyName FROM WmiMonitorID", "root\\wmi"))
            devices.Add(new("显示器", Empty(Edid(Value(row, "UserFriendlyName")), Edid(Value(row, "ManufacturerName")) + " " + Edid(Value(row, "ProductCodeID"))), HardwareStatus.Unknown,
                Value(row, "Active") is true ? "已连接" : "未激活", new[] { F("制造商", Edid(Value(row, "ManufacturerName"))), F("产品代码", Edid(Value(row, "ProductCodeID"))),
                    F("序列号", Edid(Value(row, "SerialNumberID"))), F("设备 ID", Value(row, "InstanceName")) }));
        foreach (var row in Read("网络", "SELECT Name,Manufacturer,MACAddress,NetConnectionID,Speed,NetEnabled,PNPDeviceID,Status,ConfigManagerErrorCode FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE"))
            Add("网络", Text(row, "Name"), row, F("制造商", Value(row, "Manufacturer")), F("连接名称", Value(row, "NetConnectionID")), F("MAC 地址", Value(row, "MACAddress")),
                F("链路速度", Number(row, "Speed") > 0 ? $"{Number(row, "Speed") / 1000000:0.#} Mbps" : null), F("已启用", Value(row, "NetEnabled") is bool on ? on ? "是" : "否" : null));
        foreach (var row in Read("声卡", "SELECT Name,Manufacturer,PNPDeviceID,Status,ConfigManagerErrorCode FROM Win32_SoundDevice"))
            Add("音频", Text(row, "Name"), row, F("制造商", Value(row, "Manufacturer")), F("设备 ID", Value(row, "PNPDeviceID")));

        var pnp = Read("设备管理器", "SELECT Name,Manufacturer,PNPClass,DeviceID,Status,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Present=TRUE");
        foreach (var row in pnp)
        {
            string kind = Text(row, "PNPClass"), id = Text(row, "DeviceID");
            string? category = kind.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "蓝牙" : id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || kind.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : null;
            var status = DeviceStatus(row);
            if (category != null || status is HardwareStatus.Error or HardwareStatus.Attention)
                Add(category ?? "异常设备", Text(row, "Name"), row, F("设备类别", kind), F("制造商", Value(row, "Manufacturer")), F("设备 ID", id), F("设备错误码", Value(row, "ConfigManagerErrorCode")));
        }
        var knownDevices = pnp.Where(p => Number(p, "ConfigManagerErrorCode").HasValue).ToArray();
        var problemDevices = knownDevices.Where(p => Number(p, "ConfigManagerErrorCode") != 0).ToArray();
        checks.Insert(0, new("设备管理器", knownDevices.Length == 0 ? HardwareStatus.Unknown : problemDevices.Length > 0 ? HardwareStatus.Attention : HardwareStatus.Normal,
            knownDevices.Length == 0 ? "暂无设备状态数据" : $"检查 {knownDevices.Length} 个已连接设备 · {problemDevices.Length} 个设备报告错误"));
        if (disks.Count == 0) checks.Add(new("存储健康", HardwareStatus.Unknown, "未读取到物理硬盘"));
        cancellation.ThrowIfCancellationRequested();
        return new(DateTimeOffset.Now, Environment.MachineName, typeof(HardwareInventoryReader).Assembly.GetName().Version?.ToString(3) ?? "1.3.0", devices, checks, issues);
    }

    internal static object? Value(Dictionary<string, object?> row, string key) => row.GetValueOrDefault(key);
    internal static string Text(Dictionary<string, object?> row, string key) => Convert.ToString(Value(row, key), CultureInfo.InvariantCulture)?.Trim() ?? "";
    internal static double? Number(Dictionary<string, object?> row, string key)
    {
        try { object? value = Value(row, key); if (value == null) return null; double result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return double.IsFinite(result) ? result : null; }
        catch { return null; }
    }
    internal static HardwareStatus DeviceStatus(Dictionary<string, object?> row)
    {
        double? code = Number(row, "ConfigManagerErrorCode");
        if (code.HasValue) return code == 0 ? HardwareStatus.Normal : code == 22 ? HardwareStatus.Attention : HardwareStatus.Error;
        return Text(row, "Status").ToUpperInvariant() switch { "OK" => HardwareStatus.Normal, "ERROR" => HardwareStatus.Error, "DEGRADED" or "PRED FAIL" => HardwareStatus.Attention, _ => HardwareStatus.Unknown };
    }
    internal static HardwareStatus StorageStatus(double? code) => code switch { 0 => HardwareStatus.Normal, 1 => HardwareStatus.Attention, 2 => HardwareStatus.Error, _ => HardwareStatus.Unknown };
    internal static bool MatchDisk(Dictionary<string, object?> disk, Dictionary<string, object?> physical)
    {
        string serial = Text(disk, "SerialNumber");
        if (serial.Length > 0 && Text(physical, "SerialNumber").Length > 0) return serial.Equals(Text(physical, "SerialNumber"), StringComparison.OrdinalIgnoreCase);
        return Text(disk, "Index") == Text(physical, "DeviceId") && Number(disk, "Size") is { } size && size == Number(physical, "Size");
    }
    public static string StatusName(HardwareStatus status) => status switch { HardwareStatus.Normal => "正常", HardwareStatus.Attention => "需关注", HardwareStatus.Error => "异常", _ => "暂无数据" };
    private static string Display(object? value) => Empty(Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim(), "--");
    private static string Empty(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string Bytes(double? value) => value is > 0 ? value >= 1024 * 1024 * 1024 ? $"{value / (1024 * 1024 * 1024):0.##} GiB" : $"{value / (1024 * 1024):0.##} MiB" : "--";
    private static string PositiveUnit(double? value, string unit) => value is > 0 ? value.Value.ToString("0.##") + unit : "--";
    private static string NonnegativeUnit(double? value, string unit) => value is >= 0 ? value.Value.ToString("0.##") + unit : "--";
    private static string Date(object? value)
    {
        try { return value is string text && text.Length >= 14 ? ManagementDateTimeConverter.ToDateTime(text).ToString("yyyy-MM-dd HH:mm") : "--"; }
        catch { return "--"; }
    }
    private static string Edid(object? value) => value is Array array ? new string(array.Cast<object>().Select(v => (char)Convert.ToUInt16(v)).Where(c => c != '\0').ToArray()).Trim() : "";
    private static string MemoryType(double? value) => value switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "--" };
    private static string MediaType(double? value) => value switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "--" };
    private static string BusType(double? value) => value switch { 1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 14 => "虚拟磁盘", 17 => "NVMe", _ => "--" };
}
