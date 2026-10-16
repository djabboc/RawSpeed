using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawSpeed.Core;

namespace RawSpeed.App.Controls;

/// <summary>One coordinate system for zoom, synchronized comparisons, crop handles and sampling.</summary>
public sealed class PhotoViewport : FrameworkElement
{
    private Point _start;
    private Vector _pan;
    private Vector _startPan;
    private Rect _startCrop;
    private Rect? _dragCrop;
    private int _handle = -1;
    private bool _panning;
    private bool _dividing;
    private bool _fit = true;
    private double _divider = 0.5;
    private double _zoom = 1;
    private static readonly Brush Backdrop = new SolidColorBrush(Color.FromRgb(12, 15, 19));
    private static readonly Pen CropPen = new(Brushes.LightGreen, 1.5);

    public PhotoViewport() { ClipToBounds = true; Focusable = true; InputMethod.SetIsInputMethodEnabled(this, false); }
    private static DependencyProperty VisualProperty(string name, Type type, object? initial = null) => DependencyProperty.Register(name, type, typeof(PhotoViewport), new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SourceProperty = VisualProperty(nameof(Source), typeof(BitmapSource));
    public static readonly DependencyProperty BeforeProperty = VisualProperty(nameof(Before), typeof(BitmapSource));
    public static readonly DependencyProperty CropProperty = VisualProperty(nameof(Crop), typeof(NormalizedRect));
    public static readonly DependencyProperty CompositionProperty = VisualProperty(nameof(Composition), typeof(bool), false);
    public static readonly DependencyProperty PickGrayProperty = VisualProperty(nameof(PickGray), typeof(bool), false);
    public static readonly DependencyProperty ComparisonProperty = DependencyProperty.Register(nameof(Comparison), typeof(int), typeof(PhotoViewport), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (_, _) => { }));
    public static readonly DependencyProperty LogicalWidthProperty = VisualProperty(nameof(LogicalWidth), typeof(double), 0d);
    public static readonly DependencyProperty LogicalHeightProperty = VisualProperty(nameof(LogicalHeight), typeof(double), 0d);
    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public BitmapSource? Before { get => (BitmapSource?)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public NormalizedRect? Crop { get => (NormalizedRect?)GetValue(CropProperty); set => SetValue(CropProperty, value); }
    public bool Composition { get => (bool)GetValue(CompositionProperty); set => SetValue(CompositionProperty, value); }
    public bool PickGray { get => (bool)GetValue(PickGrayProperty); set => SetValue(PickGrayProperty, value); }
    /// <summary>0 edited, 1 original, 2 synchronized side-by-side, 3 draggable wipe.</summary>
    public int Comparison { get => (int)GetValue(ComparisonProperty); set => SetValue(ComparisonProperty, value); }
    public double LogicalWidth { get => (double)GetValue(LogicalWidthProperty); set => SetValue(LogicalWidthProperty, value); }
    public double LogicalHeight { get => (double)GetValue(LogicalHeightProperty); set => SetValue(LogicalHeightProperty, value); }
    public double Zoom => _zoom;
    public event Action<NormalizedRect>? CropCommitted;
    public event Action<double, double>? GrayPicked;
    public event Action<int, ModifierKeys, Point>? WheelGesture;
    public event EventHandler? ZoomChanged;
    private bool SideBySide => !Composition && Comparison == 2;
    private double PanelWidth => SideBySide ? ActualWidth / 2 : ActualWidth;
    private double ImageWidth => LogicalWidth > 0 ? LogicalWidth : Source?.PixelWidth ?? 1;
    private double ImageHeight => LogicalHeight > 0 ? LogicalHeight : Source?.PixelHeight ?? 1;

    public Rect ImageBounds
    {
        get
        {
            double zoom = _fit ? Math.Min(Math.Max(1, PanelWidth - 32) / ImageWidth, Math.Max(1, ActualHeight - 32) / ImageHeight) : _zoom;
            if (_fit) _zoom = zoom;
            return new Rect((PanelWidth - ImageWidth * zoom) / 2 + _pan.X, (ActualHeight - ImageHeight * zoom) / 2 + _pan.Y, ImageWidth * zoom, ImageHeight * zoom);
        }
    }

    public void Fit() { _fit = true; _pan = default; InvalidateVisual(); ZoomChanged?.Invoke(this, EventArgs.Empty); }
    public void ActualSize() => ZoomAt(1, new Point(PanelWidth / 2, ActualHeight / 2));
    public void ZoomBy(double factor, Point? anchor = null) => ZoomAt(Zoom * factor, anchor ?? new Point(PanelWidth / 2, ActualHeight / 2));
    private void ZoomAt(double zoom, Point anchor)
    {
        var old = ImageBounds;
        if (SideBySide && anchor.X >= PanelWidth) anchor.X -= PanelWidth;
        double x = (anchor.X - old.X) / old.Width, y = (anchor.Y - old.Y) / old.Height;
        _fit = false;
        _zoom = Math.Clamp(zoom, 0.01, 16);
        _pan = new Vector(anchor.X - x * ImageWidth * _zoom - (PanelWidth - ImageWidth * _zoom) / 2,
            anchor.Y - y * ImageHeight * _zoom - (ActualHeight - ImageHeight * _zoom) / 2);
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(RenderSize));
        if (Source is null) return;
        var bounds = ImageBounds;
        if (SideBySide)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, PanelWidth, ActualHeight)));
            dc.DrawImage(Before ?? Source, bounds);
            dc.Pop();
            dc.PushClip(new RectangleGeometry(new Rect(PanelWidth, 0, PanelWidth, ActualHeight)));
            dc.DrawImage(Source, new Rect(bounds.X + PanelWidth, bounds.Y, bounds.Width, bounds.Height));
            dc.Pop();
            dc.DrawLine(new Pen(Brushes.DimGray, 1), new Point(PanelWidth, 0), new Point(PanelWidth, ActualHeight));
            Label(dc, "原图", 12); Label(dc, "当前", PanelWidth + 12);
        }
        else
        {
            dc.DrawImage(!Composition && Comparison == 1 ? Before ?? Source : Source, bounds);
            if (!Composition && Comparison == 3)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth * _divider, ActualHeight)));
                dc.DrawImage(Before ?? Source, bounds);
                dc.Pop();
                dc.DrawLine(new Pen(Brushes.White, 2), new Point(ActualWidth * _divider, 0), new Point(ActualWidth * _divider, ActualHeight));
                Label(dc, "原图", 12); Label(dc, "当前", Math.Min(ActualWidth - 65, ActualWidth * _divider + 12));
            }
            else if (!Composition && Comparison == 1) Label(dc, "原图", 12);
        }
        if (Composition) DrawCrop(dc, bounds);
    }

    private void Label(DrawingContext dc, string text, double x)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 20, 26, 32)), null, new Rect(x - 4, 8, 48, 27), 3, 3);
        dc.DrawText(formatted, new Point(x + 4, 12));
    }

    private Rect CurrentCrop => _dragCrop ?? (Crop is { } crop ? new Rect(crop.X, crop.Y, crop.Width, crop.Height) : new Rect(0, 0, 1, 1));
    private static Rect ToScreen(Rect crop, Rect bounds) => new(bounds.X + crop.X * bounds.Width, bounds.Y + crop.Y * bounds.Height, crop.Width * bounds.Width, crop.Height * bounds.Height);
    private static Point[] Handles(Rect r) => [r.TopLeft, new(r.Left + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Top + r.Height / 2), r.BottomRight, new(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Top + r.Height / 2)];
    private void DrawCrop(DrawingContext dc, Rect bounds)
    {
        var crop = ToScreen(CurrentCrop, bounds);
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(crop));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null, mask);
        dc.DrawRectangle(null, CropPen, crop);
        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 0.8);
        for (int i = 1; i < 3; i++)
        {
            dc.DrawLine(thirds, new(crop.Left + crop.Width * i / 3, crop.Top), new(crop.Left + crop.Width * i / 3, crop.Bottom));
            dc.DrawLine(thirds, new(crop.Left, crop.Top + crop.Height * i / 3), new(crop.Right, crop.Top + crop.Height * i / 3));
        }
        foreach (Point point in Handles(crop)) dc.DrawRectangle(Brushes.White, CropPen, new Rect(point.X - 4, point.Y - 4, 8, 8));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        WheelGesture?.Invoke(e.Delta, Keyboard.Modifiers, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (Source is null) return;
        Focus();
        _start = e.GetPosition(this);
        var bounds = ImageBounds;
        Point point = _start;
        if (SideBySide && point.X >= PanelWidth) point.X -= PanelWidth;
        if (PickGray && e.ChangedButton == MouseButton.Left && bounds.Contains(point))
        {
            GrayPicked?.Invoke((point.X - bounds.X) / bounds.Width, (point.Y - bounds.Y) / bounds.Height);
            e.Handled = true; return;
        }
        if (Composition && e.ChangedButton == MouseButton.Left)
        {
            _startCrop = CurrentCrop;
            var screen = ToScreen(_startCrop, bounds);
            var handles = Handles(screen);
            _handle = Array.FindIndex(handles, p => (p - point).Length <= 11);
            if (_handle < 0) _handle = screen.Contains(point) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 8 : 9;
            if (!bounds.Contains(point) && _handle == 9) return;
            _dragCrop = _startCrop;
        }
        else if (!Composition && Comparison == 3 && e.ChangedButton == MouseButton.Left && Math.Abs(point.X - ActualWidth * _divider) < 12) _dividing = true;
        else if (e.ChangedButton is MouseButton.Left or MouseButton.Middle) { _panning = true; _startPan = _pan; }
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (_panning) _pan = _startPan + (point - _start);
        else if (_dividing) _divider = Math.Clamp(point.X / Math.Max(1, ActualWidth), 0.02, 0.98);
        else if (_dragCrop is not null)
        {
            var bounds = ImageBounds;
            _dragCrop = CropInteraction.Drag(_startCrop, _handle,
                new Point((_start.X - bounds.X) / bounds.Width, (_start.Y - bounds.Y) / bounds.Height),
                new Point((point.X - bounds.X) / bounds.Width, (point.Y - bounds.Y) / bounds.Height));
        }
        else
        {
            Cursor = PickGray ? Cursors.Cross : Composition ? Cursors.Cross : Cursors.Hand;
            return;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragCrop is { Width: >= 0.005, Height: >= 0.005 } crop) CropCommitted?.Invoke(new(crop.X, crop.Y, crop.Width, crop.Height));
        ClearGesture();
        ReleaseMouseCapture();
        InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); ClearGesture(); InvalidateVisual(); }
    private void ClearGesture() { _dragCrop = null; _panning = false; _dividing = false; _handle = -1; }
}
