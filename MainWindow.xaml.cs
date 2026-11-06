using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanController.Models;
using FanController.Services;

namespace FanController;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MonitorService _monitor;
    private readonly bool _energyAvailable;
    private readonly ThermalProfileService _thermalProfiles = new();
    private readonly System.Windows.Threading.DispatcherTimer _profileTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _profileRequestInProgress;
    private volatile SensorSnapshot? _lastSnap;

    // ---- 行式仪表盘 ----
    private sealed class DashRow
    {
        public string Id = "";
        public TextBlock ValueTb = new();
        public TextBlock SubTb = new();
        public Controls.Sparkline Spark = new();
        public Border Root = new();
    }
    private readonly Dictionary<string, DashRow> _rows = new();
    private readonly Dictionary<string, History> _rowHist = new();

    private readonly ObservableCollection<EcCell> _ecCells = new();
    private byte[]? _currentDump;

    // ---- 托盘 ----
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyExit;
    private volatile bool _shuttingDown;

    // ---- 游戏内悬浮窗 ----
    private OverlayWindow? _overlay;

    private static readonly Brush ChipOk = new SolidColorBrush(Color.FromRgb(43, 66, 48));
    private static readonly Brush ChipBad = new SolidColorBrush(Color.FromRgb(74, 42, 42));
    private static readonly Brush ChipNeutral = new SolidColorBrush(Color.FromRgb(43, 43, 51));

    public MainWindow()
    {
        App.Log("MainWindow 构造开始");
        InitializeComponent();
        App.Log("InitializeComponent 完成");

        _settings = AppSettings.Load();
        DataContext = _settings;

        for (int i = 0; i < 256; i++) _ecCells.Add(new EcCell(i));
        EcDump.ItemsSource = _ecCells;

        CreateRows();
        BuildDashboard();
        CbCloseAction.SelectedIndex = _settings.CloseToTray ? 0 : 1;
        ChkOverlay.IsChecked = _settings.OverlayEnabled;
        CbOverlayCorner.SelectedIndex = Math.Clamp(_settings.OverlayCorner, 0, 3);

        _monitor = new MonitorService(_settings);
        _monitor.StatusChanged += OnMonitorStatus;
        _monitor.Updated += OnSnapshot;

        EnergyDrvFan.StateChanged += OnFanState;

        var admin = MonitorService.IsAdministrator();
        SetChip(ChipAdmin, ChipAdminText, admin ? "管理员：是" : "管理员：否（功能受限）", admin);

        _energyAvailable = EnergyDrvFan.DriverPresent;
        SetChip(ChipEnergy, ChipEnergyText, _energyAvailable ? "EnergyDrv：可用" : "EnergyDrv：缺失", _energyAvailable);
        if (!_energyAvailable) LblDustHint.Visibility = Visibility.Visible;
        UpdateFanControls();
        _profileTimer.Tick += async (_, _) => await RefreshThermalProfileAsync();

        // 窗口先显示，驱动加载等重活放到 Loaded 之后，避免卡住启动
        Loaded += (_, _) =>
        {
            App.Log("窗口已显示，开始加载监控");
            _profileTimer.Start();
            _ = RefreshThermalProfileAsync();
            Task.Run(() =>
            {
                try { _monitor.Start(); }
                catch (Exception ex) { App.Log("监控启动异常: " + ex); }
            });
        };

        Closed += (_, _) => Shutdown();
        App.Log("MainWindow 构造完成");
        InitTray();
    }

    /// <summary>系统托盘：关窗最小化到托盘，双击图标恢复，右键菜单可退出</summary>
    private void InitTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Text = "FanController — 双击打开",
                Visible = true,
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
            menu.Items.Add("游戏内悬浮窗", null, (_, _) => ToggleOverlay());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) =>
            {
                _reallyExit = true;
                // Exit means the whole application, including any overlay windows.
                try { Shutdown(); }
                finally { Application.Current.Shutdown(); }
            });
            _tray.ContextMenuStrip = menu;

            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            App.Log("托盘初始化失败: " + ex);
        }
    }

    /// <summary>运行时绘制托盘图标（16/32px 双尺寸），不依赖 ICO 文件解析</summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        try
        {
            var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                // 深色圆角底
                using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 23, 23, 27));
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddArc(0, 0, 14, 14, 180, 90);
                path.AddArc(18, 0, 14, 14, 270, 90);
                path.AddArc(18, 18, 14, 14, 0, 90);
                path.AddArc(0, 18, 14, 14, 90, 90);
                path.CloseFigure();
                g.FillPath(bg, path);

                // 四片青色扇叶
                using var leaf = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 79, 195, 247));
                float cx = 16, cy = 16, len = 11;
                for (int i = 0; i < 4; i++)
                {
                    double a = i * Math.PI / 2;
                    var pts = new[]
                    {
                        new System.Drawing.PointF(cx + (float)Math.Cos(a) * 2, cy + (float)Math.Sin(a) * 2),
                        new System.Drawing.PointF(cx + (float)Math.Cos(a + 0.5) * len, cy + (float)Math.Sin(a + 0.5) * len),
                        new System.Drawing.PointF(cx + (float)Math.Cos(a - 0.25) * len, cy + (float)Math.Sin(a - 0.25) * len),
                    };
                    g.FillPolygon(leaf, pts);
                }

                // 白色轴心
                using var hub = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 232, 232, 236));
                g.FillEllipse(hub, 12.5f, 12.5f, 7, 7);
            }
            var hIcon = bmp.GetHicon();
            var icon = System.Drawing.Icon.FromHandle(hIcon);
            return icon;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭按钮行为可配置：隐藏到托盘继续运行，或直接退出
        if (!_reallyExit && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _profileTimer.Stop();
        _reallyExit = true;
        try { _tray?.Dispose(); } catch { }
        _monitor.StatusChanged -= OnMonitorStatus;
        _monitor.Updated -= OnSnapshot;
        EnergyDrvFan.StateChanged -= OnFanState;
        try { if (EnergyDrvFan.IsRunning || EnergyDrvFan.RestorePending) EnergyDrvFan.Stop("程序退出"); } catch { }
        try { _overlay?.Close(); _overlay = null; } catch { }
        try { _monitor.Dispose(); } catch { }
        _settings.Save();
    }

    // ---------------- 状态更新 ----------------

    private void OnMonitorStatus(string msg)
    {
        if (_shuttingDown) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown) return;
            bool ok = EcIO.IsAvailable;
            SetChip(ChipDriver, ChipDriverText, "监控驱动：" + (msg.Length > 60 ? msg[..60] + "…" : msg), ok);
            SetChip(ChipEc, ChipEcText, "EC：" + EcIO.Status, ok);
        });
    }

    private void OnSnapshot(SensorSnapshot s)
    {
        if (_shuttingDown) return;
        _lastSnap = s;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown) return;
            SetRow("CpuTemp", s.CpuTemp, v => v.ToString("0") + " °C",
                v => v >= 90 ? Bad : v >= 75 ? Warn : Fg, _monitor.CpuName);
            SetRow("CpuPower", s.PackagePower, v => v.ToString("0.0") + " W", null,
                s.SocPower is float soc ? $"SOC：{soc:0.0} W" : "SOC：--");
            SetRow("GpuTemp", s.GpuTemp ?? s.CpuTemp, v => v.ToString("0") + " °C",
                v => v >= 90 ? Bad : v >= 75 ? Warn : Fg,
                s.GpuTemp == null ? "核显与 CPU 同芯（封装温度）" : _monitor.GpuName);
            SetRow("GpuPower", s.GpuPowerW, v => v.ToString("0.0") + " W", null, _monitor.GpuName);
            SetRow("GpuClock", s.GpuClockMHz, v => v.ToString("0") + " MHz", null, "Vega 核显核心频率");
            SetRow("GpuLoad", s.GpuLoad, v => v.ToString("0") + " %", null, "D3D 核心占用");
            SetRow("CpuClock", s.CpuClockMHz, v => (v / 1000f).ToString("0.00") + " GHz", null, "最高核心频率");
            SetRow("CpuLoad", s.CpuLoad, v => v.ToString("0") + " %", null, "全核总占用");
            SetRow("Battery", s.BatteryPowerW, v => (v >= 0 ? "+" : "") + v.ToString("0.0") + " W", null,
                s.BatteryPercent is float pct ? $"电量 {pct:0}%（正=放电 负=充电）" : "正=放电 负=充电");

            if (_rows.TryGetValue("SysInfo", out var info))
            {
                info.ValueTb.Text = EcIO.IsAvailable ? "EC 就绪" : "EC 不可用";
                info.ValueTb.Foreground = EcIO.IsAvailable ? Fg : Warn;
                info.SubTb.Text = $"{_monitor.CpuName} | {_monitor.GpuName} | 轮询 {_settings.PollIntervalMs} ms · EC {EcIO.Status}";
            }

            // 托盘提示实时温度（Text 最长 63 字符）
            if (_tray != null)
            {
                string temp = s.CpuTemp is float t2 ? $"CPU {t2:0}°C" : "CPU --";
                string pw = s.PackagePower is float p2 ? $" {p2:0.0}W" : "";
                _tray.Text = $"FanController | {temp}{pw}";
            }

            if (_overlay is { IsVisible: true })
            {
                _overlay.Update(s);
                _overlay.ApplyPosition();
            }

            UpdateFanControls();
        });
    }

    private void UpdateFanModeChip()
    {
        if (EnergyDrvFan.RestorePending)
        {
            SetChip(ChipFanMode, ChipFanModeText, "风扇：恢复失败", false);
            return;
        }
        switch (EnergyDrvFan.CurrentMode)
        {
            case FanControlMode.DustCleaning:
                SetChip(ChipFanMode, ChipFanModeText, "风扇：除尘", null);
                break;
            default:
                SetChip(ChipFanMode, ChipFanModeText, "风扇：EC 自动", null);
                break;
        }
    }

    private static readonly (string Id, string Label, Color Line)[] RowSpecs =
    {
        ("CpuTemp", "CPU 温度", Color.FromRgb(229, 115, 115)),
        ("CpuPower", "CPU 功耗", Color.FromRgb(255, 183, 77)),
        ("GpuTemp", "GPU 温度", Color.FromRgb(186, 104, 200)),
        ("GpuPower", "GPU 功耗", Color.FromRgb(129, 199, 132)),
        ("GpuClock", "核显频率", Color.FromRgb(128, 222, 234)),
        ("GpuLoad", "核显占用", Color.FromRgb(244, 143, 177)),
        ("CpuClock", "CPU 频率", Color.FromRgb(77, 208, 225)),
        ("CpuLoad", "CPU 利用率", Color.FromRgb(255, 213, 79)),
        ("Battery", "电池功率", Color.FromRgb(102, 187, 106)),
        ("SysInfo", "系统信息", Color.FromRgb(154, 154, 166)),
    };

    private void CreateRows()
    {
        foreach (var (id, label, line) in RowSpecs)
        {
            var labelTb = new TextBlock
            {
                Text = label,
                Foreground = FgDim,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var value = new TextBlock
            {
                Text = "--",
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = Fg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var spark = new Controls.Sparkline
            {
                Height = 40,
                LineColor = line,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
            };
            var sub = new TextBlock
            {
                Foreground = FgDim,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var up = new Button { Content = "↑", Width = 26, Padding = new Thickness(0, 1, 0, 1), FontSize = 11, ToolTip = "上移" };
            var dn = new Button { Content = "↓", Width = 26, Padding = new Thickness(0, 1, 0, 1), FontSize = 11, ToolTip = "下移", Margin = new Thickness(4, 0, 0, 0) };
            var captured = id;
            up.Click += (_, _) => MoveRow(captured, -1);
            dn.Click += (_, _) => MoveRow(captured, +1);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(up);
            btns.Children.Add(dn);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            Grid.SetColumn(labelTb, 0);
            Grid.SetColumn(value, 1);
            Grid.SetColumn(spark, 2);
            Grid.SetColumn(sub, 3);
            Grid.SetColumn(btns, 4);
            grid.Children.Add(labelTb);
            grid.Children.Add(value);
            grid.Children.Add(spark);
            grid.Children.Add(sub);
            grid.Children.Add(btns);

            var root = new Border
            {
                Style = (Style)FindResource("CardPanel"),
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(14, 6, 14, 6),
                Child = grid,
            };

            _rows[id] = new DashRow { Id = id, ValueTb = value, SubTb = sub, Spark = spark, Root = root };
            _rowHist[id] = new History(_settings.HistoryPoints);
        }
    }

    /// <summary>校验并补全保存的顺序（新增行自动追加到末尾）</summary>
    private List<string> ValidOrder()
    {
        var known = RowSpecs.Select(r => r.Id).ToList();
        var order = _settings.DashboardOrder.Where(known.Contains).Distinct().ToList();
        foreach (var k in known)
            if (!order.Contains(k)) order.Add(k);
        _settings.DashboardOrder = order;
        return order;
    }

    private void BuildDashboard()
    {
        DashboardRows.Children.Clear();
        foreach (var id in ValidOrder())
            DashboardRows.Children.Add(_rows[id].Root);
    }

    private void MoveRow(string id, int dir)
    {
        var order = ValidOrder();
        int i = order.IndexOf(id);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _settings.DashboardOrder = order;
        _settings.Save();
        BuildDashboard();
    }

    private void SetRow(string id, float? value, Func<float, string> fmt, Func<float, Brush>? color, string sub)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        if (value is float v)
        {
            row.ValueTb.Text = fmt(v);
            row.ValueTb.Foreground = color != null ? color(v) : Fg;
            _rowHist[id].Push(v);
        }
        else
        {
            row.ValueTb.Text = "--";
            row.ValueTb.Foreground = FgDim;
        }
        row.SubTb.Text = sub;
        row.Spark.SetValues(_rowHist[id].Values);
    }

    private static string GpuName(SensorSnapshot s) => s.GpuLoad != null ? "独显/核显传感器" : "GPU";

    private void SetChip(Border border, TextBlock text, string value, bool? ok)
    {
        text.Text = value;
        border.Background = ok == true ? ChipOk : ok == false ? ChipBad : ChipNeutral;
    }

    private static readonly Brush Bad = (Brush)Application.Current.Resources["BadBrush"];
    private static readonly Brush Warn = (Brush)Application.Current.Resources["WarnBrush"];
    private static readonly Brush Fg = (Brush)Application.Current.Resources["FgBrush"];
    private static readonly Brush FgDim = (Brush)Application.Current.Resources["FgDimBrush"];

    // ---------------- 散热模式与单次除尘 ----------------

    private async void BtnProfileSaving_Click(object sender, RoutedEventArgs e) =>
        await SetThermalProfileAsync(ThermalProfile.BatterySaving);

    private async void BtnProfileIntelligent_Click(object sender, RoutedEventArgs e) =>
        await SetThermalProfileAsync(ThermalProfile.IntelligentCooling);

    private async void BtnProfilePerformance_Click(object sender, RoutedEventArgs e) =>
        await SetThermalProfileAsync(ThermalProfile.ExtremePerformance);

    private async void BtnProfileRefresh_Click(object sender, RoutedEventArgs e) => await RefreshThermalProfileAsync();

    private Task RefreshThermalProfileAsync() => RunThermalProfileRequestAsync(_thermalProfiles.Refresh);

    private Task SetThermalProfileAsync(ThermalProfile profile) =>
        RunThermalProfileRequestAsync(() => _thermalProfiles.SetProfile(profile));

    private async Task RunThermalProfileRequestAsync(Func<bool> request)
    {
        if (_shuttingDown || _profileRequestInProgress) return;
        _profileRequestInProgress = true;
        BtnProfileSaving.IsEnabled = BtnProfileIntelligent.IsEnabled = BtnProfilePerformance.IsEnabled = false;
        try
        {
            await Task.Run(request);
            if (_shuttingDown) return;
            var profile = _thermalProfiles.CurrentProfile;
            string label = profile switch
            {
                ThermalProfile.BatterySaving => "节能",
                ThermalProfile.IntelligentCooling => "智能散热",
                ThermalProfile.ExtremePerformance => "性能",
                _ => "未知",
            };
            LblProfileCurrent.Text = "当前模式：" + label;
            LblProfileState.Text = _thermalProfiles.LastMessage;
            foreach (var (button, value) in new[]
            {
                (BtnProfileSaving, ThermalProfile.BatterySaving),
                (BtnProfileIntelligent, ThermalProfile.IntelligentCooling),
                (BtnProfilePerformance, ThermalProfile.ExtremePerformance),
            })
            {
                button.IsEnabled = _thermalProfiles.IsSupported;
                bool selected = profile == value;
                button.Background = selected ? new SolidColorBrush(Color.FromRgb(46, 74, 90)) : (Brush)FindResource("ChipBrush");
                button.BorderBrush = (Brush)FindResource(selected ? "AccentBrush" : "CardBorderBrush");
            }
        }
        catch (Exception ex)
        {
            if (!_shuttingDown)
            {
                LblProfileCurrent.Text = "当前模式：读取失败";
                LblProfileState.Text = ex.Message;
                App.Log("散热模式操作失败: " + ex);
            }
        }
        finally { _profileRequestInProgress = false; }
    }

    private void BtnDustStart_Click(object sender, RoutedEventArgs e)
    {
        EnergyDrvFan.StartDustCleaning();
        LblFanState.Text = EnergyDrvFan.LastMessage;
        UpdateFanControls();
    }

    private void BtnDustStop_Click(object sender, RoutedEventArgs e)
    {
        EnergyDrvFan.Stop("已停止除尘");
        LblFanState.Text = EnergyDrvFan.LastMessage;
        UpdateFanControls();
    }

    private void OnFanState(bool running, string msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // A newer selection may already have replaced this queued event.
            LblFanState.Text = EnergyDrvFan.LastMessage;
            UpdateFanControls();
        });
    }

    private void UpdateFanControls()
    {
        var mode = EnergyDrvFan.CurrentMode;
        bool restoring = EnergyDrvFan.RestorePending;
        LblFanState.Foreground = restoring ? Bad : Fg;
        BtnDustStart.IsEnabled = _energyAvailable && mode != FanControlMode.DustCleaning;
        BtnDustStop.IsEnabled = _energyAvailable && (mode == FanControlMode.DustCleaning || restoring);
        LblDustState.Text = mode == FanControlMode.DustCleaning
            ? "除尘：正在执行本次请求，约 11 秒后结束"
            : restoring ? "除尘：停止请求失败，请点击取消本次除尘重试" : "除尘：未运行";
        UpdateFanModeChip();
    }

    private void ToggleOverlay()
    {
        SetOverlayVisible(_overlay == null || !_overlay.IsVisible);
        ChkOverlay.IsChecked = _overlay != null && _overlay.IsVisible;
    }

    private void SetOverlayVisible(bool show)
    {
        if (show)
        {
            if (_overlay == null)
            {
                _overlay = new OverlayWindow { Corner = Math.Clamp(_settings.OverlayCorner, 0, 3) };
                if (_lastSnap != null) _overlay.Update(_lastSnap);
            }
            _overlay.Show();
            _overlay.ApplyPosition();
        }
        else
        {
            _overlay?.Hide();
        }
    }

    private void CbCloseAction_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CbCloseAction.SelectedIndex == 0) _settings.CloseToTray = true;
        else if (CbCloseAction.SelectedIndex == 1) _settings.CloseToTray = false;
        else return;
        _settings.Save();
    }

    private void ChkOverlay_Changed(object sender, RoutedEventArgs e)
    {
        _settings.OverlayEnabled = ChkOverlay.IsChecked == true;
        _settings.Save();
        SetOverlayVisible(_settings.OverlayEnabled);
    }

    private void CbOverlayCorner_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CbOverlayCorner.SelectedIndex < 0) return;
        _settings.OverlayCorner = CbOverlayCorner.SelectedIndex;
        _settings.Save();
        if (_overlay != null)
        {
            _overlay.Corner = _settings.OverlayCorner;
            _overlay.ApplyPosition();
        }
    }

    // ---------------- EC 工具 ----------------

    private void BtnEcRefresh_Click(object sender, RoutedEventArgs e)
    {
        LblEcView.Text = "读取中…";
        Task.Run(() =>
        {
            var dump = EcIO.Dump(256);
            Dispatcher.BeginInvoke(() =>
            {
                if (dump == null)
                {
                    LblEcView.Text = "读取失败：" + EcIO.Status;
                    return;
                }
                _currentDump = dump;
                for (int i = 0; i < 256; i++) _ecCells[i].SetCurrent(dump[i]);
                LblEcView.Text = $"已读取 {DateTime.Now:HH:mm:ss}（悬停查看地址）";
            });
        });
    }

    private void BtnEcSnapA_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDump == null) { LblEcView.Text = "请先「读取 EC」"; return; }
        for (int i = 0; i < 256; i++) _ecCells[i].SetA(_currentDump[i]);
        LblEcView.Text = "已记录快照 A";
    }

    private void BtnEcSnapB_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDump == null) { LblEcView.Text = "请先「读取 EC」"; return; }
        for (int i = 0; i < 256; i++) _ecCells[i].SetB(_currentDump[i]);
        LblEcView.Text = "已记录快照 B";
    }

    private void BtnEcDiff_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var c in _ecCells) { c.RefreshDiff(); if (c.Diff) n++; }
        LblEcView.Text = n == 0
            ? "A/B 无差异（或未设置快照）"
            : $"A/B 差异 {n} 字节（橙色高亮）";
    }

    // ---------------- 辅助类型 ----------------

    private sealed class History
    {
        private readonly Queue<double> _q = new();
        private readonly int _cap;
        public History(int cap) => _cap = Math.Max(10, cap);
        public void Push(double v)
        {
            _q.Enqueue(v);
            while (_q.Count > _cap) _q.Dequeue();
        }
        public double[] Values => _q.ToArray();
    }
}

/// <summary>EC 内存视图单元格：当前值 + 两份快照对比</summary>
public class EcCell : INotifyPropertyChanged
{
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(38, 38, 45));
    private static readonly Brush DiffBg = new SolidColorBrush(Color.FromRgb(122, 82, 34));
    private static readonly Brush Empty = new SolidColorBrush(Color.FromRgb(30, 30, 35));

    public int Addr { get; }
    public byte Cur { get; private set; }
    public byte A { get; private set; }
    public byte B { get; private set; }
    public bool HasA { get; private set; }
    public bool HasB { get; private set; }
    public bool Diff { get; private set; }

    public string Hex => HasCurrent ? Cur.ToString("X2") : "--";
    public Brush CellBrush => !HasCurrent ? Empty : Diff ? DiffBg : Normal;
    public string ToolTipText => $"EC 0x{Addr:X2}\n当前 0x{Cur:X2}\n快照A {(HasA ? $"0x{A:X2}" : "—")}\n快照B {(HasB ? $"0x{B:X2}" : "—")}";

    public bool HasCurrent { get; private set; }

    public EcCell(int addr) => Addr = addr;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCurrent(byte v)
    {
        Cur = v;
        HasCurrent = true;
        Raise(nameof(Cur), nameof(Hex), nameof(CellBrush), nameof(ToolTipText));
    }

    public void SetA(byte v)
    {
        A = v; HasA = true;
        RefreshDiff();
        Raise(nameof(ToolTipText));
    }

    public void SetB(byte v)
    {
        B = v; HasB = true;
        RefreshDiff();
        Raise(nameof(ToolTipText));
    }

    public void RefreshDiff()
    {
        bool d = HasA && HasB && A != B;
        if (d != Diff)
        {
            Diff = d;
            Raise(nameof(Diff), nameof(CellBrush));
        }
    }

    private void Raise(params string[] props)
    {
        foreach (var p in props)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
