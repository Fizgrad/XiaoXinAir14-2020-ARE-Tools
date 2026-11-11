# FanController — 小新 Air 14 (R5 4600U) 风扇控制与硬件监控

一个为联想小新 Air 14 2020 锐龙版（Ryzen 5 4600U，对应海外版 IdeaPad 5 14ARE05）开发的 Windows 桌面工具：

- **仪表盘**：CPU 温度、核显温度、Package/SOC 功耗、GPU 功耗、CPU 频率/利用率、电池功率，通栏行式布局，每行 ↑↓ 按钮自定义顺序（自动保存）
- **Fn+Q 散热模式**：节能／智能散热／性能，设置后回读确认，已在本机验证
- **除尘一次**：点击只触发一次，约 11 秒后结束，执行中不重复触发或排队
- **EC 工具**：EC 内存 dump / 双快照对比，观察 EC 内部状态
- **小新控制中心（1.2.0）**：电池养护、充电能力检测、电池健康与历史、自动 Fn+Q 场景、Fn 锁／USB 供电／背光管理、RyzenAdj 功耗预设

注：目前尚未验证本机的可靠 RPM 读法，故无 RPM 显示。DSDT 确实声明了 `ERPM`；此前 WinRing0 MMIO 读数未检查成功标志，不能以全零结果断言固件没有转速接口。2026-11-06 已用现有 InpOut 驱动读通 ECMM，详见下文。

## 构建与运行

直接使用：从 [GitHub Releases](https://github.com/Fizgrad/XiaoXinAir14-2020-ARE-Tools/releases/latest) 下载 Windows x64 压缩包，解压完整目录后运行 `FanController.exe`。需要安装 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)。

从源码构建：

需要 .NET 8 SDK（x64）：

```bash
dotnet build -c Release
```

功耗预设的配套工具由以下脚本从 RyzenAdj 官方 v0.19.0 发布下载，并检查官方 SHA256；完成后重新构建或发布，程序会自动携带运行所需文件。也可以在功耗页选择自己完整解压的 `ryzenadj.exe`。

```powershell
.\tools\Prepare-RyzenAdj.ps1
dotnet build -c Release
```

脚本仅下载和解压，不运行附带的服务安装脚本。授权与源码位置见 `tools/ThirdPartyNotices.txt`。

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

原有未验证的低速尝试、风扇档位和全速占位均已删除。Fn+Q 模式调整系统功耗和散热策略，由固件自动调节风扇，不设置固定 RPM 或 PWM 百分比。散热模式每 3 秒只读刷新一次，也可点击“读取当前模式”。未启用自动场景时，启动不会改变所选 Fn+Q 模式；退出保留当前模式。

## 1.2.3 界面文案精简

删除应用内的安全说明、实现原理和各页面重复的限制说明，精简充电、设备、功耗与除尘提示。保留当前状态、更新时间、未保存标记、操作记录和具体错误原因。74 项软件／界面回归检查通过。

## 1.2.2 设置状态与操作反馈

- 窗口顶部常驻操作反馈：显示处理中、成功或失败、具体结果和时间；后台刷新不会覆盖最近一次手动操作。可展开查看本次运行最近 12 条操作记录，较长详情也可通过悬停或展开查看。
- 充电模式和 Fn+Q 用高亮与“✓ 当前”标记固件回读的模式；设备开关旁显示开启／关闭或 Fn 键优先行为，并区分尚未读取、回读未确认与固件未报告支持。
- 功耗页突出显示回读的短时／持续功耗与温度上限、当前匹配预设和可恢复的原参数。按钮显示实际将应用的目标值；外部策略覆盖参数后显示警告并更新当前状态。
- 自动场景区分当前运行状态、已保存规则和未保存草稿；保存按钮固定在页面底部。背光策略也分别显示已保存策略和待保存修改。保存失败时保留原配置及编辑内容，不显示成功。
- 除尘显示倒计时和进度；EC 读取、双快照、对比、电池记录／导出、悬浮窗和窗口关闭行为都有明确的操作结果。硬件设置的成功反馈以接口结果及回读为依据，除尘提示明确为请求结果。

本版通过 **74 项软件／界面检查**（含不同窗口尺寸、反馈不会被刷新覆盖、未保存状态、保存失败恢复和外部功耗变化），另完成 **18 项实机操作回归**，失败项为 0。实机回归使用隔离配置，结束后独立确认原硬件设置已恢复、个人配置未改变。报告位于 `FanDiag/exploration/hardware-feedback-1.2.2/results.txt`；现场物理效果仍按下文的验证边界标明未验证。

## 1.2.1 功耗设置崩溃修复

1.2.0 调用 Windows RyzenAdj v0.19.0 时只传入设置参数，未带 `--info`。该版本的 [Windows 退出清理实现](https://github.com/FlyGoat/RyzenAdj/blob/v0.19.0/lib/win32/osdep_win32.cpp)会解引用功耗表映射指针；未初始化时可能在退出时触发 `0xC0000005`。本机 Windows 崩溃日志与反汇编均对应 `ryzenadj.exe+0xE0ED`。崩溃发生在清理阶段，设置可能已被硬件接受，不能仅凭进程失败判断未生效。

1.2.1 的设置与恢复调用都带 `--info`，在写入前初始化功耗表映射；同时显示十六进制退出码，记录错误详情，并在恢复命令异常后独立回读实际参数。2026-11-11 已在本机将刚读到的 **48 W / 37.5 W / 100°C** 原样写回，两次回读一致，进程正常退出（退出码 0）。

2026-11-11 随后完成三档预设的实机写入与延迟回读：安静办公 **15 W / 12 W / 70°C**、均衡 **25 W / 18 W / 80°C**、持续负载 **30 W / 25 W / 85°C** 均匹配。安静办公还使用相同的 12 线程计算负载进行短时对比，PPT 功耗采样均值从 **19.12 W** 降至 **11.67 W**；多次延迟回读的限制值保持一致。测试结束后均已恢复 **48 W / 37.5 W / 100°C** 并独立回读确认。这是短时验证，不代表长时间运行或其他固件策略切换后一定保持不变。

原始记录保存在 `FanDiag/exploration/power-quiet-live-1.2.1.json` 和 `power-other-presets-live-1.2.1.json`。`FanDiag/Test-QuietPower.ps1 -Run` 会短暂施加计算负载并切换安静预设；`FanDiag/Test-OtherPowerPresets.ps1 -Run` 会切换其余两档；两者都在结束时恢复测试前参数，需要管理员权限。

软件回归新增功耗表初始化、空输出崩溃错误码和崩溃后的独立回读检查。需要复核真实调用时，先构建 FeatureChecks，再以管理员权限运行：

```powershell
# 默认只读取
.\FanDiag\Verify-CurrentPower.ps1
# 将刚读到的三个参数原样写回，并验证回读；不选择预设
.\FanDiag\Verify-CurrentPower.ps1 -VerifyUnchanged
```

## 小新控制中心（1.2.0）

### 2026-11-12 全功能实机自动检查

使用真实主窗口、独立配置目录和管理员权限完成 **19 项实机流程检查**；另有 **64 项软件／界面回归检查**通过。实机覆盖：传感器与电池刷新、仪表盘顺序保存、Fn+Q 三档、正常／养护充电模式、Fn 锁／两类 USB 供电／背光开关、真实应用启动退出触发自动场景、手动优先及解除暂停、当前电源状态规则、三档功耗按钮与恢复、EC 256 字节双快照、悬浮窗四角及穿透样式、托盘隐藏恢复、真实电池 CSV 导出与停止记录、除尘请求／约 11 秒结束／取消。开关类检查通过驱动独立回读确认。

验证边界：快充未报告支持；当前监控库没有 SOC 功耗读数。背光闲置测试的连续空闲时间最长约 **19.2 秒**，没有达到 **30 秒**阈值，实际触发／活动恢复仍未验证。按用户选择，未进行现场观察、真实拔插电源或睡眠唤醒；因此 USB 实际供电、Fn 键行为、背光物理亮灭、风扇实际加速、睡眠掉电和完整充放电阈值效果仍未验证。除尘命令成功不等同于已测得风扇转速。

每轮实机测试均确认恢复原散热模式、充电模式、设备开关及 **48 W / 37.5 W / 100°C** 功耗参数，原用户配置文件的内容校验一致。汇总报告位于 `FanDiag/exploration/hardware-validation-2026-11-12.txt`，JSON 包含原始证据路径。初次闲置测试超时的记录也保留，并通过真实空闲秒数记录区分条件未满足与产品失效。

可复跑的实机入口（会短暂切换硬件设置，结束后恢复；先退出正在运行的 FanController）：

```powershell
dotnet build FanDiag/HardwareChecks/HardwareChecks.csproj -c Release
# 管理员 PowerShell；默认不执行，必须显式指定 -Run
.\FanDiag\Run-HardwareChecks.ps1 -Run
```

新增控制仍限定 **LENOVO 81YN / DTCN27WW(V1.13)**。进入“小新控制中心”后可使用四个页面：

| 页面 | 功能与行为 |
|---|---|
| 电池管理 | 正常充电、固件养护模式、快充能力检测；设置后回读，快充与养护互斥。显示电量、设计／满充／剩余容量、健康度、损耗和循环次数；未知值显示 `--`。 |
| 自动场景 | 插电／电池 Fn+Q 规则及最多 50 条进程规则，应用规则按列表顺序优先。场景稳定 3 秒后切换，默认间隔 30 秒。手动切换或检测到外部 Fn+Q 变化后，默认暂停自动切换 10 分钟。 |
| 设备设置 | 根据固件能力开放 Fn 锁、插电 USB 持续供电、电池 USB 持续供电和背光开关。支持背光闲置熄灭；只恢复由工具自动熄灭的背光，手动关闭时保持关闭。退出工具时尝试恢复由其自动熄灭的背光。 |
| 功耗预设 | 点击“读取当前参数”后使用安静办公／均衡／持续负载预设；读取 Renoir 功耗表，限制值按首次调整前的固件参数取较小值，写入后两次回读。失败时尝试恢复操作前参数，可恢复本次首次调整前参数。 |

自动场景默认关闭，修改规则后点击“保存自动场景”生效。应用结束后回到电源规则；关闭电源联动时回到启用前模式。关闭自动场景会在切换间隔允许后恢复其接管前模式；已手动接管则保留手动选择。点击“立即恢复自动切换”可以解除手动暂停。关闭整个工具时保留当前 Fn+Q 和功耗参数，重新打开不会自动应用功耗预设。

功耗预设的请求值为：安静办公 15／12 W、70°C；均衡 25／18 W、80°C；持续负载 30／25 W、85°C（前两项分别为短时和持续功耗上限）。实际应用值不超过首次调整前参数。应用功耗预设时暂停已启用的自动场景。Fn+Q、插拔电源或其他工具可能覆盖参数；工具只提示变化，不反复重写。

电池详细信息改用 Windows 电池类只读 IOCTL，无需额外电池驱动。最近 5 分钟平均放电功率需要至少 20 秒连续有效放电采样；睡眠和长采样间隔不计入平均。历史记录默认每 30 秒保存一次 CSV，保留 30 天，并可导出；睡眠前后净电量变化最多保留 100 条。配置和记录位于 `%APPDATA%\FanController`，关闭记录后保留已有文件。

2026-11-11 本机只读探测：电池设计容量 **44.5 Wh**，满充容量约 **42.8 Wh**，健康度约 **96.1%**，循环 **61 次**；Fn 锁、两类 USB 供电和背光均能读取。`GBMD` 返回 `0x00100240`，**未报告快充支持位，因此本机快充按钮禁用**。管理员权限下 RyzenAdj 成功读取短时 **48 W**、持续 **37.5 W**、温度上限 **100°C**；这些是当时参数，不是预设目标。新设备开关、充电模式和功耗写入经过模拟传输检查，本次本机探测未发送设置命令。

软件回归与界面检查不写入硬件：

```powershell
dotnet run --project FanDiag/FeatureChecks/FeatureChecks.csproj -c Release
dotnet run --project FanDiag/FeatureUiChecks/FeatureUiChecks.csproj -c Release
```

本机只读检查先构建 FeatureChecks，再在管理员 PowerShell 中运行：

```powershell
.\FanDiag\Read-FeatureState.ps1
# 同时读取 RyzenAdj 功耗表，需要先准备配套工具
.\FanDiag\Read-FeatureState.ps1 -Power
```

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
