using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RawSpeed.App.Controls;

public partial class AdjustmentSlider : UserControl
{
    public AdjustmentSlider() => InitializeComponent();
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(AdjustmentSlider), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(AdjustmentSlider), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, null, CoerceNumber));
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(AdjustmentSlider), new PropertyMetadata(0d));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(AdjustmentSlider), new PropertyMetadata(-100d));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(AdjustmentSlider), new PropertyMetadata(100d));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(AdjustmentSlider), new PropertyMetadata(1d));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    private static object CoerceNumber(DependencyObject owner, object value)
    {
        var control = (AdjustmentSlider)owner;
        double number = (double)value;
        return Math.Clamp(double.IsFinite(number) ? number : control.DefaultValue, control.Minimum, control.Maximum);
    }
    private void ResetClick(object sender, RoutedEventArgs e) => SetCurrentValue(ValueProperty, DefaultValue);
    private void ResetValue(object sender, MouseButtonEventArgs e) { SetCurrentValue(ValueProperty, DefaultValue); e.Handled = true; }
    private void NumberLostFocus(object sender, RoutedEventArgs e)
    {
        var binding = Number.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
        binding?.UpdateTarget();
    }
    private void NumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Number.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (e.Key is Key.Enter or Key.Escape) { Number.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); e.Handled = true; }
    }
}
