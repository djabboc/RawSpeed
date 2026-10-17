using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using RawSpeed.App.Models;
using RawSpeed.App.Services;
using RawSpeed.Core;
using RawSpeed.Imaging;

namespace RawSpeed.App.ViewModels;

public sealed partial class EditorViewModel
{
    private readonly RatingStore _ratings;
    private readonly LutCatalogService _catalog;
    private readonly CancellationTokenSource _thumbnailCancellation = new();
    private CancellationTokenSource? _detailCancellation;
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly HashSet<string> _pendingThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<PhotoItem> _thumbnailOrder = new();
    private int _selectedToolTab;
    private int _curveChannel;
    private int _comparisonMode;
    private int _ratingFilter;
    private bool _groupRatings;
    private bool _pickGray;
    private bool _isDetailLoading;
    private double _thumbnailSize = 100;
    private BitmapSource? _beforePreview;
    private DecodedImage? _beforeSource;
    private EditSettings? _beforeSettings;
    private FilterPreset? _selectedFilter;
    private long _filterVersion;
    private string _filterSearch = "";

    public ICollectionView PhotosView { get; private set; } = null!;
    public ICollectionView FiltersView { get; private set; } = null!;
    public ObservableCollection<FilterPreset> Filters { get; } = [];
    public string[] CurveChannels { get; } = ["RGB", "R", "G", "B"];
    public string[] ComparisonModes { get; } = ["当前", "原图", "左右对比", "划线对比"];
    public string[] RatingFilters { get; } = ["全部照片", "未评级", "★ 及以上", "★★ 及以上", "★★★ 及以上", "★★★★ 及以上", "★★★★★"];
    public bool AutoAdvance { get; set; }
    public bool IsFullResolution => _source is { } source && source.Width == source.OriginalWidth && source.Height == source.OriginalHeight;
    public bool IsDetailLoading { get => _isDetailLoading; private set { if (SetProperty(ref _isDetailLoading, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public double ThumbnailSize { get => _thumbnailSize; set => SetProperty(ref _thumbnailSize, Math.Clamp(value, 45, 240)); }
    public BitmapSource? BeforePreview { get => _beforePreview; private set => SetProperty(ref _beforePreview, value); }
    public int SelectedToolTab
    {
        get => _selectedToolTab;
        set { if (SetProperty(ref _selectedToolTab, value)) { CropMode = value == 1; PickGray = false; } }
    }
    public bool PickGray { get => _pickGray; set => SetProperty(ref _pickGray, value); }
    public int ComparisonMode
    {
        get => _comparisonMode;
        set
        {
            if (!SetProperty(ref _comparisonMode, Math.Clamp(value, 0, 3))) return;
            _showBefore = value == 1;
            OnPropertyChanged(nameof(ShowBefore)); OnPropertyChanged(nameof(CompareLabel));
        }
    }
    public int CurveChannel { get => _curveChannel; set { if (SetProperty(ref _curveChannel, value)) OnPropertyChanged(nameof(ActiveCurve)); } }
    public ToneCurve? ActiveCurve
    {
        get => CurveChannel switch { 1 => Settings.RedCurve, 2 => Settings.GreenCurve, 3 => Settings.BlueCurve, _ => Settings.MasterCurve };
        set => ChangeSettings(CurveChannel switch
        {
            1 => Settings with { RedCurve = value }, 2 => Settings with { GreenCurve = value },
            3 => Settings with { BlueCurve = value }, _ => Settings with { MasterCurve = value }
        }, "curve-" + CurveChannel);
    }
    public double Midtones { get => Settings.Midtones; set => ChangeSettings(Settings with { Midtones = value }); }
    public double LuminanceDenoise { get => Settings.LuminanceDenoise; set => ChangeSettings(Settings with { LuminanceDenoise = value, Denoise = 0 }); }
    public double ColorDenoise { get => Settings.ColorDenoise; set => ChangeSettings(Settings with { ColorDenoise = value }); }
    public double DenoiseDetail { get => Settings.DenoiseDetail; set => ChangeSettings(Settings with { DenoiseDetail = value }); }
    public NormalizedRect? Crop => Settings.Crop;
    private NormalizedRect EffectiveCrop => Crop ?? new(0, 0, 1, 1);
    public double CropX { get => EffectiveCrop.X * 100; set => ApplyCrop(EffectiveCrop with { X = Math.Clamp(value / 100, 0, 1 - EffectiveCrop.Width) }); }
    public double CropY { get => EffectiveCrop.Y * 100; set => ApplyCrop(EffectiveCrop with { Y = Math.Clamp(value / 100, 0, 1 - EffectiveCrop.Height) }); }
    public double CropWidth { get => EffectiveCrop.Width * 100; set => ApplyCrop(EffectiveCrop with { Width = Math.Clamp(value / 100, Math.Min(0.005, 1 - EffectiveCrop.X), 1 - EffectiveCrop.X) }); }
    public double CropHeight { get => EffectiveCrop.Height * 100; set => ApplyCrop(EffectiveCrop with { Height = Math.Clamp(value / 100, Math.Min(0.005, 1 - EffectiveCrop.Y), 1 - EffectiveCrop.Y) }); }
    public double LogicalWidth => FullGeometry?.OutputWidth ?? 0;
    public double LogicalHeight => FullGeometry?.OutputHeight ?? 0;
    private GeometryTransform? FullGeometry => _source is null ? null : ImageGeometry.Create(_source.OriginalWidth, _source.OriginalHeight, CropMode ? Settings with { Crop = null } : Settings);

    public int RatingFilter
    {
        get => _ratingFilter;
        set { if (SetProperty(ref _ratingFilter, value)) { PhotosView.Refresh(); EnsureVisibleSelection(); } }
    }
    public bool GroupRatings
    {
        get => _groupRatings;
        set
        {
            if (!SetProperty(ref _groupRatings, value)) return;
            using (PhotosView.DeferRefresh())
            {
                PhotosView.GroupDescriptions.Clear();
                PhotosView.SortDescriptions.Clear();
                if (value)
                {
                    PhotosView.SortDescriptions.Add(new SortDescription(nameof(PhotoItem.Rating), ListSortDirection.Descending));
                    PhotosView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PhotoItem.RatingGroup)));
                }
            }
            EnsureVisibleSelection();
        }
    }
    public FilterPreset? SelectedFilter
    {
        get => _selectedFilter;
        set { if (SetProperty(ref _selectedFilter, value) && value is not null) _ = ApplyFilterAsync(value); }
    }
    public string FilterSearch { get => _filterSearch; set { if (SetProperty(ref _filterSearch, value)) FiltersView.Refresh(); } }
    public bool HasLut => _recipe.LutSource is not null || _recipe.LutPath is not null;
    private void ClearFilterSelection() { _filterVersion++; _selectedFilter = null; OnPropertyChanged(nameof(SelectedFilter)); }

    private void InitializeWorkspace()
    {
        PhotosView = new ListCollectionView(Photos);
        PhotosView.Filter = item => item is PhotoItem photo && (RatingFilter == 0 || (RatingFilter == 1 ? photo.Rating == 0 : photo.Rating >= RatingFilter - 1));
        FiltersView = new ListCollectionView(Filters);
        FiltersView.Filter = item => item is FilterPreset preset && preset.SearchText.Contains(FilterSearch, StringComparison.OrdinalIgnoreCase);
        try { foreach (var preset in _catalog.Scan()) Filters.Add(preset); }
        catch (Exception ex) { ReportError(ex); }
    }

    public void SetRating(int rating)
    {
        if (SelectedPhoto is not { } photo) return;
        try
        {
            _ratings.SetRating(photo.Path, rating);
            var visible = PhotosView.Cast<PhotoItem>().ToList();
            int index = visible.IndexOf(photo);
            photo.Rating = rating;
            PhotosView.Refresh();
            if (AutoAdvance && index >= 0 && index + 1 < visible.Count && PhotosView.Contains(visible[index + 1])) SelectedPhoto = visible[index + 1];
            else EnsureVisibleSelection();
            Status = $"{photo.Name} · {photo.RatingGroup}";
        }
        catch (Exception ex) { ReportError(ex); }
    }
    public void NavigatePhoto(int direction)
    {
        var visible = PhotosView.Cast<PhotoItem>().ToList();
        if (visible.Count == 0) return;
        int index = SelectedPhoto is null ? -1 : visible.IndexOf(SelectedPhoto);
        SelectedPhoto = visible[Math.Clamp(index + direction, 0, visible.Count - 1)];
    }
    private void EnsureVisibleSelection()
    {
        if (SelectedPhoto is null || !PhotosView.Contains(SelectedPhoto)) SelectedPhoto = PhotosView.Cast<PhotoItem>().FirstOrDefault();
    }
    private void ClearCurrentPhoto()
    {
        _loadVersion++;
        _loadCancellation?.Cancel(); _renderCancellation?.Cancel(); _detailCancellation?.Cancel();
        _source = null; _lut = null; _recipe = EditRecipe.Default;
        _beforeSource = null; _denoiseSource = null; _denoisedPixels = null;
        Preview = null; BeforePreview = null;
        _undo.Clear(); _redo.Clear(); _lastEditProperty = null;
        ClearFilterSelection();
        MetadataEntries.Clear(); Histogram = new int[768];
        IsLoading = false;
        NotifySettings();
        foreach (string property in new[] { nameof(FileName), nameof(HasImage), nameof(Empty), nameof(IsFullResolution), nameof(MetadataCount) }) OnPropertyChanged(property);
        Status = "当前筛选没有选中的照片";
    }

    public void SampleGray(double normalizedX, double normalizedY)
    {
        if (_source is not { } source || IsLoading) return;
        try
        {
            var geometry = ImageGeometry.Create(source.Width, source.Height, CropMode ? Settings with { Crop = null } : Settings);
            var point = geometry.MapOutputToSource(normalizedX * geometry.OutputWidth - 0.5, normalizedY * geometry.OutputHeight - 0.5);
            var balance = ColorSampling.EstimateNeutralBalance(source.Pixels, source.Width, source.Height,
                Math.Clamp((int)Math.Round(point.X), 0, source.Width - 1), Math.Clamp((int)Math.Round(point.Y), 0, source.Height - 1));
            ChangeSettings(Settings with { Temperature = balance.Temperature, Tint = balance.Tint }, "gray-balance");
            PickGray = false;
            Status = "已按灰点校正白平衡";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    public void ResetCurves() => ChangeSettings(Settings with { MasterCurve = null, RedCurve = null, GreenCurve = null, BlueCurve = null, CurveShadows = 0, CurveMidtones = 0, CurveHighlights = 0 }, "reset-curves");
    public void ResetDenoise() => ChangeSettings(Settings with { Denoise = 0, LuminanceDenoise = 0, ColorDenoise = 0, DenoiseDetail = 50 }, "reset-denoise");
    public void ResetComposition() => ChangeSettings(Settings with { Crop = null, Straighten = 0, RotationQuarterTurns = 0, FlipHorizontal = false }, "reset-composition");
    public void ApplyAspectCrop(double ratio)
    {
        if (_source is not { } source) return;
        double aspect = Settings.RotationQuarterTurns % 2 == 0 ? (double)source.OriginalWidth / source.OriginalHeight : (double)source.OriginalHeight / source.OriginalWidth;
        double width = Math.Min(1, ratio / aspect), height = Math.Min(1, aspect / ratio);
        ApplyCrop(new((1 - width) / 2, (1 - height) / 2, width, height));
    }

    public async Task EnsureFullResolutionAsync()
    {
        if (_disposed || IsFullResolution || IsLoading || IsDetailLoading || SelectedPhoto is not { } owner) return;
        var version = _loadVersion;
        using var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        IsDetailLoading = true;
        try
        {
            Status = "正在加载原尺寸细节…";
            var full = await _files.LoadFullAsync(owner.Path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || version != _loadVersion || owner != SelectedPhoto) return;
            _source = full; // Only the selected full-resolution image is retained; the three-entry cache stays reduced.
            OnPropertyChanged(nameof(IsFullResolution));
            NotifyGeometry();
            await RenderAsync(0);
            Status = "原尺寸细节已就绪 · 可在 100% 检查降噪";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _loadVersion) ReportError(ex); }
        finally { if (_detailCancellation == cancellation) { _detailCancellation = null; IsDetailLoading = false; } }
    }

    private async Task ApplyFilterAsync(FilterPreset preset)
    {
        long request = ++_filterVersion, ownerVersion = _loadVersion;
        if (!HasImage || IsLoading) return;
        try
        {
            if (preset.BuiltInKey is { } key) { ApplyPreset(key == "film" ? 1 : key == "mono" ? 2 : 0); return; }
            var prepared = await Task.Run(() => _catalog.PrepareLut(preset, _thumbnailCancellation.Token));
            if (_disposed || request != _filterVersion || ownerVersion != _loadVersion || IsLoading) return;
            _referencedLuts[prepared.Source] = prepared.Lut;
            if (_recipe.LutSource == prepared.Source)
            {
                // A source restored on disk may be selected again without changing saved intent.
                _lut = prepared.Lut; Error = "";
                NotifySettings(); ScheduleRender();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
            else ChangeRecipe(_recipe with { LutPath = null, LutSource = prepared.Source }, "filter");
            Status = $"已应用 {preset.Name}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == _filterVersion && ownerVersion == _loadVersion) ReportError(ex); }
    }

    public async Task EnsureThumbnailAsync(PhotoItem photo)
    {
        if (_disposed || photo.Thumbnail is not null || !_pendingThumbnails.Add(photo.Path)) return;
        try
        {
            await _thumbnailGate.WaitAsync(_thumbnailCancellation.Token);
            try
            {
                if (photo.Thumbnail is not null) return;
                var preview = await _files.TryLoadEmbeddedPreviewAsync(photo.Path, 240, _thumbnailCancellation.Token)
                    ?? await _files.LoadPreviewAsync(photo.Path, 240, _thumbnailCancellation.Token);
                var bitmap = await Task.Run(() => CreateThumbnail(preview), _thumbnailCancellation.Token);
                _thumbnailCancellation.Token.ThrowIfCancellationRequested();
                CacheThumbnail(photo, bitmap);
            }
            finally { _thumbnailGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* A broken file must not stop neighboring library thumbnails. */ }
        finally { _pendingThumbnails.Remove(photo.Path); }
    }

    private void CacheThumbnail(PhotoItem photo, BitmapSource bitmap)
    {
        _thumbnailOrder.Remove(photo);
        _thumbnailOrder.AddLast(photo);
        photo.Thumbnail = bitmap;
        while (_thumbnailOrder.Count > 128)
        {
            _thumbnailOrder.First!.Value.Thumbnail = null;
            _thumbnailOrder.RemoveFirst();
        }
    }

    private void NotifyGeometry()
    {
        OnPropertyChanged(nameof(LogicalWidth)); OnPropertyChanged(nameof(LogicalHeight));
    }
    private void NotifyWorkspaceSettings()
    {
        OnPropertyChanged(nameof(HasLut));
        foreach (string property in new[] { nameof(Midtones), nameof(LuminanceDenoise), nameof(ColorDenoise), nameof(DenoiseDetail), nameof(ActiveCurve), nameof(Crop), nameof(CropX), nameof(CropY), nameof(CropWidth), nameof(CropHeight) }) OnPropertyChanged(property);
        NotifyGeometry();
    }
}
