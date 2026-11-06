# FanController — 小新 Air 14 (R5 4600U) 风扇控制与硬件监控

一个为联想小新 Air 14 2020 锐龙版（Ryzen 5 4600U，对应海外版 IdeaPad 5 14ARE05）开发的 Windows 桌面工具：

- **仪表盘**：CPU 温度、核显温度、Package/SOC 功耗、GPU 功耗、CPU 频率/利用率、电池功率，通栏行式布局，每行 ↑↓ 按钮自定义顺序（自动保存）
- **Fn+Q 散热模式**：节能／智能散热／性能，设置后回读确认，已在本机验证
- **除尘一次**：点击只触发一次，约 11 秒后结束，执行中不重复触发或排队
- **EC 工具**：EC 内存 dump / 双快照对比，观察 EC 内部状态

注：目前尚未验证本机的可靠 RPM 读法，故无 RPM 显示。DSDT 确实声明了 `ERPM`；此前 WinRing0 MMIO 读数未检查成功标志，不能以全零结果断言固件没有转速接口。2026-11-06 已用现有 InpOut 驱动读通 ECMM，详见下文。

## 构建与运行

直接使用：从 [GitHub Releases](https://github.com/Fizgrad/XiaoXinAir14-2020-ARE-Tools/releases/latest) 下载 `FanController-v1.1.0-win-x64.zip`，解压完整目录后运行 `FanController.exe`。需要安装 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)。

从源码构建：

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
| Fn+Q 模式 | EnergyDrv IOCTL `0x8310213C` → DYTC；只读查询使用 0 和 7，设置使用已验证的模式命令 |
| 除尘一次 | 单次发送除尘启动命令，约 11 秒后发送停止请求，不重新触发保活 |

## 使用散热模式与单次除尘

从 Release 下载并解压后运行 `FanController.exe`，进入“散热与除尘”页。若已有程序运行，先从托盘菜单退出，再打开新版。

| 功能 | 行为 |
|---|---|
| 节能 | Fn+Q Battery Saving 策略 |
| 智能散热 | Fn+Q Intelligent Cooling 策略 |
| 性能 | Fn+Q Extreme Performance 策略 |
| 除尘一次 | 一次点击仅发送一次启动请求，约 11 秒后结束；期间不重复触发、不排队 |
| 取消本次除尘 | 立即发送停止请求；失败时可再次点击重试 |

原有未验证的低速尝试、风扇档位和全速占位均已删除。Fn+Q 模式调整系统功耗和散热策略，由固件自动调节风扇，不设置固定 RPM 或 PWM 百分比。散热模式每 3 秒只读刷新一次，也可点击“读取当前模式”。启动及退出程序不会自动改变所选 Fn+Q 模式。

除尘时长约 11 秒来自既有原生除尘周期资料，并非本机转速反馈测量；应用在期限后明确发送停止请求。每次完成后按钮重新可用，下一次点击才启动下一轮。退出时会取消仍在进行的除尘，不会重新开启。

## Fn+Q 本机验证

限定型号 **LENOVO 81YN / DTCN27WW(V1.13)**，不安装新驱动。协议与 [OpenLenovoSettings 实现](https://github.com/dantmnf/OpenLenovoSettings/blob/master/OpenLenovoSettings.FeatureLib/Feature/Performance/PerformanceModeAcpiVpc.cs)及本机 AcpiVpc.sys 静态分析一致：输入、输出各一个 DWORD，返回长度必须为 4。

| 操作 | DYTC 命令 |
|---|---|
| 查询支持 | `0`，本机返回 `0x50000101` |
| 只读查询当前模式 | `7`，模式位于返回值高 16 位 |
| 智能散热 | `0x000FB001` |
| 性能 | `0x0012B001` |
| 节能 | `0x0013B001` |

已在本机执行“节能 → 智能散热 → 性能”，三种设置均经即时及延迟回读确认，测试后恢复原来的性能模式。只读刷新不使用 `DYTC(2)`，因为本机该分支还会重新应用 CPU 功耗设置。

设置请求失败、返回长度异常或回读不匹配时不会显示切换成功，当前模式会标为未知并给出具体错误。固定转速、百分比和独立全速仍无已验证方案。

无需硬件写入的软件检查：

```powershell
dotnet run --project FanDiag/ControlChecks/ControlChecks.csproj -c Release
dotnet run --project FanDiag/ProfileChecks/ProfileChecks.csproj -c Release
```

## 退出与重复打开

托盘菜单的“退出”会取消仍在进行的除尘、停止监控、关闭主窗口和悬浮窗，并释放单实例锁；不会改动 Fn+Q 模式。窗口右上角的关闭按钮仍按“说明”页设置选择隐藏到托盘或退出；隐藏到托盘时会继续运行。

重复打开程序只唤醒已有主窗口，不再构造第二个窗口。窗口唤醒在 UI 线程执行，避免跨线程异常使唤醒功能失效。旧版已经留下的后台残留进程需要在任务管理器中结束一次，再打开新版；新版修复后的正常退出不会留下该残留。

启动和退出回归检查使用隔离实例及模拟窗口，不会写入硬件：

```powershell
dotnet run --project FanDiag/StartupChecks/StartupChecks.csproj -c Release
```

## 2026-11-06 风扇接口复核（早期只读调查）

本机实测身份为 **81YN / XiaoXinAir-14ARE 2020 / Ryzen 5 4600U**，BIOS 为 **DTCN27WW(V1.13)**，EC 为 1.27。

- `Win32_Fan` 只提供通用设备条目，`DesiredSpeed`、`VariableSpeed` 均为空；未发现 Legion 使用的 `LENOVO_FAN_METHOD` / `LENOVO_GAMEZONE_DATA` 类。
- 当前注册表导出的 DSDT 与之前保存的表 SHA256 一致。`ECMM` 区域为 `SystemMemory 0xFE0B0800, 0x1000`，并非仅 16 字节。
- 复用系统上已经运行的 `inpoutx64`，成功只读映射 ECMM 的前 256 字节，连续读取到非全零、非全 FF 的数据及变化的温度字段。脚本不会安装或启动驱动。
- `QTMD` 在 ECMM `+0x10 bit2`，`STMD` 在 `+0x10 bit5`；此次两者均为 0，且 `AUPM/AQMD` 为 0。结合本机 DYTC 实现，**推断当前为性能模式**。这描述的是固件散热/功耗策略，不能换算为固定 PWM。
- `ERPM` 在 `+0x06`，此次三次读取均为 0；单位、更新条件和有效性尚未验证，不能将它显示为 0 RPM。`FNCM` 原值为 4，`EnergyDrv` 状态原值为 133，暂不赋予转速含义。其余带 FAN 名称的位也只记录原值，尚未验证为有效控制接口。
- 本机 DSDT 还定义 `FANG(index)`：写入 `ERIB`（ECMM `+0x5D`，16 位索引），再读取 `ERBD`（EC 端口空间 `+0x5F`）。这是后续查扩展 EC 数据的线索；当前没有该机已验证的风扇索引，未尝试索引扫描或风扇写入。
- 官方 `DYTC` 方法包含静音/均衡/性能策略。其 `GET` 分支也会执行功耗设置函数，因此本次没有直接调用它。读取状态采用上面的已知 ECMM 字段。

管理员 PowerShell 中可复现只读检查：

```powershell
# 枚举 WMI 类和方法签名，导出 ACPI 表；不调用 WMI 设置方法
.\FanDiag\Explore-FanInterfaces.ps1
# 仅限上面已验证的型号和 BIOS，复用已运行的 inpoutx64
.\FanDiag\Read-FanSettings.ps1
# 验证旧读取路径；失败明确返回 unavailable，不再伪装成全零
dotnet .\FanDiag\bin\Release\net8.0-windows\FanDiag.dll --mmio-check
```

原始 JSON、ACPI 表及分析工具位于被 Git 忽略的 `FanDiag/exploration/`。没有更改风扇模式、功耗策略或 BIOS，也没有安装新驱动。目前已打通状态读取，但尚未获得可靠 RPM 或可用的自定义风扇曲线。

协议对照：[Linux ideapad-laptop 源码](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/lenovo/ideapad-laptop.c)、[EnergyDrv 查询实现](https://github.com/jiarandiana0307/Lenovo-Fan-Control/blob/main/src/fanctrl.c)。网上相关 FanControl 插件仍基于同一 EnergyDrv 协议，未提供已验证的精确百分比调速接口。

### 百分比调速的进一步核查

以下为早期调查记录，相关低速尝试入口与全速占位均已从程序删除。

没有 RPM 反馈也可以通过 PWM 占空比控制风扇；但本机尚未找到已验证的可写 PWM 寄存器或目标档位协议，不能可靠地实现 30%、50%、70% 等设定。

- 已查询 [Windows NBFC 配置目录](https://github.com/hirschmann/nbfc/tree/master/Configs)及 [nbfc-linux 配置目录](https://github.com/nbfc-linux/nbfc-linux/tree/main/share/nbfc/configs)，没有名称匹配 81YN、XiaoXinAir-14ARE 2020 或 IdeaPad 5 14ARE05 的专用配置。目录查询结果保存于 `FanDiag/exploration/nbfc-*-configs.json`。
- [FanControl.LenovoPlugin](https://github.com/jiarandiana0307/FanControl.LenovoPlugin) 用 ≤20%、20–80%、≥80% 分别选择低速/正常/全速逻辑，不是设置实际 PWM 百分比。上游所谓低速逻辑只是每五秒重发正常模式命令。
- 不能直接套用其他机器的百分比配置。例如 [IdeaPad 3 15IAU7 配置](https://github.com/nbfc-linux/nbfc-linux/blob/main/share/nbfc/configs/Lenovo%20Ideapad%203%2015IAU7.json)写入 `0xB0`，但本机 DSDT 中同一偏移是 `CTMP` 温度字段，不是已确认的 PWM 控制字段。
- DSDT 中出现 `PWMN`、`DUTY` 名称也不证明存在风扇百分比接口；`DUTY` 位于 CPU 时钟节流相关 `CCLK` 区域，`PWMN` 只见字段声明。`FANU/FSSN` 也暂不能换算百分比。

进一步的有效路线是针对该型号 EC 固件分析 `FANG/FANW` 索引空间与风扇调节逻辑，确定实际寄存器、自动调速覆盖机制和恢复路径，再进行有限写入验证；本次没有根据其他机型的地址试写，也没有添加未经验证的百分比滑块。

## 常见问题

- **杀毒软件报警 / 监控驱动加载失败**：WinRing0 在微软"易受攻击驱动"名单上，如被拦截请为程序目录加白名单或临时关闭实时防护
- **EnergyDrv 缺失**：安装一次"联想电脑管家"（会装上 Lenovo ACPI-Compliant Virtual Power Controller 驱动）再重启本程序
- **GPU 温度与 CPU 一样**：4600U 的 Vega 核显与 CPU 在同一颗芯片上共享封装温度，独立核显温度传感器通常不暴露，属正常现象
- **温度比其他软件高几度**：AMD 的 Tctl 含偏移，不同软件读取的传感器不同，看趋势即可

## 致谢

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（监控与内核驱动）
- [Lenovo-Fan-Control](https://github.com/jiarandiana0307/Lenovo-Fan-Control)（EnergyDrv 除尘模式协议参考，MIT）
