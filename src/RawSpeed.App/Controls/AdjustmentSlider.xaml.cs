using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RawSpeed.App.Controls;

public partial class AdjustmentSlider : UserControl
{
    public AdjustmentSlider() => InitializeComponent();
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(AdjustmentSlider), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(AdjustmentSlider), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(AdjustmentSlider), new PropertyMetadata(-100d));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(AdjustmentSlider), new PropertyMetadata(100d));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(AdjustmentSlider), new PropertyMetadata(1d));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    private void ResetValue(object sender, MouseButtonEventArgs e) { SetCurrentValue(ValueProperty, Math.Clamp(0, Minimum, Maximum)); e.Handled = true; }
}
