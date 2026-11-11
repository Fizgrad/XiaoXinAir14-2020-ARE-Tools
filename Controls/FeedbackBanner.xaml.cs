using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanController.Models;

namespace FanController.Controls;

public partial class FeedbackBanner : UserControl
{
    public ObservableCollection<FeedbackMessage> History { get; } = new();
    public FeedbackMessage? Current { get; private set; }
    public FeedbackBanner() { InitializeComponent(); HistoryItems.ItemsSource = History; }

    public void Show(FeedbackMessage message)
    {
        Current = message;
        Title.Text = message.Title;
        Detail.Text = message.Detail;
        Detail.ToolTip = message.Detail;
        TimeLabel.Text = (message.Kind == FeedbackKind.Pending ? "" : "上次操作 · ") + $"{message.KindLabel} · {message.Time:HH:mm:ss}";
        Progress.Visibility = message.Kind == FeedbackKind.Pending ? Visibility.Visible : Visibility.Collapsed;
        var (background, border, symbol) = message.Kind switch
        {
            FeedbackKind.Success => ("#FF203329", "#FF4F8A61", "✓"),
            FeedbackKind.Error => ("#FF3B2529", "#FFB85B65", "!"),
            FeedbackKind.Pending => ("#FF20313E", "#FF4FC3F7", "…"),
            _ => ("#FF2E2B23", "#FF9B8150", "●"),
        };
        Frame.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        Frame.BorderBrush = Symbol.Foreground = (Brush)new BrushConverter().ConvertFromString(border)!;
        Symbol.Text = symbol;
        if (message.Kind != FeedbackKind.Pending)
        {
            History.Insert(0, message);
            while (History.Count > 12) History.RemoveAt(History.Count - 1);
            HistoryExpander.Visibility = Visibility.Visible;
        }
    }
}
