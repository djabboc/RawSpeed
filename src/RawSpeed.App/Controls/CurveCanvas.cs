using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RawSpeed.Core;

namespace RawSpeed.App.Controls;

/// <summary>Immutable point-curve editor. Only endpoint abscissas and point order are constrained.</summary>
public sealed class CurveCanvas : FrameworkElement
{
    internal static readonly IReadOnlyList<CurvePoint> Identity = new[] { new CurvePoint(0, 0), new CurvePoint(1, 1) };
    private bool _dragging;
    public CurveCanvas() { Focusable = true; Cursor = Cursors.Cross; InputMethod.SetIsInputMethodEnabled(this, false); }
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(nameof(Curve), typeof(ToneCurve), typeof(CurveCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public ToneCurve? Curve { get => (ToneCurve?)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public int SelectedIndex { get; private set; }
    public event EventHandler? SelectionChanged;
    private IReadOnlyList<CurvePoint> Points => Curve?.Points ?? Identity;
    private Point Screen(CurvePoint point) => new(8 + point.X * Math.Max(1, ActualWidth - 16), 8 + (1 - point.Y) * Math.Max(1, ActualHeight - 16));
    private CurvePoint Normalized(Point point) => new(Math.Clamp((point.X - 8) / Math.Max(1, ActualWidth - 16), 0, 1), Math.Clamp(1 - (point.Y - 8) / Math.Max(1, ActualHeight - 16), 0, 1));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 21, 27)), null, new Rect(RenderSize));
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(48, 57, 66)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double fraction = i / 4d;
            dc.DrawLine(gridPen, Screen(new(fraction, 0)), Screen(new(fraction, 1)));
            dc.DrawLine(gridPen, Screen(new(0, fraction)), Screen(new(1, fraction)));
        }
        dc.DrawLine(gridPen, Screen(new(0, 0)), Screen(new(1, 1)));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Screen(new(0, Curve?.Evaluate(0) ?? 0)), false, false);
            for (int i = 1; i <= 256; i++) context.LineTo(Screen(new(i / 256d, Curve?.Evaluate(i / 256d) ?? i / 256d)), true, false);
        }
        dc.DrawGeometry(null, new Pen(Brushes.LightGreen, 1.8), geometry);
        for (int i = 0; i < Points.Count; i++) dc.DrawEllipse(i == SelectedIndex ? Brushes.LightGreen : Brushes.White, new Pen(Brushes.Black, 1), Screen(Points[i]), 4, 4);
    }

    public void MoveSelected(double x, double y)
    {
        var points = Points.ToArray();
        SelectedIndex = Math.Clamp(SelectedIndex, 0, points.Length - 1);
        if (SelectedIndex == 0) x = 0;
        else if (SelectedIndex == points.Length - 1) x = 1;
        else
        {
            double minimum = points[SelectedIndex - 1].X + 0.000001000001;
            double maximum = points[SelectedIndex + 1].X - 0.000001000001;
            x = minimum <= maximum ? Math.Clamp(x, minimum, maximum) : points[SelectedIndex].X;
        }
        points[SelectedIndex] = new(x, Math.Clamp(y, 0, 1));
        SetCurrentValue(CurveProperty, new ToneCurve(points));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelected()
    {
        if (SelectedIndex <= 0 || SelectedIndex >= Points.Count - 1) return;
        var points = Points.ToList();
        points.RemoveAt(SelectedIndex);
        SelectedIndex--;
        SetCurrentValue(CurveProperty, new ToneCurve(points));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool InsertPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is <= 0 or >= 1 || y is < 0 or > 1 || Points.Count >= 64 || Points.Any(p => Math.Abs(p.X - x) < 0.000001)) return false;
        var point = new CurvePoint(x, y);
        var points = Points.Append(point).OrderBy(p => p.X).ToArray();
        SelectedIndex = Array.IndexOf(points, point);
        SetCurrentValue(CurveProperty, new ToneCurve(points));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Point screen = e.GetPosition(this);
        int nearest = Enumerable.Range(0, Points.Count).MinBy(i => (Screen(Points[i]) - screen).Length);
        if ((Screen(Points[nearest]) - screen).Length < 12) SelectedIndex = nearest;
        else if (e.ChangedButton == MouseButton.Left && Points.Count < 64)
        {
            var point = Normalized(screen);
            InsertPoint(point.X, point.Y);
        }
        if (e.ChangedButton == MouseButton.Right && (Screen(Points[Math.Min(nearest, Points.Count - 1)]) - screen).Length < 12) DeleteSelected();
        if (e.ChangedButton == MouseButton.Left) { _dragging = true; CaptureMouse(); }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var point = Normalized(e.GetPosition(this));
        MoveSelected(point.X, point.Y);
    }
    protected override void OnMouseUp(MouseButtonEventArgs e) { base.OnMouseUp(e); _dragging = false; ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragging = false; }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.Key is Key.Delete or Key.Back) { DeleteSelected(); e.Handled = true; } base.OnKeyDown(e); }
}
