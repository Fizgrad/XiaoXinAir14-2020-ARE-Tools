using System.Windows;
using System.Windows.Media;

namespace FanController.Controls;

/// <summary>轻量趋势线：调用 SetValues 推入新序列后自动重绘</summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty LineColorProperty =
        DependencyProperty.Register(nameof(LineColor), typeof(Color), typeof(Sparkline),
            new FrameworkPropertyMetadata(Color.FromRgb(79, 195, 247),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public Color LineColor
    {
        get => (Color)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    private double[] _values = Array.Empty<double>();

    public void SetValues(IEnumerable<double>? values)
    {
        _values = values?.ToArray() ?? Array.Empty<double>();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 4 || bounds.Height < 4 || _values.Length < 2) return;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in _values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (max - min < 1e-6) { max += 1; min -= 1; }
        double pad = (max - min) * 0.12;
        min -= pad; max += pad;

        double xStep = bounds.Width / (_values.Length - 1);
        double y(double v) => bounds.Height - (v - min) / (max - min) * bounds.Height;

        var points = new List<Point>(_values.Length);
        for (int i = 0; i < _values.Length; i++)
            points.Add(new Point(i * xStep, y(_values[i])));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++)
                ctx.LineTo(points[i], true, false);
        }
        geo.Freeze();

        // 面积填充
        var areaGeo = new StreamGeometry();
        using (var ctx = areaGeo.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, bounds.Bottom), true, true);
            foreach (var p in points) ctx.LineTo(p, true, false);
            ctx.LineTo(new Point(points[^1].X, bounds.Bottom), true, false);
        }
        areaGeo.Freeze();

        var lineBrush = new SolidColorBrush(LineColor);
        lineBrush.Freeze();
        var fillBrush = new SolidColorBrush(Color.FromArgb(48, LineColor.R, LineColor.G, LineColor.B));
        fillBrush.Freeze();

        dc.DrawGeometry(fillBrush, null, areaGeo);
        dc.DrawGeometry(null, new Pen(lineBrush, 1.4), geo);
    }
}
