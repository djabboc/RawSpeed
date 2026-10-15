using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RawSpeed.App.ViewModels;
using RawSpeed.Core;
using RawSpeed.Metadata;

namespace RawSpeed.App;

public partial class MainWindow : Window
{
    private readonly EditorViewModel _viewModel = new();
    private bool _fit = true;
    private Point? _cropStart;
    private Rect _cropRect;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelChanged;
        Loaded += (_, _) =>
        {
            var paths = Environment.GetCommandLineArgs().Skip(1).Where(EditorViewModel.IsSupported);
            if (paths.Any()) _viewModel.AddFiles(paths);
            UpdateImageSize();
        };
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Preview))
        {
            UpdateImageSize();
            _cropRect = Rect.Empty; _cropStart = null;
            CropCanvas.ReleaseMouseCapture(); UpdateCropOverlay();
        }
        if (e.PropertyName == nameof(EditorViewModel.CropMode) && _viewModel.CropMode)
        {
            _fit = true; UpdateImageSize();
            _cropRect = Rect.Empty; UpdateCropOverlay();
        }
    }

    private void UpdateImageSize()
    {
        if (_viewModel.Preview is not { } image) return;
        if (_fit)
        {
            var scale = Math.Min(Math.Max(1, Viewport.ActualWidth - 24) / image.PixelWidth, Math.Max(1, Viewport.ActualHeight - 24) / image.PixelHeight);
            PhotoImage.Width = image.PixelWidth * scale;
            PhotoImage.Height = image.PixelHeight * scale;
        }
        else { PhotoImage.Width = image.PixelWidth; PhotoImage.Height = image.PixelHeight; }
    }

    private void ViewportSizeChanged(object sender, SizeChangedEventArgs e) { UpdateImageSize(); _cropRect = Rect.Empty; UpdateCropOverlay(); }
    private void FitImage(object sender, RoutedEventArgs e) { _fit = true; UpdateImageSize(); }
    private void ActualSize(object sender, RoutedEventArgs e) { _viewModel.CropMode = false; _fit = false; UpdateImageSize(); }
    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var files = new List<string>();
        try
        {
            foreach (var path in paths)
                if (System.IO.Directory.Exists(path)) files.AddRange(System.IO.Directory.EnumerateFiles(path).Where(EditorViewModel.IsSupported));
                else files.Add(path);
            _viewModel.AddFiles(files);
        }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }

    private Rect DisplayImageRect()
    {
        var origin = PhotoImage.TranslatePoint(new Point(0, 0), CropCanvas);
        return new Rect(origin, new Size(PhotoImage.ActualWidth, PhotoImage.ActualHeight));
    }

    private void CropMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(CropCanvas);
        if (!DisplayImageRect().Contains(point)) return;
        _cropStart = point; _cropRect = new Rect(point, point); CropCanvas.CaptureMouse(); e.Handled = true;
    }

    private void CropMouseMove(object sender, MouseEventArgs e)
    {
        if (_cropStart is not { } start) return;
        var bounds = DisplayImageRect();
        var point = e.GetPosition(CropCanvas);
        point = new Point(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom));
        _cropRect = new Rect(start, point); UpdateCropOverlay();
    }

    private void CropMouseUp(object sender, MouseButtonEventArgs e) { _cropStart = null; CropCanvas.ReleaseMouseCapture(); }

    private void UpdateCropOverlay()
    {
        if (CropRectangle is null) return;
        CropRectangle.Visibility = _cropRect.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        CropGrid.Visibility = CropRectangle.Visibility;
        if (_cropRect.IsEmpty) return;
        Canvas.SetLeft(CropRectangle, _cropRect.Left); Canvas.SetTop(CropRectangle, _cropRect.Top);
        CropRectangle.Width = _cropRect.Width; CropRectangle.Height = _cropRect.Height;
        var geometry = new GeometryGroup();
        for (var i = 1; i <= 2; i++)
        {
            var x = _cropRect.Left + _cropRect.Width * i / 3;
            var y = _cropRect.Top + _cropRect.Height * i / 3;
            geometry.Children.Add(new LineGeometry(new Point(x, _cropRect.Top), new Point(x, _cropRect.Bottom)));
            geometry.Children.Add(new LineGeometry(new Point(_cropRect.Left, y), new Point(_cropRect.Right, y)));
        }
        CropGrid.Data = geometry;
    }

    private void ApplyCrop(object sender, RoutedEventArgs e)
    {
        var bounds = DisplayImageRect();
        if (_cropRect.IsEmpty || _cropRect.Width < 8 || _cropRect.Height < 8 || bounds.Width == 0 || bounds.Height == 0) return;
        _viewModel.ApplyCrop(new NormalizedRect((_cropRect.X - bounds.X) / bounds.Width, (_cropRect.Y - bounds.Y) / bounds.Height, _cropRect.Width / bounds.Width, _cropRect.Height / bounds.Height));
    }

    private void CancelCrop(object sender, RoutedEventArgs e) => _viewModel.CropMode = false;
    private void CropSquare(object sender, RoutedEventArgs e) => ApplyAspectCrop(1);
    private void CropThreeTwo(object sender, RoutedEventArgs e) => ApplyAspectCrop(1.5);
    private void ApplyAspectCrop(double ratio)
    {
        if (_viewModel.Preview is null) return;
        // Use the uncropped oriented canvas dimensions, not the previous cropped preview.
        var turns = _viewModel.Settings.RotationQuarterTurns % 2;
        var original = _viewModel.SelectedPhoto;
        if (original is null || original.OriginalHeight == 0 || original.OriginalWidth == 0) return;
        var aspect = turns == 0 ? original.OriginalWidth / (double)original.OriginalHeight : original.OriginalHeight / (double)original.OriginalWidth;
        var w = Math.Min(1, ratio / aspect); var h = Math.Min(1, aspect / ratio);
        _viewModel.ApplyCrop(new NormalizedRect((1 - w) / 2, (1 - h) / 2, w, h));
    }

    private void CopyMetadata(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, _viewModel.MetadataView.Cast<MetadataEntry>().Select(entry => $"[{entry.Group}] {entry.Name}: {entry.Value}"))); }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        ICommand? command = (control, e.Key) switch
        {
            (true, Key.O) => _viewModel.OpenCommand, (true, Key.E) => _viewModel.ExportCommand,
            (true, Key.S) => _viewModel.SaveCommand, (true, Key.Z) => _viewModel.UndoCommand,
            (true, Key.Y) => _viewModel.RedoCommand, _ => null
        };
        if (!control && Keyboard.FocusedElement is not TextBox)
        {
            command = e.Key switch { Key.B => _viewModel.CompareCommand, Key.C => _viewModel.CropCommand, _ => command };
            if (e.Key == Key.Escape) { _viewModel.CropMode = false; _viewModel.ShowBefore = false; }
        }
        if (command?.CanExecute(null) == true) { command.Execute(null); e.Handled = true; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.HasUnsavedChanges && MessageBox.Show(this, "尚有未保存的编辑配方。关闭后这些调整将丢失，是否关闭？", "RawSpeed", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        _viewModel.Dispose();
    }
}
