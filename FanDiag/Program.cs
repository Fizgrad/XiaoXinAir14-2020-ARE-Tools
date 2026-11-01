using LibreHardwareMonitor.Hardware;
using FanController.Services;

// 诊断工具：打印所有传感器原始读数 + EC 原始字节，用于修正主程序的传感器映射

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true,
    IsStorageEnabled = true,
};
computer.Open();

// 多轮 Update，AMD SMU 传感器首轮可能不出现
for (int i = 0; i < 3; i++)
{
    foreach (var hw in computer.Hardware) hw.Update();
    Thread.Sleep(800);
}

WL("=========== 硬件与传感器 ===========");
foreach (var hw in computer.Hardware)
{
    WL($"[{hw.HardwareType}] {hw.Name}");
    foreach (var s in hw.Sensors)
    {
        string v = s.Value == null ? "null" : s.Value.Value.ToString("0.###");
        WL($"    {s.SensorType,-12} {s.Name,-40} = {v}  (id={s.Identifier})");
    }
    foreach (var sub in hw.SubHardware)
    {
        sub.Update();
        WL($"  [子硬件 {sub.HardwareType}] {sub.Name}");
        foreach (var s in sub.Sensors)
        {
            string v = s.Value == null ? "null" : s.Value.Value.ToString("0.###");
            WL($"      {s.SensorType,-12} {s.Name,-40} = {v}");
        }
    }
}

WL("");
WL("=========== EC 读取（复用主程序 EcIO / Ring0） ===========");
var ring0t = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetTypes()
    .Where(t => t.Name == "Ring0").ToList();
foreach (var t in ring0t)
{
    WL($"Ring0 候选: {t.FullName} public={t.IsPublic} static={t.IsAbstract && t.IsSealed}");
    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
        if (m.Name.Contains("IoPort") || m.Name.Contains("Mutex") || m.Name == "Open")
            WL($"  {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))}) static");
}
EcIO.Init();
WL("EcIO.IsAvailable = " + EcIO.IsAvailable + "  Status = " + EcIO.Status);
if (EcIO.IsAvailable)
{
    var data = EcIO.Dump(256);
    if (data != null)
    {
        for (int row = 0; row < 16; row++)
        {
            var line = $"0x{row * 16:X2}: ";
            for (int col = 0; col < 16; col++) line += $"{data[row * 16 + col]:X2} ";
            WL(line);
        }

        WL("");
        WL("风扇转速候选解读（EC 0x06）:");
        WL($"  8位 ×100 = {data[0x06] * 100} RPM");
        WL($"  8位 原值 = {data[0x06]} (若≈2.5-4.5千则为原始RPM)");
        WL($"  16位 0x06+0x07 = {(data[0x06] << 8) | data[0x07]}");
        WL($"  16位 0x0C+0x0D = {(data[0x0C] << 8) | data[0x0D]}");
        WL($"  16位 0x0E+0x0F = {(data[0x0E] << 8) | data[0x0F]}");
    }
    else
    {
        WL("EC Dump 失败: " + EcIO.Status);
    }
}

WL("");
WL("=========== EnergyDrv ===========");
WL(EnergyDrvFan.DriverPresent ? "EnergyDrv 设备存在" : "EnergyDrv 设备不存在");

static void WL(string s) => Console.WriteLine(s);
