using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RawSpeed.App.Infrastructure;
using RawSpeed.App.Models;
using RawSpeed.App.Services;
using RawSpeed.Core;
using RawSpeed.Imaging;
using RawSpeed.Metadata;

namespace RawSpeed.App.ViewModels;

public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly ImageFileService _files = new();
    private readonly MetadataService _metadata = new();
    private readonly Dictionary<string, EditRecipe> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EditRecipe> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditRecipe> _undo = [];
    private readonly List<EditRecipe> _redo = [];
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly Dictionary<string, DecodedImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Dictionary<string, CubeLut> _lutCache = new(StringComparer.OrdinalIgnoreCase);
    private DecodedImage? _denoiseSource;
    private byte[]? _denoisedPixels;
    private (double Legacy, double Luminance, double Color, double Detail) _denoiseKey;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _renderCancellation;
    private CancellationTokenSource? _exportCancellation;
    private EditRecipe _recipe = EditRecipe.Default;
    private CubeLut? _lut;
    private DecodedImage? _source;
    private PhotoItem? _selectedPhoto;
    private BitmapSource? _preview;
    private string _status = "打开照片，开始一次轻快的创作。";
    private string _imageInfo = "支持 JPEG · Nikon NEF · Olympus ORF";
    private string _metadataSearch = "";
    private string _error = "";
    private string _renderInfo = "";
    private bool _isLoading;
    private bool _isExporting;
    private bool _showBefore;
    private bool _cropMode;
    private bool _disposed;
    private long _loadVersion;
    private int[] _histogram = new int[768];
    private DateTime _lastEdit;
    private string? _lastEditProperty;

    public EditorViewModel(string? stateDirectory = null, string? presetsDirectory = null)
    {
        _ratings = new RatingStore(stateDirectory);
        _catalog = new LutCatalogService(presetsDirectory);
        InitializeWorkspace();
        MetadataView = CollectionViewSource.GetDefaultView(MetadataEntries);
        MetadataView.Filter = value => value is MetadataEntry entry &&
            ($"{entry.Group} {entry.Name} {entry.Value}").Contains(MetadataSearch, StringComparison.OrdinalIgnoreCase);
        OpenCommand = new RelayCommand(OpenFiles);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ExportCommand = new AsyncCommand(ChooseExportAsync, ReportError, () => HasImage && !IsLoading && !IsExporting);
        SaveCommand = new AsyncCommand(SaveRecipeAsync, ReportError, () => HasImage && !IsLoading);
        ImportLutCommand = new AsyncCommand(ImportLutAsync, ReportError, () => HasImage);
        ClearLutCommand = new RelayCommand(() => { ClearFilterSelection(); ChangeRecipe(_recipe with { LutPath = null }, "filter"); }, () => HasImage);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0 && !IsLoading);
        RedoCommand = new RelayCommand(Redo, () => _redo.Count > 0 && !IsLoading);
        ResetCommand = new RelayCommand(() => { ClearFilterSelection(); ChangeRecipe(EditRecipe.Default, "reset"); }, () => HasImage);
        RotateLeftCommand = new RelayCommand(() => ChangeSettings(Settings with { RotationQuarterTurns = (Settings.RotationQuarterTurns + 3) % 4, Crop = null }, "rotate"), () => HasImage);
        RotateRightCommand = new RelayCommand(() => ChangeSettings(Settings with { RotationQuarterTurns = (Settings.RotationQuarterTurns + 1) % 4, Crop = null }, "rotate"), () => HasImage);
        FlipCommand = new RelayCommand(() => ChangeSettings(Settings with { FlipHorizontal = !Settings.FlipHorizontal, Crop = null }, "flip"), () => HasImage);
        CropCommand = new RelayCommand(() => { SelectedToolTab = SelectedToolTab == 1 ? 0 : 1; ShowBefore = false; }, () => HasImage && !IsLoading);
        ClearCropCommand = new RelayCommand(() => ChangeSettings(Settings with { Crop = null }, "crop"), () => HasImage);
        CompareCommand = new RelayCommand(() => ShowBefore = !ShowBefore, () => HasImage);
        CancelCommand = new RelayCommand(() => { _loadCancellation?.Cancel(); _exportCancellation?.Cancel(); _detailCancellation?.Cancel(); Status = "已请求取消，正在结束当前步骤…"; }, () => IsLoading || IsExporting || IsDetailLoading);
        NaturalCommand = new RelayCommand(() => ApplyPreset(0), () => HasImage);
        FilmCommand = new RelayCommand(() => ApplyPreset(1), () => HasImage);
        MonoCommand = new RelayCommand(() => ApplyPreset(2), () => HasImage);
    }

    public ObservableCollection<PhotoItem> Photos { get; } = [];
    public ObservableCollection<MetadataEntry> MetadataEntries { get; } = [];
    public ICollectionView MetadataView { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ImportLutCommand { get; }
    public ICommand ClearLutCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand RotateLeftCommand { get; }
    public ICommand RotateRightCommand { get; }
    public ICommand FlipCommand { get; }
    public ICommand CropCommand { get; }
    public ICommand ClearCropCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NaturalCommand { get; }
    public ICommand FilmCommand { get; }
    public ICommand MonoCommand { get; }
    public EditSettings Settings => _recipe.Settings;
    public bool HasImage => _source is not null;
    public bool Empty => Preview is null;
    public bool HasUnsavedChanges => _sessions.Any(pair => !_saved.TryGetValue(pair.Key, out var recipe) || pair.Value != recipe);
    public string PhotoCount => $"{Photos.Count:00} 张照片";
    public string FileName => SelectedPhoto?.Name ?? "未选择照片";
    public string FilterName => _recipe.LutPath is null ? "无 LUT · 原始色彩" : Path.GetFileNameWithoutExtension(_recipe.LutPath);
    public string MetadataCount => $"{MetadataEntries.Count} 项元数据 · 可搜索、复制";
    public PhotoItem? SelectedPhoto
    {
        get => _selectedPhoto;
        set
        {
            if (_disposed) return;
            if (value is null) { if (SetProperty(ref _selectedPhoto, value)) ClearCurrentPhoto(); return; }
            if (SetProperty(ref _selectedPhoto, value) || (!HasImage && !IsLoading)) _ = LoadPhotoAsync(value);
        }
    }
    public BitmapSource? Preview { get => _preview; private set { if (SetProperty(ref _preview, value)) OnPropertyChanged(nameof(Empty)); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ImageInfo { get => _imageInfo; private set => SetProperty(ref _imageInfo, value); }
    public string RenderInfo { get => _renderInfo; private set => SetProperty(ref _renderInfo, value); }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string MetadataSearch { get => _metadataSearch; set { if (SetProperty(ref _metadataSearch, value)) MetadataView.Refresh(); } }
    public int[] Histogram { get => _histogram; private set => SetProperty(ref _histogram, value); }
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsExporting { get => _isExporting; private set { if (SetProperty(ref _isExporting, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool ShowBefore { get => _showBefore; set { if (SetProperty(ref _showBefore, value)) { OnPropertyChanged(nameof(CompareLabel)); ComparisonMode = value ? 1 : 0; } } }
    public string CompareLabel => ShowBefore ? "原图 · 点击返回" : "对比原图  B";
    public bool CropMode { get => _cropMode; set { if (SetProperty(ref _cropMode, value)) { NotifyGeometry(); ScheduleRender(); } } }

    public double Exposure { get => Settings.Exposure; set => ChangeSettings(Settings with { Exposure = value }); }
    public double Temperature { get => Settings.Temperature; set => ChangeSettings(Settings with { Temperature = value }); }
    public double Tint { get => Settings.Tint; set => ChangeSettings(Settings with { Tint = value }); }
    public double Contrast { get => Settings.Contrast; set => ChangeSettings(Settings with { Contrast = value }); }
    public double Highlights { get => Settings.Highlights; set => ChangeSettings(Settings with { Highlights = value }); }
    public double Shadows { get => Settings.Shadows; set => ChangeSettings(Settings with { Shadows = value }); }
    public double Saturation { get => Settings.Saturation; set => ChangeSettings(Settings with { Saturation = value }); }
    public double Hue { get => Settings.Hue; set => ChangeSettings(Settings with { Hue = value }); }
    public double Denoise { get => Settings.Denoise; set => ChangeSettings(Settings with { Denoise = value }); }
    public double CurveShadows { get => Settings.CurveShadows; set => ChangeSettings(Settings with { CurveShadows = value }); }
    public double CurveMidtones { get => Settings.CurveMidtones; set => ChangeSettings(Settings with { CurveMidtones = value }); }
    public double CurveHighlights { get => Settings.CurveHighlights; set => ChangeSettings(Settings with { CurveHighlights = value }); }
    public double Straighten { get => Settings.Straighten; set => ChangeSettings(Settings with { Straighten = value }); }
    public double LutStrength { get => _recipe.LutStrength * 100; set => ChangeRecipe(_recipe with { LutStrength = value / 100 }, nameof(LutStrength)); }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (_disposed) return;
        PhotoItem? first = null;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !IsSupported(path)) continue;
            var full = Path.GetFullPath(path);
            var item = Photos.FirstOrDefault(photo => photo.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
            if (item is null) { item = new PhotoItem(full) { Rating = _ratings.GetRating(full) }; Photos.Add(item); }
            first ??= item;
        }
        OnPropertyChanged(nameof(PhotoCount));
        if (first is not null) SelectedPhoto = first;
        else Error = "没有找到支持的文件。请打开 JPG、NEF 或 ORF。";
    }

    public static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".nef" or ".orf";

    private void OpenFiles()
    {
        var dialog = new OpenFileDialog { Title = "打开照片", Filter = "支持的照片|*.jpg;*.jpeg;*.nef;*.orf", Multiselect = true };
        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }

    private void OpenFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择照片文件夹" };
        if (dialog.ShowDialog() == true)
        {
            try { AddFiles(Directory.EnumerateFiles(dialog.FolderName).Where(IsSupported).OrderBy(Path.GetFileName)); }
            catch (Exception ex) { ReportError(ex); }
        }
    }

    private async Task LoadPhotoAsync(PhotoItem item)
    {
        _loadVersion++;
        _loadCancellation?.Cancel();
        _detailCancellation?.Cancel();
        _renderCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        _source = null;
        NotifyGeometry();
        _denoiseSource = null;
        _denoisedPixels = null;
        Preview = null;
        _undo.Clear(); _redo.Clear();
        _lastEditProperty = null;
        BeforePreview = null;
        ClearFilterSelection();
        _beforeSource = null;
        OnPropertyChanged(nameof(IsFullResolution));
        Error = "";
        IsLoading = true;
        OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(Empty));
        MetadataEntries.Clear(); OnPropertyChanged(nameof(MetadataCount));
        Status = $"正在读取 {item.Name}…";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            EditRecipe recipe;
            if (!_sessions.TryGetValue(item.Path, out recipe!))
            {
                try { recipe = await RecipeStore.LoadAsync(item.Path) ?? EditRecipe.Default; }
                catch (Exception ex) { token.ThrowIfCancellationRequested(); Error = "配方未载入：" + ex.Message; recipe = EditRecipe.Default; }
                token.ThrowIfCancellationRequested();
                _saved[item.Path] = recipe;
            }
            token.ThrowIfCancellationRequested();
            CubeLut? loadedLut = null;
            try { loadedLut = recipe.LutPath is null ? null : await LoadLutAsync(recipe.LutPath, token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                recipe = recipe with { LutPath = null };
                Error = "滤镜未载入：" + ex.Message;
            }
            token.ThrowIfCancellationRequested();
            _recipe = recipe; _lut = loadedLut;
            _sessions[item.Path] = recipe;
            NotifySettings();
            IReadOnlyList<MetadataEntry> metadata = [];
            try { metadata = await Task.Run(() => _metadata.Read(item.Path), token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { token.ThrowIfCancellationRequested(); Error = "部分元数据无法读取：" + ex.Message; }
            token.ThrowIfCancellationRequested();
            foreach (var entry in metadata) MetadataEntries.Add(entry);
            OnPropertyChanged(nameof(MetadataCount));
            var camera = metadata.FirstOrDefault(entry => entry.Name.StartsWith("Model (", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            if (!_cache.TryGetValue(item.Path, out var source))
            {
                var embedded = await _files.TryLoadEmbeddedPreviewAsync(item.Path, 1600, token);
                token.ThrowIfCancellationRequested();
                if (embedded is not null)
                {
                    var embeddedBitmap = await Task.Run(() => CreateBitmap(embedded.Width, embedded.Height, embedded.Pixels), token);
                    token.ThrowIfCancellationRequested();
                    Preview = embeddedBitmap;
                    ImageInfo = $"{camera} · 相机内嵌预览";
                    Status = $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms 首屏 · 正在显影 RAW…";
                }
                source = await _files.LoadPreviewAsync(item.Path, 1600, token);
                token.ThrowIfCancellationRequested();
                _cache[item.Path] = source;
                _cacheOrder.Enqueue(item.Path);
                while (_cacheOrder.Count > 3) _cache.Remove(_cacheOrder.Dequeue());
            }
            token.ThrowIfCancellationRequested();
            _source = source;
            NotifyGeometry();
            OnPropertyChanged(nameof(IsFullResolution));
            item.OriginalWidth = source.OriginalWidth;
            item.OriginalHeight = source.OriginalHeight;
            ImageInfo = $"{source.OriginalWidth:N0} × {source.OriginalHeight:N0}   ·   {camera}   ·   {item.Kind}";
            OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(Empty));
            var thumbnail = await Task.Run(() => CreateThumbnail(source), token);
            token.ThrowIfCancellationRequested();
            CacheThumbnail(item, thumbnail);
            ShowBefore = false;
            await RenderAsync(0);
            token.ThrowIfCancellationRequested();
            Status = $"已载入 · {stopwatch.Elapsed.TotalSeconds:F2} s · {source.DecodeMode}";
        }
        catch (OperationCanceledException)
        {
            if (_loadCancellation == cancellation)
            {
                _source = null; Preview = null;
                OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(Empty));
                Status = "已取消读取 · 可重新打开此照片";
            }
        }
        catch (Exception ex) { if (_loadCancellation == cancellation) ReportError(ex); }
        finally
        {
            if (_loadCancellation == cancellation) { IsLoading = false; _loadCancellation = null; }
            cancellation.Dispose();
        }
    }

    private void ChangeSettings(EditSettings settings, [System.Runtime.CompilerServices.CallerMemberName] string property = "") => ChangeRecipe(_recipe with { Settings = settings }, property);

    private void ChangeRecipe(EditRecipe recipe, string property)
    {
        if (_recipe == recipe || IsLoading) return;
        if (_lastEditProperty != property || (DateTime.UtcNow - _lastEdit).TotalMilliseconds > 550)
        {
            _undo.Add(_recipe);
            if (_undo.Count > 80) _undo.RemoveAt(0);
        }
        _lastEdit = DateTime.UtcNow; _lastEditProperty = property;
        _redo.Clear();
        ApplyRecipe(recipe);
    }

    private void ApplyRecipe(EditRecipe recipe)
    {
        try
        {
            if (recipe.LutPath != _recipe.LutPath) _lut = recipe.LutPath is null ? null : _lutCache[recipe.LutPath];
            _recipe = recipe;
            if (SelectedPhoto is not null) _sessions[SelectedPhoto.Path] = recipe;
            ShowBefore = false;
            NotifySettings();
            ScheduleRender();
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void NotifySettings()
    {
        NotifyWorkspaceSettings();
        foreach (var name in new[] { nameof(Exposure), nameof(Temperature), nameof(Tint), nameof(Contrast), nameof(Highlights), nameof(Shadows), nameof(Saturation), nameof(Hue), nameof(Denoise), nameof(CurveShadows), nameof(CurveMidtones), nameof(CurveHighlights), nameof(Straighten), nameof(LutStrength), nameof(FilterName), nameof(Settings) }) OnPropertyChanged(name);
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        ClearFilterSelection();
        _redo.Add(_recipe);
        var recipe = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _lastEditProperty = null; ApplyRecipe(recipe);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        ClearFilterSelection();
        _undo.Add(_recipe);
        var recipe = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        _lastEditProperty = null; ApplyRecipe(recipe);
    }

    public void ApplyCrop(NormalizedRect crop)
    {
        ChangeSettings(Settings with { Crop = crop }, "crop");
    }

    private void ApplyPreset(int preset)
    {
        var settings = preset switch
        {
            1 => Settings with { Temperature = 12, Contrast = 12, Saturation = -12, CurveShadows = 10, CurveMidtones = 3, CurveHighlights = -6 },
            2 => Settings with { Temperature = 0, Tint = 0, Saturation = -100, Contrast = 22, CurveShadows = -10, CurveMidtones = 5, CurveHighlights = 0 },
            _ => Settings with { Temperature = 0, Tint = 0, Contrast = 0, Saturation = 0, CurveShadows = 0, CurveMidtones = 0, CurveHighlights = 0 }
        };
        ChangeRecipe(_recipe with { Settings = settings, LutPath = null }, "preset");
    }

    private void ScheduleRender() { if (!_disposed) _ = RenderAsync(45); }

    private async Task RenderAsync(int delay)
    {
        _renderCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        var token = cancellation.Token;
        var source = _source;
        if (source is null) { if (_renderCancellation == cancellation) _renderCancellation = null; cancellation.Dispose(); return; }
        var settings = Settings;
        if (CropMode) settings = settings with { Crop = null };
        var lut = _lut;
        var beforeSettings = new EditSettings { Crop = settings.Crop, RotationQuarterTurns = settings.RotationQuarterTurns, FlipHorizontal = settings.FlipHorizontal, Straighten = settings.Straighten };
        var cachedBefore = ReferenceEquals(_beforeSource, source) && _beforeSettings == beforeSettings ? BeforePreview : null;
        var strength = _recipe.LutStrength;
        var denoiseKey = (settings.Denoise, settings.LuminanceDenoise, settings.ColorDenoise, settings.DenoiseDetail);
        bool needsDenoise = settings.Denoise > 0 || settings.LuminanceDenoise > 0 || settings.ColorDenoise > 0;
        var cachedDenoised = ReferenceEquals(_denoiseSource, source) && _denoiseKey == denoiseKey ? _denoisedPixels : null;
        try
        {
            if (delay > 0) await Task.Delay(delay, token);
            await _renderGate.WaitAsync(token);
            (RenderedImage Image, BitmapSource Bitmap, BitmapSource Before, byte[]? Denoised) result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                result = await Task.Run(() =>
                {
                    var denoised = needsDenoise ? cachedDenoised ?? NoiseReducer.Reduce(source.Pixels, source.Width, source.Height, settings, token) : null;
                    var colorSettings = settings with { Denoise = 0, LuminanceDenoise = 0, ColorDenoise = 0 };
                    var rendered = ImageProcessor.Render(denoised ?? source.Pixels, source.Width, source.Height, colorSettings, lut, strength, token);
                    token.ThrowIfCancellationRequested();
                    var original = cachedBefore;
                    if (original is null)
                    {
                        var unedited = ImageProcessor.Render(source.Pixels, source.Width, source.Height, beforeSettings, cancellationToken: token);
                        original = CreateBitmap(unedited.Width, unedited.Height, unedited.Pixels);
                    }
                    return (rendered, CreateBitmap(rendered.Width, rendered.Height, rendered.Pixels), original, denoised);
                }, token);
            }
            finally { _renderGate.Release(); }
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            Preview = result.Bitmap;
            BeforePreview = result.Before;
            _beforeSource = source;
            _beforeSettings = beforeSettings;
            _denoiseSource = result.Denoised is null ? null : source;
            _denoiseKey = denoiseKey;
            _denoisedPixels = result.Denoised;
            Histogram = result.Image.Histogram;
            RenderInfo = $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms   ·   {result.Image.Width} × {result.Image.Height} 预览";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ReportError(ex); }
        finally { if (_renderCancellation == cancellation) _renderCancellation = null; cancellation.Dispose(); }
    }

    public static BitmapSource CreateBitmap(int width, int height, byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3];
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private async Task ImportLutAsync()
    {
        var dialog = new OpenFileDialog { Title = "导入 3D LUT", Filter = "Cube 滤镜|*.cube" };
        if (dialog.ShowDialog() != true) return;
        ClearFilterSelection();
        var filterVersion = _filterVersion;
        var owner = SelectedPhoto;
        var loadVersion = _loadVersion;
        var lut = await LoadLutAsync(dialog.FileName, CancellationToken.None, refresh: true);
        if (SelectedPhoto != owner || loadVersion != _loadVersion || filterVersion != _filterVersion || _disposed || IsLoading) return;
        _undo.Add(_recipe); _redo.Clear();
        _lastEditProperty = null;
        _lut = lut;
        _recipe = _recipe with { LutPath = Path.GetFullPath(dialog.FileName) };
        var imported = new FilterPreset
        {
            Id = Path.GetFullPath(dialog.FileName), Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            Category = "已导入", Kind = FilterPresetKind.Cube, OriginalPath = dialog.FileName, LutPath = dialog.FileName,
            Notes = "用户导入的 sRGB 3D LUT"
        };
        if (Filters.All(filter => filter.Id != imported.Id)) Filters.Add(imported);
        _selectedFilter = imported;
        OnPropertyChanged(nameof(SelectedFilter));
        if (SelectedPhoto is not null) _sessions[SelectedPhoto.Path] = _recipe;
        NotifySettings(); ScheduleRender();
        Status = $"已导入滤镜 · {FilterName}";
    }

    private async Task<CubeLut> LoadLutAsync(string path, CancellationToken token, bool refresh = false)
    {
        if (!refresh && _lutCache.TryGetValue(path, out var cached)) return cached;
        var loaded = await Task.Run(() => CubeLut.Load(path), token);
        token.ThrowIfCancellationRequested();
        _lutCache[path] = loaded;
        return loaded;
    }

    private static BitmapSource CreateThumbnail(DecodedImage source)
    {
        var scale = Math.Min(1, 240d / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = ((int)(y / scale) * source.Width + (int)(x / scale)) * 4;
            Buffer.BlockCopy(source.Pixels, index, pixels, (y * width + x) * 4, 4);
        }
        return CreateBitmap(width, height, pixels);
    }

    private async Task SaveRecipeAsync()
    {
        if (SelectedPhoto is null) return;
        var path = SelectedPhoto.Path;
        var recipe = _recipe;
        await RecipeStore.SaveAsync(path, recipe);
        _saved[path] = recipe;
        Status = "编辑配方已保存 · 下次打开照片自动恢复";
    }

    private async Task ChooseExportAsync()
    {
        if (SelectedPhoto is null) return;
        var sourcePath = SelectedPhoto.Path;
        var recipe = _recipe;
        var lut = _lut;
        var geometry = ImageGeometry.Create(SelectedPhoto.OriginalWidth, SelectedPhoto.OriginalHeight, recipe.Settings);
        var dialog = new Views.ExportWindow(geometry.OutputWidth, geometry.OutputHeight, sourcePath) { Owner = System.Windows.Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var destination = Path.GetFullPath(dialog.DestinationPath);
        if (Photos.Any(photo => string.Equals(photo.Path, destination, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("请使用新的文件名，原始照片不可被覆盖。");
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        IsExporting = true; Error = "";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Status = $"正在原尺寸解码 · {Path.GetFileName(sourcePath)}";
            var full = await _files.LoadFullAsync(sourcePath, cancellation.Token);
            Status = "正在应用调整并导出…";
            var rendered = await Task.Run(() => ImageProcessor.Render(full.Pixels, full.Width, full.Height, recipe.Settings, lut, recipe.LutStrength, cancellation.Token), cancellation.Token);
            await _files.ExportAsync(destination, rendered.Width, rendered.Height, rendered.Pixels, dialog.Options, cancellation.Token);
            Status = $"导出完成 · {rendered.Width} × {rendered.Height} · {stopwatch.Elapsed.TotalSeconds:F2} s · {destination}";
        }
        catch (OperationCanceledException) { Status = "导出已取消"; }
        finally { IsExporting = false; _exportCancellation = null; }
    }

    public void ReportError(Exception exception) { Error = exception.Message; Status = "操作未完成 · 详见提示"; }

    public void Dispose()
    {
        _disposed = true;
        _loadCancellation?.Cancel(); _renderCancellation?.Cancel(); _exportCancellation?.Cancel();
        _detailCancellation?.Cancel();
        _thumbnailCancellation.Cancel();
        _cache.Clear();
        _denoisedPixels = null; _denoiseSource = null; _beforeSource = null;
    }
}
