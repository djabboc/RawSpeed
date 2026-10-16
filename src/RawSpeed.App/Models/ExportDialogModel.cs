using System.Globalization;
using System.Runtime.CompilerServices;
using RawSpeed.App.Infrastructure;
using RawSpeed.Imaging;

namespace RawSpeed.App.Models;

public sealed record ExportChoice<T>(T Value, string Label);

/// <summary>Editable dialog state; successful validation creates a detached immutable export snapshot.</summary>
public sealed class ExportDialogModel : ObservableObject
{
    private ExportFormat _format;
    private ExportResizeMode _resizeMode;
    private ExportColorProfile _colorProfile;
    private ExportMetadataMode _metadataMode;
    private ExportTiffCompression _tiffCompression = ExportTiffCompression.Lzw;
    private string _qualityText = "92", _dpiText = "300", _pngCompressionText = "2";
    private string _widthText, _heightText, _longEdgeText;
    private bool _preserveAspectRatio = true, _allowUpscale, _progressive, _optimize = true;
    private int _bitDepth = 8;

    public ExportDialogModel(int sourceWidth, int sourceHeight, string? sourcePath = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        SourcePath = sourcePath;
        _widthText = sourceWidth.ToString(CultureInfo.InvariantCulture);
        _heightText = sourceHeight.ToString(CultureInfo.InvariantCulture);
        _longEdgeText = Math.Max(sourceWidth, sourceHeight).ToString(CultureInfo.InvariantCulture);
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public string? SourcePath { get; }
    public string SourceDescription => $"编辑后图像  {SourceWidth:N0} × {SourceHeight:N0} px";
    public bool HasMetadataSource => !string.IsNullOrWhiteSpace(SourcePath);
    public static IReadOnlyList<ExportChoice<ExportFormat>> Formats { get; } =
        [new(ExportFormat.Jpeg, "JPEG · 适合分享"), new(ExportFormat.Png, "PNG · 无损 / 透明"), new(ExportFormat.Tiff, "TIFF · 后期交换")];
    public static IReadOnlyList<ExportChoice<ExportResizeMode>> ResizeModes { get; } =
        [new(ExportResizeMode.Original, "原始尺寸"), new(ExportResizeMode.LongEdge, "指定长边"), new(ExportResizeMode.Dimensions, "指定宽高")];
    public static IReadOnlyList<ExportChoice<ExportColorProfile>> ColorProfiles { get; } =
        [new(ExportColorProfile.SRgb, "sRGB · 屏幕和网络"), new(ExportColorProfile.AdobeRgb, "Adobe RGB (1998)")];
    public static IReadOnlyList<ExportChoice<ExportMetadataMode>> MetadataModes { get; } =
        [new(ExportMetadataMode.None, "不保留拍摄信息"), new(ExportMetadataMode.SafeExif, "常用拍摄参数 · 不含 GPS"), new(ExportMetadataMode.SafeExifWithGps, "常用拍摄参数 + GPS 位置")];
    public static IReadOnlyList<ExportChoice<ExportTiffCompression>> TiffCompressions { get; } =
        [new(ExportTiffCompression.Lzw, "LZW · 无损"), new(ExportTiffCompression.Zip, "ZIP · 无损"), new(ExportTiffCompression.None, "不压缩")];
    public static IReadOnlyList<int> BitDepths { get; } = [8, 16];

    public ExportFormat Format
    {
        get => _format;
        set
        {
            if (!SetProperty(ref _format, value)) return;
            if (value == ExportFormat.Jpeg) BitDepth = 8;
            OnPropertyChanged(nameof(IsJpeg)); OnPropertyChanged(nameof(IsPng));
            OnPropertyChanged(nameof(IsTiff)); OnPropertyChanged(nameof(SupportsHighBitDepth));
            Refresh();
        }
    }
    public ExportResizeMode ResizeMode
    {
        get => _resizeMode;
        set
        {
            if (!SetProperty(ref _resizeMode, value)) return;
            OnPropertyChanged(nameof(ShowsLongEdge)); OnPropertyChanged(nameof(ShowsDimensions));
            Refresh();
        }
    }
    public ExportColorProfile ColorProfile { get => _colorProfile; set => Change(ref _colorProfile, value); }
    public ExportMetadataMode MetadataMode { get => _metadataMode; set => Change(ref _metadataMode, value); }
    public ExportTiffCompression TiffCompression { get => _tiffCompression; set => Change(ref _tiffCompression, value); }
    public string QualityText { get => _qualityText; set => Change(ref _qualityText, value); }
    public string DpiText { get => _dpiText; set => Change(ref _dpiText, value); }
    public string PngCompressionText { get => _pngCompressionText; set => Change(ref _pngCompressionText, value); }
    public string WidthText { get => _widthText; set => Change(ref _widthText, value); }
    public string HeightText { get => _heightText; set => Change(ref _heightText, value); }
    public string LongEdgeText { get => _longEdgeText; set => Change(ref _longEdgeText, value); }
    public bool PreserveAspectRatio { get => _preserveAspectRatio; set => Change(ref _preserveAspectRatio, value); }
    public bool AllowUpscale { get => _allowUpscale; set => Change(ref _allowUpscale, value); }
    public bool JpegProgressive { get => _progressive; set => Change(ref _progressive, value); }
    public bool JpegOptimize { get => _optimize; set => Change(ref _optimize, value); }
    public int BitDepth { get => _bitDepth; set => Change(ref _bitDepth, value); }
    public bool IsJpeg => Format == ExportFormat.Jpeg;
    public bool IsPng => Format == ExportFormat.Png;
    public bool IsTiff => Format == ExportFormat.Tiff;
    public bool SupportsHighBitDepth => !IsJpeg;
    public bool ShowsLongEdge => ResizeMode == ExportResizeMode.LongEdge;
    public bool ShowsDimensions => ResizeMode == ExportResizeMode.Dimensions;
    public bool CanExport => TryBuildOptions(out _, out _);
    public string ValidationMessage => TryBuildOptions(out _, out string error) ? "" : error;
    public string OutputSummary
    {
        get
        {
            if (!TryBuildOptions(out var options, out _)) return "请检查导出设置";
            var (width, height) = options.ResolveDimensions(SourceWidth, SourceHeight);
            return $"{width:N0} × {height:N0} px  ·  {BitDepth} bit  ·  {DpiText} DPI";
        }
    }
    public string PrintSize
    {
        get
        {
            if (!TryBuildOptions(out var options, out _)) return "";
            var (width, height) = options.ResolveDimensions(SourceWidth, SourceHeight);
            return $"打印尺寸约 {width * 25.4 / options.Dpi:F1} × {height * 25.4 / options.Dpi:F1} mm。DPI 不改变像素数量。";
        }
    }

    public bool TryBuildOptions(out ExportOptions options, out string error)
    {
        options = new ExportOptions();
        error = "";
        if (!Parse(DpiText, 72, 1200, "分辨率 DPI", out int dpi, out error)) return false;
        int quality = 92, compression = 2, width = 0, height = 0, longEdge = 0;
        if (IsJpeg && !Parse(QualityText, 1, 100, "JPEG 品质", out quality, out error)) return false;
        if (IsPng && !Parse(PngCompressionText, 0, 9, "PNG 压缩级别", out compression, out error)) return false;
        if (ShowsLongEdge && !Parse(LongEdgeText, 1, 60000, "长边", out longEdge, out error)) return false;
        if (ShowsDimensions && (!Parse(WidthText, 1, 60000, "宽度", out width, out error)
            || !Parse(HeightText, 1, 60000, "高度", out height, out error))) return false;
        if (MetadataMode != ExportMetadataMode.None && !HasMetadataSource)
        { error = "没有源文件，无法保留拍摄信息。"; return false; }
        options = new ExportOptions
        {
            Format = Format, Quality = quality, ResizeMode = ResizeMode,
            Width = width, Height = height, LongEdge = longEdge,
            PreserveAspectRatio = PreserveAspectRatio, AllowUpscale = AllowUpscale,
            Dpi = dpi, ColorProfile = ColorProfile, BitDepth = BitDepth,
            MetadataMode = MetadataMode, SourcePath = SourcePath,
            JpegProgressive = JpegProgressive, JpegOptimize = JpegOptimize,
            PngCompressionLevel = compression, TiffCompression = TiffCompression,
        };
        try { options.ResolveDimensions(SourceWidth, SourceHeight); return true; }
        catch (ArgumentException exception) { error = exception.Message; return false; }
    }

    private static bool Parse(string text, int min, int max, string label, out int value, out string error)
    {
        error = "";
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max) return true;
        error = $"{label}需要 {min}–{max} 范围内的整数。";
        return false;
    }

    private void Change<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name)) Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(OutputSummary)); OnPropertyChanged(nameof(PrintSize));
    }
}
