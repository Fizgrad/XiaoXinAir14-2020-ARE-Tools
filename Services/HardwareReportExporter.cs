using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FanController.Models;

namespace FanController.Services;

public static class HardwareReportExporter
{
    public static string Json(HardwareInventoryReport report) => JsonSerializer.Serialize(report, new JsonSerializerOptions
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    public static void Export(HardwareInventoryReport report, string path) => File.WriteAllText(path,
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? Json(report) : Html(report), new UTF8Encoding(false));

    public static string Html(HardwareInventoryReport report)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        static string Badge(HardwareStatus status) => $"<span class='badge {status.ToString().ToLowerInvariant()}'>{E(HardwareInventoryReader.StatusName(status))}</span>";
        var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>FanController 硬件报告</title><style>");
        html.Append("body{margin:0;background:#f3f5f7;color:#202632;font:15px/1.6 system-ui,'Microsoft YaHei',sans-serif}main{max-width:1080px;margin:32px auto;padding:0 24px}h1{margin-bottom:4px}h2{margin-top:30px}article{background:white;border:1px solid #dde3e8;border-radius:12px;padding:20px;margin:12px 0}h3{margin:0 0 16px}.muted{color:#627080}.badge{font-size:12px;border-radius:20px;padding:4px 10px;background:#e9eef2;float:right}.normal{color:#267145;background:#e4f3e9}.attention{color:#8d5900;background:#fff1d6}.error{color:#a52e39;background:#ffe7e9}dl{display:grid;grid-template-columns:160px 1fr;gap:8px 18px;margin:0}dt{color:#627080}dd{margin:0;overflow-wrap:anywhere}.check{display:flex;gap:16px;align-items:center;padding:14px 0;border-bottom:1px solid #edf0f3}.check:last-child{border:0}.check .badge{float:none;white-space:nowrap}pre{white-space:pre-wrap;overflow-wrap:anywhere}@media(max-width:600px){dl{grid-template-columns:1fr;gap:2px}dd{margin-bottom:12px}.check{align-items:start}}@media print{body{background:white}article{break-inside:avoid}}</style><main>");
        html.Append($"<h1>硬件检测报告</h1><p class='muted'>{E(report.Computer)} · {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz} · FanController {E(report.AppVersion)}</p>");
        int normal = report.Checks.Count(c => c.Status == HardwareStatus.Normal), concern = report.Checks.Count(c => c.Status is HardwareStatus.Attention or HardwareStatus.Error), unknown = report.Checks.Count(c => c.Status == HardwareStatus.Unknown);
        html.Append($"<p>设备 {report.Devices.Count} 项 · 正常检查 {normal} 项 · 需关注 {concern} 项 · 暂无数据 {unknown} 项</p><h2>健康检查</h2><article>");
        foreach (var check in report.Checks) html.Append($"<div class='check'>{Badge(check.Status)}<div><strong>{E(check.Name)}</strong><br>{E(check.Detail)}</div></div>");
        html.Append("</article>");
        foreach (var group in report.Devices.GroupBy(d => d.Category))
        {
            html.Append($"<h2>{E(group.Key)}</h2>");
            foreach (var device in group)
            {
                html.Append($"<article><h3><span class='badge {device.Status.ToString().ToLowerInvariant()}'>{E(device.State)}</span>{E(device.Name)}</h3><dl>");
                foreach (var field in device.Fields) html.Append($"<dt>{E(field.Name)}</dt><dd>{E(field.Value)}</dd>");
                html.Append("</dl></article>");
            }
        }
        if (report.Issues.Count > 0)
        {
            html.Append("<h2>读取情况</h2><article>");
            foreach (var issue in report.Issues) html.Append($"<p><strong>{E(issue.Source)}</strong>：{E(issue.Detail)}</p>");
            html.Append("</article>");
        }
        return html.Append("</main></html>").ToString();
    }
}
