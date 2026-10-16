using System.Windows;
using System.Windows.Controls;
using RawSpeed.Core;

namespace RawSpeed.App.Controls;

public partial class CurveEditor : UserControl
{
    private bool _synchronizing;
    public CurveEditor()
    {
        InitializeComponent();
        Graph.SelectionChanged += (_, _) => SynchronizePoint();
    }

    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(nameof(Curve), typeof(ToneCurve), typeof(CurveEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, CurveChanged));
    public static readonly DependencyProperty InputProperty = DependencyProperty.Register(nameof(Input), typeof(double), typeof(CurveEditor), new PropertyMetadata(0d, PointChanged));
    public static readonly DependencyProperty OutputProperty = DependencyProperty.Register(nameof(Output), typeof(double), typeof(CurveEditor), new PropertyMetadata(0d, PointChanged));
    public ToneCurve? Curve { get => (ToneCurve?)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public double Input { get => (double)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Output { get => (double)GetValue(OutputProperty); set => SetValue(OutputProperty, value); }
    private static void CurveChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) => ((CurveEditor)owner).SynchronizePoint();
    private static void PointChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CurveEditor)owner;
        if (!editor._synchronizing && editor.Graph is not null) editor.Graph.MoveSelected(editor.Input / 255, editor.Output / 255);
    }
    private void SynchronizePoint()
    {
        if (Graph is null) return;
        var points = Curve?.Points ?? CurveCanvas.Identity;
        var point = points[Math.Clamp(Graph.SelectedIndex, 0, points.Count - 1)];
        _synchronizing = true;
        SetCurrentValue(InputProperty, point.X * 255);
        SetCurrentValue(OutputProperty, point.Y * 255);
        _synchronizing = false;
    }
    private void ResetCurve(object sender, RoutedEventArgs e) => SetCurrentValue(CurveProperty, null);
}
