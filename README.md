# FanController — 小新 Air 14 (R5 4600U) 风扇控制与硬件监控

一个为联想小新 Air 14 2020 锐龙版（Ryzen 5 4600U，对应海外版 IdeaPad 5 14ARE05）开发的 Windows 桌面工具：

- **仪表盘**：CPU 温度、核显温度、Package/SOC 功耗、GPU 功耗、CPU 频率/利用率、电池功率，通栏行式布局，每行 ↑↓ 按钮自定义顺序（自动保存）
- **全速散热**：复用联想 EnergyDrv 驱动的"除尘模式"，保持风扇持续全速（默认最长 30 分钟自动停止）
- **EC 工具**：EC 内存 dump / 双快照对比，观察 EC 内部状态

注：本机（14ARE05）EC 不向操作系统暴露风扇转速寄存器（已用 DSDT 逆向 + MMIO 实测确认），故无 RPM 显示。

## 构建与运行

需要 .NET 8 SDK（x64）：

```bash
dotnet build -c Release
```

**必须以管理员身份运行**（程序清单已声明自动提权）：硬件监控和 EC 端口访问依赖内核驱动。

```bash
# 从产物目录直接启动（会弹 UAC）
start bin/Release/net8.0-windows/FanController.exe
```

## 工作原理

| 功能 | 实现方式 |
|---|---|
| 温度/功耗/频率 | LibreHardwareMonitorLib → AMD SMU（Package 功耗、Tctl 温度、核心频率） |
| EC 内存观察 | 复用 LibreHardwareMonitorLib 的 Ring0 (WinRing0) 内核驱动做端口只读访问 |
| 全速散热 | 联想 `\\.\EnergyDrv` 驱动 IOCTL `0x831020C0`，写入 `[6,1,1]` 触发除尘模式，每 7 秒重触发保持全速 |

## 安全机制

- 全速散热默认最长 30 分钟自动停止，停止后恢复 BIOS 自动调速
- 程序正常退出时自动停止除尘模式
- 监控驱动加载失败不影响其余功能，状态栏会明示

## 常见问题

- **杀毒软件报警 / 监控驱动加载失败**：WinRing0 在微软"易受攻击驱动"名单上，如被拦截请为程序目录加白名单或临时关闭实时防护
- **EnergyDrv 缺失**：安装一次"联想电脑管家"（会装上 Lenovo ACPI-Compliant Virtual Power Controller 驱动）再重启本程序
- **GPU 温度与 CPU 一样**：4600U 的 Vega 核显与 CPU 在同一颗芯片上共享封装温度，独立核显温度传感器通常不暴露，属正常现象
- **温度比其他软件高几度**：AMD 的 Tctl 含偏移，不同软件读取的传感器不同，看趋势即可

## 致谢

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（监控与内核驱动）
- [Lenovo-Fan-Control](https://github.com/jiarandiana0307/Lenovo-Fan-Control)（EnergyDrv 除尘模式协议参考，MIT）
