namespace RawSpeed.Core;

/// <summary>
/// Stateless, thread-safe CPU renderer for tightly packed RGBA8 images. The input is never modified.
/// Processing order: source-coordinate luma/chroma denoising, geometry, display-referred color, then LUT.
/// </summary>
public static class ImageProcessor
{
    public static RenderedImage Render(byte[] rgba, int width, int height, EditSettings settings,
        CubeLut? lut = null, double lutStrength = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(settings);
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4 || rgba.Length != (long)width * height * 4)
            throw new ArgumentException("Dimensions must be positive and match the tightly packed RGBA8 buffer.", nameof(rgba));
        ValidateSettings(settings);
        ValidateRange(lutStrength, 0, 1, nameof(lutStrength));
        cancellationToken.ThrowIfCancellationRequested();
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(16, Environment.ProcessorCount - 1))
        };
        double luminance = Math.Max(settings.Denoise, settings.LuminanceDenoise);
        double chroma = Math.Max(settings.Denoise, settings.ColorDenoise);
        byte[] working = rgba;
        if (luminance > 0 || chroma > 0)
        {
            working = (byte[])rgba.Clone();
            NoiseReducer.ApplyInPlace(working, width, height, luminance, chroma, settings.DenoiseDetail, options);
        }
        byte[] pixels;
        int outputWidth, outputHeight;
        if (!ReferenceEquals(working, rgba) && settings.RotationQuarterTurns % 4 == 0 && settings.Straighten == 0 && !settings.FlipHorizontal && settings.Crop is null)
        {
            pixels = working; outputWidth = width; outputHeight = height;
        }
        else pixels = ApplyGeometry(working, width, height, settings, options, out outputWidth, out outputHeight);
        ColorPipeline.Apply(pixels, outputWidth, outputHeight, settings, lut, (float)lutStrength, options);
        int[] histogram = BuildHistogram(pixels, outputWidth, outputHeight, options);
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderedImage(outputWidth, outputHeight, pixels, histogram);
    }

    /// <summary>Evaluates legacy curve controls followed by the master curve, without other color controls.</summary>
    public static double EvaluateCurve(double input, EditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateRange(input, 0, 1, nameof(input));
        ValidateRange(settings.CurveShadows, -100, 100, nameof(settings.CurveShadows));
        ValidateRange(settings.CurveMidtones, -100, 100, nameof(settings.CurveMidtones));
        ValidateRange(settings.CurveHighlights, -100, 100, nameof(settings.CurveHighlights));
        Span<double> nodes = stackalloc double[5]
        {
            0, Math.Clamp(0.25 + settings.CurveShadows * 0.002, 0, 1),
            Math.Clamp(0.5 + settings.CurveMidtones * 0.002, 0, 1),
            Math.Clamp(0.75 + settings.CurveHighlights * 0.002, 0, 1), 1
        };
        double value = EvaluateCurve(nodes, input);
        return settings.MasterCurve?.Evaluate(value) ?? value;
    }

    public static double EvaluateCurve(double input, ToneCurve? curve)
    {
        ValidateRange(input, 0, 1, nameof(input));
        return curve?.Evaluate(input) ?? input;
    }

    /// <summary>Validates adjustment ranges and crop geometry without allocating or rendering pixels.</summary>
    public static void ValidateSettings(EditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateRange(settings.Exposure, -10, 10, nameof(settings.Exposure));
        ValidateRange(settings.Temperature, -100, 100, nameof(settings.Temperature));
        ValidateRange(settings.Tint, -100, 100, nameof(settings.Tint));
        ValidateRange(settings.Contrast, -100, 100, nameof(settings.Contrast));
        ValidateRange(settings.Highlights, -100, 100, nameof(settings.Highlights));
        ValidateRange(settings.Shadows, -100, 100, nameof(settings.Shadows));
        ValidateRange(settings.Midtones, -100, 100, nameof(settings.Midtones));
        ValidateRange(settings.Saturation, -100, 100, nameof(settings.Saturation));
        ValidateRange(settings.Hue, -360, 360, nameof(settings.Hue));
        ValidateRange(settings.Denoise, 0, 100, nameof(settings.Denoise));
        ValidateRange(settings.LuminanceDenoise, 0, 100, nameof(settings.LuminanceDenoise));
        ValidateRange(settings.ColorDenoise, 0, 100, nameof(settings.ColorDenoise));
        ValidateRange(settings.DenoiseDetail, 0, 100, nameof(settings.DenoiseDetail));
        ValidateRange(settings.CurveShadows, -100, 100, nameof(settings.CurveShadows));
        ValidateRange(settings.CurveMidtones, -100, 100, nameof(settings.CurveMidtones));
        ValidateRange(settings.CurveHighlights, -100, 100, nameof(settings.CurveHighlights));
        ValidateRange(settings.Straighten, -180, 180, nameof(settings.Straighten));
        if (settings.Crop is { } crop)
        {
            ValidateRange(crop.X, 0, 1, nameof(crop.X));
            ValidateRange(crop.Y, 0, 1, nameof(crop.Y));
            ValidateRange(crop.Width, double.Epsilon, 1, nameof(crop.Width));
            ValidateRange(crop.Height, double.Epsilon, 1, nameof(crop.Height));
            if (crop.X + crop.Width > 1 + 1e-10 || crop.Y + crop.Height > 1 + 1e-10)
                throw new ArgumentOutOfRangeException(nameof(settings.Crop), "Crop must remain inside the image.");
        }
    }

    private static void ValidateRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"Value must be finite and between {minimum} and {maximum}.");
    }

    private static byte[] ApplyGeometry(byte[] source, int width, int height, EditSettings settings,
        ParallelOptions options, out int outputWidth, out int outputHeight)
    {
        var geometry = new GeometryTransform(width, height, settings);
        int targetWidth = outputWidth = geometry.OutputWidth, targetHeight = outputHeight = geometry.OutputHeight;
        var target = GC.AllocateUninitializedArray<byte>(checked(targetWidth * targetHeight * 4));
        if (geometry.CanCopyRows)
        {
            Parallel.For(0, targetHeight, options, y => Buffer.BlockCopy(source,
                ((y + geometry.CropY) * width + geometry.CropX) * 4, target, y * targetWidth * 4, targetWidth * 4));
            return target;
        }
        Parallel.For(0, targetHeight, options, y =>
        {
            for (int x = 0; x < targetWidth; x++)
            {
                var point = geometry.MapOutputToSourceUnchecked(x, y);
                int destination = (y * targetWidth + x) * 4;
                if (geometry.RequiresInterpolation) SampleBilinear(source, width, height, point.X, point.Y, target, destination);
                else
                {
                    int input = ((int)point.Y * width + (int)point.X) * 4;
                    target[destination] = source[input]; target[destination + 1] = source[input + 1];
                    target[destination + 2] = source[input + 2]; target[destination + 3] = source[input + 3];
                }
            }
        });
        return target;
    }

    private static void SampleBilinear(byte[] source, int width, int height, double x, double y, byte[] target, int destination)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        double fx = x - x0, fy = y - y0;
        int a = (y0 * width + x0) * 4, b = (y0 * width + x1) * 4;
        int c = (y1 * width + x0) * 4, d = (y1 * width + x1) * 4;
        for (int channel = 0; channel < 4; channel++)
        {
            double top = source[a + channel] + (source[b + channel] - source[a + channel]) * fx;
            double bottom = source[c + channel] + (source[d + channel] - source[c + channel]) * fx;
            target[destination + channel] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), 0, 255);
        }
    }

    private static double EvaluateCurve(ReadOnlySpan<double> nodes, double value)
    {
        double coordinate = value * 4;
        int segment = Math.Min((int)coordinate, 3);
        double t = coordinate - segment, t2 = t * t, t3 = t2 * t;
        double a = nodes[segment], b = nodes[segment + 1];
        double tangentA = segment == 0 ? b - a : (b - nodes[segment - 1]) * 0.5;
        double tangentB = segment == 3 ? b - a : (nodes[segment + 2] - a) * 0.5;
        return Math.Clamp((2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * tangentA +
            (-2 * t3 + 3 * t2) * b + (t3 - t2) * tangentB, 0, 1);
    }

    private static int[] BuildHistogram(byte[] pixels, int width, int height, ParallelOptions options)
    {
        var histogram = new int[768];
        Parallel.For(0, height, options, () => new int[768], (y, _, local) =>
        {
            int end = (y + 1) * width * 4;
            for (int index = y * width * 4; index < end; index += 4)
            {
                local[pixels[index]]++;
                local[256 + pixels[index + 1]]++;
                local[512 + pixels[index + 2]]++;
            }
            return local;
        }, local =>
        {
            for (int bin = 0; bin < histogram.Length; bin++) Interlocked.Add(ref histogram[bin], local[bin]);
        });
        return histogram;
    }

}
