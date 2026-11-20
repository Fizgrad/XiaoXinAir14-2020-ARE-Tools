# 小新控制中心

联想小新 Air 14 2020 锐龙版（81YN）的硬件管理工具。

- **Debian / Linux：1.4.0**，原生 Python 3 + GTK 3，支持 **Wayland 和 X11**。
- **Windows：1.3.2**，原有 WPF 版本保留。

## Debian 13 安装

从 [Linux Releases](https://github.com/Fizgrad/XiaoXinAir14-2020-ARE-Tools/releases/tag/linux-v1.4.0) 下载 `fancontroller_1.4.0_all.deb` 和 `SHA256SUMS`，在下载目录运行：

```sh
sha256sum -c SHA256SUMS
sudo apt install ./fancontroller_1.4.0_all.deb
fancontroller
```

也可以从应用菜单启动 **小新控制中心**（XiaoXin Control Center）。依赖由 APT 安装，不需要 .NET、Wine、pip 或额外的内核驱动。软件包使用 `Architecture: all`（Python 代码）；设备控制仍取决于 Lenovo 固件和内核接口。

适配基线为 Debian 13、Linux 6.12、LENOVO 81YN、BIOS `DTCN27WW(V1.13)`。程序按实际内核接口探测能力；其他硬件可读取通用监控信息，但不保证具有联想设备控制功能。

### Linux 功能与差异

| 功能 | Debian / Linux 版本 |
|---|---|
| 原生桌面 | GTK 3，支持原生 Wayland 和 X11；关闭窗口即退出，无托盘依赖。 |
| 实时监控 | 每 2 秒读取 CPU 利用率与频率、内存、GPU 利用率、温度和风扇转速；CPU、内存和 GPU 分别显示最近 5 分钟的独立历史曲线，与多传感器温度曲线纵向紧凑排列。内核未提供的数据明确显示为暂无数据。CPU 利用率需要两个采样点。 |
| 散热模式 | 通过节能、均衡、性能三个按钮直接选择模式；使用 `platform_profile` 应用设置并读取内核状态确认结果。 |
| 电池 | 电量、功率、容量、健康度、循环次数、养护充电；可选记录最近 30 天的电池 CSV 并导出。睡眠期间不采样，不推测睡眠放电。 |
| 设备设置 | 点击开关或按钮直接设置 Fn 锁、USB 持续供电、键盘背光；使用 `ideapad_laptop` 和 LED 接口。不提供 Windows 独立 AC / 电池 USB 开关或背光闲置策略。 |
| 自动场景 | 使用并排按钮选择电源场景模式；程序运行时根据电源和进程名切换散热模式，应用规则按顺序匹配。稳定 3 秒后切换、至少间隔 30 秒、手动操作优先 10 分钟。取消授权后需手动恢复自动切换。停用时保留当前模式。 |
| 硬件与健康 | PCI、USB、网络、存储、显示器、音频、蓝牙清单，支持搜索及 JSON / HTML 本地导出；检查电池容量、当前温度和根文件系统空间。未执行 SMART 自检，磁盘健康显示暂无数据。 |
| 登录启动 | 按用户保存 XDG Autostart 桌面入口，适用于 Wayland / X11。默认不启用自动场景或历史记录。 |
| 除尘 | **暂不支持**。本机 `fan_mode` 返回 `133`，不属于 Linux 文档定义的 `0/1/2/4`；不沿用 Windows 指令，不尝试写入未知风扇模式。 |
| 功耗预设 / EC | Windows 的 EnergyDrv、WinRing0、RyzenAdj 功耗预设和直接 EC 访问未移植。Linux 版本使用内核散热策略，不提供手动 PWM、直接 EC 写入或悬浮窗。 |

主窗口以普通用户运行，读取设备无需提权。每次启动后首次修改硬件设置时，通过 Polkit 请求一次管理员授权；后续 GUI 操作通过私有管道复用受限助手，关闭程序即结束授权会话，下次启动需重新授权。助手只接受固定控件及有效枚举值，不接受任意路径、命令或 EC 地址，不创建常驻服务或持久授权规则。安装和首次启动不会修改设备设置。若桌面没有 Polkit 认证代理，请安装并启动当前桌面的认证代理。

配置保存于 `${XDG_CONFIG_HOME:-~/.config}/fancontroller/settings.json`，历史保存于 `${XDG_DATA_HOME:-~/.local/share}/fancontroller/history/`，自启动入口为 `${XDG_CONFIG_HOME:-~/.config}/autostart/fancontroller.desktop`。卸载软件包保留这些用户数据；如需停止登录启动，先在设置中关闭它。

### 命令行与源码运行

```sh
fancontroller --status
fancontroller --json
fancontroller --export report.json
fancontroller --export report.html
fancontroller --set profile balanced
fancontroller --set conservation 1
```

支持的设置名为 `profile`、`conservation`、`usb_charging`、`fn_lock` 和 `keyboard_backlight`；有效值可在 `--json` 的 `controls.*.choices` 中查看。命令行状态和报告导出也适用于没有图形桌面的环境；每次命令行修改单独请求 Polkit 授权，不复用 GUI 授权会话。

源码运行（需要 `python3-gi`、`python3-cairo`、`python3-gi-cairo`、`gir1.2-gtk-3.0`；源码模式读取可用，设备修改需要先安装软件包）：

```sh
PYTHONPATH=linux /usr/bin/python3 -m fancontroller
# 强制原生 Wayland，不通过 Xwayland：
GDK_BACKEND=wayland PYTHONPATH=linux /usr/bin/python3 -m fancontroller
```

### 构建与发布 .deb

```sh
PYTHONPATH=linux /usr/bin/python3 -m unittest discover -s linux/tests -v
sh tools/build-deb.sh
```

构建需要 Python 3、`dpkg-deb`、`xz-utils`、Git 和基本命令行工具，无需 root 或网络。输出位于 `dist/fancontroller_1.4.0_all.deb` 和 `dist/SHA256SUMS`。构建使用 `SOURCE_DATE_EPOCH`（默认最后一次 Git 提交时间）；从无 Git 元数据的源码压缩包构建时，需显式设置此变量。相同源码及构建环境可复现字节相同的软件包。

`.github/workflows/debian.yml` 在 Debian 13 容器运行单元测试、重复构建校验、安装校验和 Weston 无头 **Wayland** 窗口测试。推送 `linux-v1.4.0` 标签后自动创建 Linux Release 并上传 `.deb` 和校验文件；Linux 标签与原有 Windows `v*` 标签分开。

Linux 接口依据：[ideapad-laptop ABI](https://github.com/torvalds/linux/blob/v6.12/Documentation/ABI/testing/sysfs-platform-ideapad-laptop)、[platform_profile ABI](https://github.com/torvalds/linux/blob/v6.12/Documentation/ABI/testing/sysfs-platform_profile)、[GTK Wayland 支持](https://docs.gtk.org/gtk3/wayland.html)。

## Windows 1.3.2 功能

| 功能 | 内容 |
|---|---|
| 硬件检测 | 查看整机、系统、CPU、主板与 BIOS、内存、显卡、硬盘、电池、显示器、网络、音频、USB 和蓝牙设备；支持分类和搜索。 |
| 健康检查 | 检查设备错误、存储健康、SMART 故障预测、磁盘空间与电池容量，分别显示正常、需关注和暂无数据；可导出 HTML 或 JSON 报告。 |
| 实时监控 | 显示 CPU／GPU 温度、功耗、频率、利用率与电池功率，支持趋势曲线、自定义行顺序和悬浮窗。 |
| 散热与除尘 | 切换节能、智能散热、性能三种 Fn+Q 模式；单次除尘约 11 秒，可随时取消。 |
| 电池管理 | 切换正常／养护充电模式，显示健康度、容量和循环次数；保存放电与睡眠记录，支持导出历史 CSV。 |
| 自动场景 | 根据插电／电池状态和运行中的应用切换 Fn+Q，支持应用优先级、切换间隔、手动优先和恢复自动切换。 |
| 设备设置 | 管理 Fn 锁、USB 持续供电、键盘背光与闲置熄灭策略。 |
| 功耗预设 | 安静办公、均衡、持续负载三档预设，显示实际限制值，支持恢复调整前参数。 |
| 启动与关闭 | 设置登录 Windows 时启动、自启动时应用的功耗预设，以及关闭窗口时隐藏到托盘或直接退出。自启动显示主窗口。 |
| 操作反馈 | 高亮当前模式，显示开关状态与更新时间；区分未保存修改，保留操作进度、结果和最近操作记录。 |
| EC 工具 | 读取 EC 数据、设置双快照并对比差异。 |

## Windows 安装与使用

从 [Windows Release](https://github.com/Fizgrad/XiaoXinAir14-2020-ARE-Tools/releases/tag/v1.3.2) 下载 `FanController-v1.3.2-win-x64-Setup.exe`，按向导安装。安装版包含 .NET 运行时、RyzenAdj 和配套组件，支持开始菜单、可选桌面快捷方式及 Windows 卸载。

也可下载便携压缩包，完整解压后运行 `FanController.exe`；便携版需要 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)。

程序界面包含仪表盘、散热与除尘、小新控制中心、硬件检测、EC 工具和设置。硬件检测报告在本地导出，应用配置与电池历史保存在 `%APPDATA%\FanController`。
