using System.Windows;
using System.Windows.Media;
using RawSpeed.Core;

namespace RawSpeed.App.Controls;

public sealed class CurveView : FrameworkElement
{
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(nameof(Settings), typeof(EditSettings), typeof(CurveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public EditSettings? Settings { get => (EditSettings?)GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(17, 21, 27)), null, new Rect(RenderSize), 5, 5);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(41, 47, 54)), 1);
        for (var i = 1; i < 4; i++)
        {
            context.DrawLine(pen, new Point(ActualWidth * i / 4, 0), new Point(ActualWidth * i / 4, ActualHeight));
            context.DrawLine(pen, new Point(0, ActualHeight * i / 4), new Point(ActualWidth, ActualHeight * i / 4));
        }
        context.DrawLine(pen, new Point(0, ActualHeight), new Point(ActualWidth, 0));
        var settings = Settings ?? new EditSettings();
        var curve = new StreamGeometry();
        using (var drawing = curve.Open())
        {
            drawing.BeginFigure(new Point(0, ActualHeight), false, false);
            for (var i = 0; i <= 100; i++)
                drawing.LineTo(new Point(i / 100d * ActualWidth, (1 - ImageProcessor.EvaluateCurve(i / 100d, settings)) * ActualHeight), true, false);
        }
        curve.Freeze();
        var brush = new SolidColorBrush(Color.FromRgb(200, 242, 138));
        context.DrawGeometry(null, new Pen(brush, 1.5), curve);
        foreach (var x in new[] { 0.25, 0.5, 0.75 })
            context.DrawEllipse(brush, null, new Point(x * ActualWidth, (1 - ImageProcessor.EvaluateCurve(x, settings)) * ActualHeight), 3, 3);
    }
}
