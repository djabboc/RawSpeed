namespace RawSpeed.Imaging;

public enum ExportFormat { Jpeg, Png, Tiff }
public enum ExportResizeMode { Original, LongEdge, Dimensions }
public enum ExportColorProfile { SRgb, AdobeRgb }
public enum ExportMetadataMode { None, SafeExif, SafeExifWithGps }
public enum ExportTiffCompression { None, Lzw, Zip }

/// <summary>An immutable export specification for an already edited, oriented sRGB RGBA8 buffer.</summary>
/// <remarks>16-bit output stores the existing 8-bit editing result in a 16-bit container; it cannot recover RAW precision.</remarks>
public sealed record ExportOptions
{
    public ExportFormat Format { get; init; } = ExportFormat.Jpeg;
    public int Quality { get; init; } = 92;
    public ExportResizeMode ResizeMode { get; init; } = ExportResizeMode.Original;
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? LongEdge { get; init; }
    public bool PreserveAspectRatio { get; init; } = true;
    public bool AllowUpscale { get; init; }
    public int Dpi { get; init; } = 300;
    public ExportColorProfile ColorProfile { get; init; } = ExportColorProfile.SRgb;
    public int BitDepth { get; init; } = 8;
    public ExportMetadataMode MetadataMode { get; init; } = ExportMetadataMode.None;
    public string? SourcePath { get; init; }
    public bool JpegProgressive { get; init; }
    public bool JpegOptimize { get; init; } = true;
    public int PngCompressionLevel { get; init; } = 2;
    public ExportTiffCompression TiffCompression { get; init; } = ExportTiffCompression.Lzw;

    public string Extension => Format switch
    {
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Png => ".png",
        ExportFormat.Tiff => ".tif",
        _ => throw new ArgumentOutOfRangeException(nameof(Format)),
    };

    public static ExportFormat FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ExportFormat.Jpeg,
        ".png" => ExportFormat.Png,
        ".tif" or ".tiff" => ExportFormat.Tiff,
        _ => throw new NotSupportedException("Export supports JPEG (.jpg/.jpeg), PNG (.png), and TIFF (.tif/.tiff)."),
    };

    /// <summary>Validates the specification and calculates the exact output dimensions, including the no-upscale limit.</summary>
    public (int Width, int Height) ResolveDimensions(int sourceWidth, int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        if (!Enum.IsDefined(Format) || !Enum.IsDefined(ResizeMode) || !Enum.IsDefined(ColorProfile)
            || !Enum.IsDefined(MetadataMode) || !Enum.IsDefined(TiffCompression))
            throw new ArgumentException("An export option has an unsupported value.");
        if (Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Quality), "JPEG quality must be 1–100.");
        if (Dpi is < 72 or > 1200) throw new ArgumentOutOfRangeException(nameof(Dpi), "Resolution must be 72–1200 DPI.");
        if (BitDepth is not (8 or 16) || (Format == ExportFormat.Jpeg && BitDepth != 8))
            throw new ArgumentOutOfRangeException(nameof(BitDepth), "JPEG supports 8-bit output; PNG and TIFF support 8 or 16 bits.");
        if (PngCompressionLevel is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(PngCompressionLevel), "PNG compression must be 0–9.");
        if (Width < 0 || Height < 0 || LongEdge < 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Output dimensions cannot be negative.");

        double width = sourceWidth, height = sourceHeight;
        if (ResizeMode == ExportResizeMode.LongEdge)
        {
            if (LongEdge is not > 0) throw new ArgumentException("Enter a positive long-edge size.");
            double scale = (double)LongEdge.Value / Math.Max(sourceWidth, sourceHeight);
            width *= scale;
            height *= scale;
        }
        else if (ResizeMode == ExportResizeMode.Dimensions)
        {
            if (Width is not > 0 && Height is not > 0) throw new ArgumentException("Enter a positive width or height.");
            if (PreserveAspectRatio)
            {
                double scale = Math.Min(Width is > 0 ? (double)Width.Value / sourceWidth : double.PositiveInfinity,
                    Height is > 0 ? (double)Height.Value / sourceHeight : double.PositiveInfinity);
                width *= scale;
                height *= scale;
            }
            else
            {
                width = Width is > 0 ? Width.Value : sourceWidth;
                height = Height is > 0 ? Height.Value : sourceHeight;
            }
        }

        if (!AllowUpscale)
        {
            double limit = Math.Min(1, Math.Min(sourceWidth / width, sourceHeight / height));
            width *= limit;
            height *= limit;
        }
        if (width > 60000 || height > 60000 || width * height > 100_000_000)
            throw new ArgumentException("Export is limited to 60,000 pixels per side and 100 megapixels.");
        return (Math.Max(1, (int)Math.Round(width, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(height, MidpointRounding.AwayFromZero)));
    }
}
