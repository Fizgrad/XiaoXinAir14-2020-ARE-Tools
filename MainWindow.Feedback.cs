using System.Windows.Media;
using FanController.Models;

namespace FanController;

public partial class MainWindow
{
    private DateTimeOffset? _snapshotATime, _snapshotBTime;
    private void Feedback(FeedbackKind kind, string title, string detail)
    {
        if (!_shuttingDown) ActionFeedback.Show(new(kind, title, detail, DateTimeOffset.Now));
    }

    private bool SaveWindowPreferences(Action rollback, Action synchronize, string title, string detail)
    {
        if (_settings.TrySave(out string error)) { Feedback(FeedbackKind.Success, title, detail); return true; }
        rollback();
        _updatingUiSettings = true;
        try { synchronize(); }
        finally { _updatingUiSettings = false; }
        Feedback(FeedbackKind.Error, "设置未保存", error + "；已保留原设置。");
        return false;
    }

    private void UpdateSnapshotState()
    {
        LblEcSnapshots.Text = "快照 A：" + (_snapshotATime?.ToString("HH:mm:ss") ?? "未设置")
            + "    快照 B：" + (_snapshotBTime?.ToString("HH:mm:ss") ?? "未设置");
        LblEcSnapshots.Foreground = (Brush)FindResource(_snapshotATime.HasValue && _snapshotBTime.HasValue ? "GoodBrush" : "FgDimBrush");
    }
}
