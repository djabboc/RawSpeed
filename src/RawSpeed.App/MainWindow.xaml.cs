using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RawSpeed.App.Controls;
using RawSpeed.App.Models;
using RawSpeed.App.Services;
using RawSpeed.App.ViewModels;
using RawSpeed.App.Views;
using RawSpeed.Metadata;

namespace RawSpeed.App;

public partial class MainWindow : Window
{
    private readonly EditorViewModel _viewModel = new();
    private readonly PreferencesStore _preferencesStore = new();
    private EditorPreferences _preferences;
    private ShortcutService _shortcuts;

    public MainWindow()
    {
        InitializeComponent();
        _preferences = _preferencesStore.Load();
        _shortcuts = new ShortcutService(_preferences);
        DataContext = _viewModel;
        LibraryColumn.Width = new GridLength(_preferences.LibraryWidth);
        ToolsColumn.Width = new GridLength(_preferences.ToolsWidth);
        HistogramRow.Height = new GridLength(_preferences.HistogramHeight);
        _viewModel.ThumbnailSize = _preferences.ThumbnailSize;
        Viewport.CropCommitted += _viewModel.ApplyCrop;
        Viewport.GrayPicked += _viewModel.SampleGray;
        Viewport.WheelGesture += OnWheelGesture;
        Viewport.ZoomChanged += (_, _) => UpdateZoomLabel();
        Viewport.SizeChanged += (_, _) => UpdateZoomLabel();
        _viewModel.PropertyChanged += ViewModelChanged;
        Loaded += (_, _) =>
        {
            var paths = Environment.GetCommandLineArgs().Skip(1).Where(EditorViewModel.IsSupported).ToArray();
            if (paths.Length > 0) _viewModel.AddFiles(paths);
            Viewport.Focus();
        };
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.SelectedPhoto)) Viewport.Fit();
        if (e.PropertyName is nameof(EditorViewModel.Preview) or nameof(EditorViewModel.ComparisonMode) or nameof(EditorViewModel.LogicalWidth) or nameof(EditorViewModel.CropMode))
            Dispatcher.BeginInvoke(new Action(UpdateZoomLabel), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void UpdateZoomLabel()
    {
        _ = Viewport.ImageBounds; // Resolve fit zoom using the current viewport dimensions.
        if (!ZoomNumber.IsKeyboardFocused) ZoomNumber.Text = $"{Viewport.Zoom * 100:0.#}";
    }
    private void ZoomNumberLostFocus(object sender, RoutedEventArgs e) => ApplyZoomNumber();
    private void ZoomNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyZoomNumber(); e.Handled = true; }
        if (e.Key == Key.Escape) { ZoomNumber.Text = $"{Viewport.Zoom * 100:0.#}"; e.Handled = true; }
    }
    private void ApplyZoomNumber()
    {
        if (double.TryParse(ZoomNumber.Text, out double percent) && double.IsFinite(percent) && percent is >= 1 and <= 1600)
        {
            Viewport.ZoomBy(percent / 100 / Viewport.Zoom);
            RequestDetailIfNeeded();
        }
        ZoomNumber.Text = $"{Viewport.Zoom * 100:0.#}";
    }
    private async void PhotoCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        item.DataContextChanged -= PhotoCardDataContextChanged;
        item.DataContextChanged += PhotoCardDataContextChanged;
        if (item.DataContext is PhotoItem photo) await _viewModel.EnsureThumbnailAsync(photo);
    }
    private async void PhotoCardDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PhotoItem photo) await _viewModel.EnsureThumbnailAsync(photo);
    }
    private void FitImage(object sender, RoutedEventArgs e) => Viewport.Fit();
    private async void ActualSize(object sender, RoutedEventArgs e)
    {
        Viewport.ActualSize();
        await _viewModel.EnsureFullResolutionAsync();
    }
    private void ConfigureShortcuts(object sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutsWindow(_preferences) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _preferences = dialog.Preferences;
            _preferencesStore.Save(_preferences);
            _shortcuts = new ShortcutService(_preferences);
        }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }

    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        try
        {
            var files = paths.SelectMany(path => System.IO.Directory.Exists(path)
                ? System.IO.Directory.EnumerateFiles(path).Where(EditorViewModel.IsSupported) : [path]);
            _viewModel.AddFiles(files);
        }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }
    private void CropSquare(object sender, RoutedEventArgs e) => _viewModel.ApplyAspectCrop(1);
    private void CropThreeTwo(object sender, RoutedEventArgs e) => _viewModel.ApplyAspectCrop(1.5);
    private void CropWide(object sender, RoutedEventArgs e) => _viewModel.ApplyAspectCrop(16d / 9);
    private void ResetCurves(object sender, RoutedEventArgs e) => _viewModel.ResetCurves();
    private void ResetDenoise(object sender, RoutedEventArgs e) => _viewModel.ResetDenoise();
    private void ResetComposition(object sender, RoutedEventArgs e) => _viewModel.ResetComposition();
    private void CopyMetadata(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, _viewModel.MetadataView.Cast<MetadataEntry>().Select(entry => $"[{entry.Group}] {entry.Name}: {entry.Value}"))); }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Never turn a digit typed into a numeric editor into a photo rating, or steal its undo/delete.
        if (IsTextEntry(Keyboard.FocusedElement as DependencyObject) || Keyboard.FocusedElement is CurveCanvas) return;
        if (e.Key == Key.Escape)
        {
            _viewModel.PickGray = false;
            _viewModel.ComparisonMode = 0;
            if (_viewModel.CropMode) _viewModel.SelectedToolTab = 0;
            e.Handled = true;
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_shortcuts.MatchKey(key, Keyboard.Modifiers) is { } action) { ExecuteAction(action); e.Handled = true; }
    }
    private static bool IsTextEntry(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBox or PasswordBox or System.Windows.Controls.Primitives.TextBoxBase) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : null;
        }
        return false;
    }
    private void OnWheelGesture(int delta, ModifierKeys modifiers, Point anchor)
    {
        if (_shortcuts.MatchWheel(delta, modifiers) is { } action) ExecuteAction(action, anchor);
    }
    private void ExecuteAction(EditorAction action, Point? anchor = null)
    {
        ICommand? command = action switch
        {
            EditorAction.Open => _viewModel.OpenCommand, EditorAction.Save => _viewModel.SaveCommand,
            EditorAction.Export => _viewModel.ExportCommand, EditorAction.Undo => _viewModel.UndoCommand,
            EditorAction.Redo => _viewModel.RedoCommand, EditorAction.ToggleBefore => _viewModel.CompareCommand,
            EditorAction.Composition => _viewModel.CropCommand, _ => null
        };
        if (command is not null) { if (command.CanExecute(null)) command.Execute(null); return; }
        switch (action)
        {
            case EditorAction.Fit: Viewport.Fit(); break;
            case EditorAction.ActualSize: Viewport.ActualSize(); _ = _viewModel.EnsureFullResolutionAsync(); break;
            case EditorAction.ZoomIn: Viewport.ZoomBy(1.2, anchor); RequestDetailIfNeeded(); break;
            case EditorAction.ZoomOut: Viewport.ZoomBy(1 / 1.2, anchor); break;
            case EditorAction.CycleComparison: _viewModel.ComparisonMode = (_viewModel.ComparisonMode + 1) % 4; break;
            case EditorAction.Previous: _viewModel.NavigatePhoto(-1); break;
            case EditorAction.Next: _viewModel.NavigatePhoto(1); break;
            case EditorAction.Rating0: _viewModel.SetRating(0); break;
            case EditorAction.Rating1: _viewModel.SetRating(1); break;
            case EditorAction.Rating2: _viewModel.SetRating(2); break;
            case EditorAction.Rating3: _viewModel.SetRating(3); break;
            case EditorAction.Rating4: _viewModel.SetRating(4); break;
            case EditorAction.Rating5: _viewModel.SetRating(5); break;
        }
    }
    private void RequestDetailIfNeeded()
    {
        if (Viewport.Zoom >= 0.65) _ = _viewModel.EnsureFullResolutionAsync();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.HasUnsavedChanges && MessageBox.Show(this, "尚有未保存的编辑配方。关闭后这些调整将丢失，是否关闭？", "RawSpeed", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        try
        {
            _preferences.LibraryWidth = LibraryColumn.ActualWidth;
            _preferences.ToolsWidth = ToolsColumn.ActualWidth;
            _preferences.HistogramHeight = HistogramRow.ActualHeight;
            _preferences.ThumbnailSize = _viewModel.ThumbnailSize;
            _preferencesStore.Save(_preferences);
        }
        catch (Exception ex) { _viewModel.ReportError(ex); }
        _viewModel.Dispose();
    }
}
