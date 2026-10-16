using ImageMagick;

namespace RawSpeed.Imaging;

/// <summary>Loads JPEG and develops Nikon NEF / Olympus ORF files through ImageMagick and LibRaw.</summary>
/// <remarks>
/// Native image work runs off the caller's thread, with a process-wide gate to prevent simultaneous
/// full-resolution decodes from exhausting memory. Cancellation is honored while waiting and between
/// native operations; LibRaw itself cannot be interrupted safely. Export never replaces an existing file.
/// </remarks>
public sealed class ImageFileService
{
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private static readonly HashSet<string> SupportedInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".nef", ".orf",
    };

    /// <summary>Develops the source and returns an oriented preview fitting within <paramref name="maxEdge"/>.</summary>
    /// <remarks>RAW previews use real sensor development, never the camera's embedded JPEG.</remarks>
    public Task<DecodedImage> LoadPreviewAsync(string path, int maxEdge, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEdge, 1);
        return LoadAsync(ValidateInput(path), maxEdge, cancellationToken);
    }

    /// <summary>Develops and loads the entire oriented source image.</summary>
    public Task<DecodedImage> LoadFullAsync(string path, CancellationToken cancellationToken) =>
        LoadAsync(ValidateInput(path), null, cancellationToken);

    /// <summary>Returns the largest camera-rendered embedded RAW preview, or null when unavailable.</summary>
    /// <remarks>
    /// This optional first-paint optimization does not develop sensor data. Its color and framing can
    /// differ from LibRaw development. Replace it with LoadPreviewAsync before interactive editing.
    /// JPEG inputs return null. Decoder errors are deferred to the normal development path.
    /// </remarks>
    public async Task<DecodedImage?> TryLoadEmbeddedPreviewAsync(string path, int maxEdge, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEdge, 1);
        path = ValidateInput(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRaw(path))
            return null;

        await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run<DecodedImage?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var header = new MagickImage();
                    header.Ping(path);
                    var (originalWidth, originalHeight) = GetOrientedDimensions(header);
                    cancellationToken.ThrowIfCancellationRequested();

                    var settings = new MagickReadSettings();
                    settings.SetDefine(MagickFormat.Dng, "read-thumbnails", true);
                    using var thumbnails = new MagickImageCollection(path, settings);
                    var image = thumbnails.OrderByDescending(item => (long)item.Width * item.Height).FirstOrDefault();
                    if (image is null)
                        return null;
                    if (image.Orientation == Orientation.Undefined)
                        image.Orientation = header.Orientation;
                    return CreateDecodedImage(image, path, maxEdge, "Embedded JPEG · camera rendering", cancellationToken,
                        originalWidth, originalHeight);
                }
                catch (MagickException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            NativeGate.Release();
        }
    }

    /// <summary>Exports sRGB RGBA8 pixels to a new JPEG, PNG, or TIFF file without overwriting existing files.</summary>
    public Task ExportAsync(
        string path,
        int width,
        int height,
        byte[] rgba,
        int quality,
        CancellationToken cancellationToken) =>
        ExportAsync(path, width, height, rgba,
            new ExportOptions { Format = ExportOptions.FormatFromPath(path), Quality = quality }, cancellationToken);

    /// <summary>Exports an edited sRGB RGBA8 buffer using an immutable size, color, metadata and encoding specification.</summary>
    /// <remarks>No source or existing destination is overwritten. DPI changes print size only; resizing is controlled separately.</remarks>
    public async Task ExportAsync(string path, int width, int height, byte[] rgba, ExportOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgba.LongLength != checked((long)width * height * 4))
            throw new ArgumentException("The pixel buffer must contain exactly width × height × 4 RGBA bytes.", nameof(rgba));

        string destination = Path.GetFullPath(path);
        var outputSize = options.ResolveDimensions(width, height);
        if (ExportOptions.FormatFromPath(destination) != options.Format)
            throw new ArgumentException("The destination extension must match the selected format.", nameof(path));
        MagickFormat format = options.Format switch
        {
            ExportFormat.Jpeg => MagickFormat.Jpeg,
            ExportFormat.Png => MagickFormat.Png,
            ExportFormat.Tiff => MagickFormat.Tiff,
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };

        if (File.Exists(destination))
            throw new IOException("The export destination already exists. Choose a new file name to preserve existing images.");

        string? directory = Path.GetDirectoryName(destination);
        if (directory is null || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("The export destination folder does not exist.");
        if (options.MetadataMode != ExportMetadataMode.None)
        {
            if (string.IsNullOrWhiteSpace(options.SourcePath))
                throw new ArgumentException("A source file is required to preserve capture metadata.", nameof(options));
            ValidateInput(options.SourcePath);
        }

        await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var image = new MagickImage();
                image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
                image.ColorSpace = ColorSpace.sRGB;
                image.SetProfile(ColorProfiles.SRGB);
                // Keep resampling and ICC transforms at native Q16 precision; quantize only when encoding.
                image.Depth = 16;
                if (image.Width != outputSize.Width || image.Height != outputSize.Height)
                {
                    image.FilterType = FilterType.Lanczos;
                    image.Resize(new MagickGeometry((uint)outputSize.Width, (uint)outputSize.Height) { IgnoreAspectRatio = true });
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (options.ColorProfile == ExportColorProfile.AdobeRgb)
                    image.TransformColorSpace(ColorProfiles.AdobeRGB1998, ColorTransformMode.HighRes);
                image.Depth = (uint)options.BitDepth;
                image.Density = new Density(options.Dpi, options.Dpi, DensityUnit.PixelsPerInch);
                image.Orientation = Orientation.TopLeft;
                image.Format = format;
                if (format == MagickFormat.Jpeg)
                {
                    image.Quality = (uint)options.Quality;
                    image.Settings.Interlace = options.JpegProgressive ? Interlace.Plane : Interlace.NoInterlace;
                    image.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", options.JpegOptimize);
                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaAction.Remove);
                }
                else if (format == MagickFormat.Png)
                {
                    // PNG quality is a zlib/filter setting, not a visual quality control. Applying
                    // JPEG's 95 here selects level 9 and makes noisy full-size photographs very slow.
                    image.Settings.SetDefine(MagickFormat.Png, "compression-level", options.PngCompressionLevel);
                    // Force the requested container depth; otherwise the encoder optimizes replicated
                    // 16-bit RGBA8 values back to 8 bits (or indexed/gray output for simple fixtures).
                    image.Settings.SetDefine(MagickFormat.Png, "bit-depth", options.BitDepth);
                    image.Settings.SetDefine(MagickFormat.Png, "color-type", 6);
                }
                else if (format == MagickFormat.Tiff)
                    image.Settings.Compression = options.TiffCompression switch
                    {
                        ExportTiffCompression.None => CompressionMethod.NoCompression,
                        ExportTiffCompression.Lzw => CompressionMethod.LZW,
                        ExportTiffCompression.Zip => CompressionMethod.Zip,
                        _ => throw new ArgumentOutOfRangeException(nameof(options)),
                    };
                ExportMetadata.Apply(image, options);

                // A same-directory temporary file keeps publication atomic and prevents partial outputs.
                string temporary = Path.Combine(directory, $".rawspeed-{Guid.NewGuid():N}.tmp");
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    image.Write(temporary, format);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            NativeGate.Release();
        }
    }

    private static async Task<DecodedImage> LoadAsync(string path, int? maxEdge, CancellationToken cancellationToken)
    {
        await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool raw = IsRaw(path);
                var settings = new MagickReadSettings();
                int? originalWidth = null;
                int? originalHeight = null;
                if (raw)
                {
                    // These definitions are implemented by ImageMagick's built-in LibRaw DNG coder.
                    settings.SetDefine(MagickFormat.Dng, "use-camera-wb", true);
                    settings.SetDefine(MagickFormat.Dng, "output-color", "1");
                    settings.SetDefine(MagickFormat.Dng, "interpolation-quality", "3");
                }
                else if (maxEdge is int edge)
                {
                    using var header = new MagickImage();
                    header.Ping(path);
                    (originalWidth, originalHeight) = GetOrientedDimensions(header);
                    // libjpeg can skip high-frequency IDCT work and decode directly at 1/2, 1/4, or 1/8 size.
                    // A hint larger than a small source can request libjpeg's 2× upsampling mode.
                    if (Math.Max(originalWidth.Value, originalHeight.Value) > edge)
                        settings.SetDefine(MagickFormat.Jpeg, "size", $"{edge}x{edge}");
                }

                cancellationToken.ThrowIfCancellationRequested();
                using var image = new MagickImage(path, settings);
                return CreateDecodedImage(image, path, maxEdge,
                    raw ? "LibRaw · camera WB · sRGB" : "JPEG · sRGB", cancellationToken,
                    originalWidth, originalHeight);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            NativeGate.Release();
        }
    }

    private static (int Width, int Height) GetOrientedDimensions(IMagickImage image)
    {
        bool swapsAxes = image.Orientation is Orientation.LeftTop or Orientation.RightTop
            or Orientation.RightBottom or Orientation.LeftBottom;
        return (checked((int)(swapsAxes ? image.Height : image.Width)),
            checked((int)(swapsAxes ? image.Width : image.Height)));
    }

    private static DecodedImage CreateDecodedImage(
        IMagickImage<ushort> image,
        string path,
        int? maxEdge,
        string decodeMode,
        CancellationToken cancellationToken,
        int? originalWidth = null,
        int? originalHeight = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        image.AutoOrient();
        originalWidth ??= checked((int)image.Width);
        originalHeight ??= checked((int)image.Height);
        if (image.GetColorProfile() is not null)
            image.TransformColorSpace(ColorProfiles.SRGB);
        else if (image.ColorSpace != ColorSpace.sRGB)
            image.ColorSpace = ColorSpace.sRGB;

        cancellationToken.ThrowIfCancellationRequested();
        if (maxEdge is int edge && Math.Max(image.Width, image.Height) > edge)
        {
            var geometry = new MagickGeometry((uint)edge, (uint)edge) { Greater = true };
            image.FilterType = FilterType.Triangle;
            image.Resize(geometry);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var pixels = image.GetPixels();
        byte[] rgba = pixels.ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidDataException("The decoder returned an empty pixel buffer.");
        return new DecodedImage(
            checked((int)image.Width), checked((int)image.Height), rgba, path,
            originalWidth.Value, originalHeight.Value, decodeMode);
    }

    private static bool IsRaw(string path) =>
        Path.GetExtension(path).Equals(".nef", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".orf", StringComparison.OrdinalIgnoreCase);

    private static string ValidateInput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!SupportedInputs.Contains(Path.GetExtension(fullPath)))
            throw new NotSupportedException("Supported input formats are JPEG (.jpg/.jpeg), Nikon NEF, and Olympus ORF.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The image file does not exist.", fullPath);
        return fullPath;
    }
}
