using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanController.Models;
using FanController.Services;
using Microsoft.Win32;

namespace FanController.Views;

public partial class HardwareInventoryView : UserControl
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _scanning;
    public bool AutoScan { get; set; } = true;
    public HardwareInventoryReport? Report { get; private set; }
    public event Action<FeedbackMessage>? FeedbackPublished;
    public sealed record CategoryOption(string Name, string Label);
    public sealed record DeviceRow(string Category, string Name, string State, Brush StateBrush, IReadOnlyList<HardwareField> Fields);
    public sealed record CheckRow(string Name, string State, Brush StateBrush, string Detail);

    public HardwareInventoryView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (AutoScan && Report == null && !_scanning) await ScanAsync(); };
    }
    private void Publish(FeedbackKind kind, string title, string detail) => FeedbackPublished?.Invoke(new(kind, title, detail, DateTimeOffset.Now));
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    public async Task ScanAsync()
    {
        if (_scanning || _cancellation.IsCancellationRequested) return;
        _scanning = true; BtnScan.IsEnabled = BtnExport.IsEnabled = false; ScanProgress.Visibility = Visibility.Visible;
        Publish(FeedbackKind.Pending, "正在检测硬件…", "正在读取设备和健康状态。");
        try
        {
            var progress = new Progress<string>(message => { if (!_cancellation.IsCancellationRequested) ScanStatus.Text = message; });
            var report = await Task.Run(() => new HardwareInventoryReader().Scan(_cancellation.Token, progress));
            if (_cancellation.IsCancellationRequested) return;
            if (report.Devices.Count == 0 && report.Issues.Count > 0) throw new InvalidOperationException(report.Issues[0].Detail);
            ShowReport(report);
            Publish(FeedbackKind.Success, "硬件检测完成", $"已识别 {report.Devices.Count} 个项目 · {report.Checks.Count(c => c.Status is HardwareStatus.Attention or HardwareStatus.Error)} 项需关注");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ScanStatus.Text = "检测失败：" + ex.Message; Publish(FeedbackKind.Error, "硬件检测失败", ex.Message);
        }
        finally { _scanning = false; BtnScan.IsEnabled = true; BtnExport.IsEnabled = Report != null; ScanProgress.Visibility = Visibility.Collapsed; }
    }

    public void ShowReport(HardwareInventoryReport report)
    {
        Report = report;
        DeviceCount.Text = report.Devices.Count.ToString(); NormalCount.Text = report.Checks.Count(c => c.Status == HardwareStatus.Normal).ToString();
        ConcernCount.Text = report.Checks.Count(c => c.Status is HardwareStatus.Attention or HardwareStatus.Error).ToString();
        UnknownCount.Text = report.Checks.Count(c => c.Status == HardwareStatus.Unknown).ToString();
        ScanStatus.Text = $"{report.Computer} · 更新于 {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
        string? category = (Categories.SelectedItem as CategoryOption)?.Name;
        var options = new[] { new CategoryOption("", $"全部  {report.Devices.Count}") }.Concat(report.Devices.GroupBy(d => d.Category).Select(g => new CategoryOption(g.Key, $"{g.Key}  {g.Count()}"))).ToArray();
        Categories.ItemsSource = options; Categories.SelectedItem = options.FirstOrDefault(c => c.Name == category) ?? options[0];
        CheckItems.ItemsSource = report.Checks.OrderBy(c => c.Status switch { HardwareStatus.Error => 0, HardwareStatus.Attention => 1, HardwareStatus.Unknown => 2, _ => 3 })
            .Select(c => new CheckRow(c.Name, HardwareInventoryReader.StatusName(c.Status), StatusBrush(c.Status), c.Detail)).ToArray();
        IssuesExpander.Visibility = report.Issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IssueDetails.Text = string.Join("\n\n", report.Issues.Select(i => i.Source + "：" + i.Detail));
        BtnExport.IsEnabled = true; Filter();
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) => Filter();
    private void Filter()
    {
        if (Report == null || SearchText == null || DeviceItems == null) return;
        string query = SearchText.Text.Trim(), category = (Categories.SelectedItem as CategoryOption)?.Name ?? "";
        var rows = Report.Devices.Where(d => (category.Length == 0 || d.Category == category) && (query.Length == 0
            || (d.Name + " " + d.Category + " " + string.Join(" ", d.Fields.Select(f => f.Name + " " + f.Value))).Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(d => new DeviceRow(d.Category, d.Name, d.State, StatusBrush(d.Status), d.Fields)).ToArray();
        DeviceItems.ItemsSource = rows; EmptyMessage.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = "没有匹配的硬件项目";
    }
    private Brush StatusBrush(HardwareStatus status) => (Brush)FindResource(status switch
    { HardwareStatus.Normal => "GoodBrush", HardwareStatus.Attention => "WarnBrush", HardwareStatus.Error => "BadBrush", _ => "FgDimBrush" });
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var report = Report; if (report == null) return;
        var dialog = new SaveFileDialog { Filter = "HTML 硬件报告 (*.html)|*.html|JSON 数据 (*.json)|*.json", FileName = $"硬件报告-{report.GeneratedAt:yyyy-MM-dd-HHmm}.html" };
        if (dialog.ShowDialog() != true) return;
        BtnExport.IsEnabled = false; Publish(FeedbackKind.Pending, "正在导出硬件报告…", dialog.FileName);
        try { await Task.Run(() => HardwareReportExporter.Export(report, dialog.FileName)); Publish(FeedbackKind.Success, "硬件报告已导出", dialog.FileName); }
        catch (Exception ex) { Publish(FeedbackKind.Error, "硬件报告导出失败", ex.Message); }
        finally { BtnExport.IsEnabled = Report != null; }
    }
    public void Stop() => _cancellation.Cancel();
}
