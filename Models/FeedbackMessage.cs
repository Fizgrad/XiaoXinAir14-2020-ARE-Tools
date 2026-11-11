namespace FanController.Models;

public enum FeedbackKind { Info, Pending, Success, Error }

public sealed record FeedbackMessage(FeedbackKind Kind, string Title, string Detail, DateTimeOffset Time)
{
    public string KindLabel => Kind switch
    { FeedbackKind.Pending => "正在处理", FeedbackKind.Success => "操作成功", FeedbackKind.Error => "操作失败", _ => "提示" };
    public string HistoryTitle => $"{Time:HH:mm:ss}  {KindLabel} · {Title}";
}
