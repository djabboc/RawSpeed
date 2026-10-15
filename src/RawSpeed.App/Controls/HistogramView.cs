using System.Windows;
using System.Windows.Media;

namespace RawSpeed.App.Controls;

public sealed class HistogramView : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(int[]), typeof(HistogramView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public int[]? Values { get => (int[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(17, 21, 27)), null, new Rect(RenderSize), 5, 5);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(41, 47, 54)), 1);
        for (var i = 1; i < 4; i++) context.DrawLine(gridPen, new Point(ActualWidth * i / 4, 0), new Point(ActualWidth * i / 4, ActualHeight));
        if (Values is not { Length: 768 } values) return;
        var max = Math.Max(1, values.Skip(3).Take(250).Concat(values.Skip(259).Take(250)).Concat(values.Skip(515).Take(250)).Max());
        var colors = new[] { Color.FromArgb(100, 245, 125, 125), Color.FromArgb(100, 159, 224, 140), Color.FromArgb(100, 124, 169, 240) };
        for (var channel = 0; channel < 3; channel++)
        {
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(new Point(0, ActualHeight), true, true);
                for (var x = 0; x < 256; x++)
                    drawing.LineTo(new Point(x / 255d * ActualWidth, ActualHeight * (1 - Math.Min(1, values[channel * 256 + x] / (double)max))), true, false);
                drawing.LineTo(new Point(ActualWidth, ActualHeight), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(new SolidColorBrush(colors[channel]), null, geometry);
        }
    }
}
