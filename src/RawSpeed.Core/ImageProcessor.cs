using System.Buffers;
using System.Numerics;

namespace RawSpeed.Core;

/// <summary>
/// Stateless, thread-safe CPU renderer for tightly packed RGBA8 images. The input is never modified.
/// Processing order: geometry, display-referred color, LUT, then separable bilateral denoising.
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
        byte[] pixels = ApplyGeometry(rgba, width, height, settings, options, out int outputWidth, out int outputHeight);
        ApplyColor(pixels, outputWidth, outputHeight, settings, lut, (float)lutStrength, options);
        if (settings.Denoise > 0) Denoise(pixels, outputWidth, outputHeight, (float)settings.Denoise, options);
        int[] histogram = BuildHistogram(pixels, outputWidth, outputHeight, options);
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderedImage(outputWidth, outputHeight, pixels, histogram);
    }

    /// <summary>Evaluates only the three curve controls, for drawing an accurate curve preview.</summary>
    public static double EvaluateCurve(double input, EditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateRange(input, 0, 1, nameof(input));
        ValidateRange(settings.CurveShadows, -100, 100, nameof(settings.CurveShadows));
        ValidateRange(settings.CurveMidtones, -100, 100, nameof(settings.CurveMidtones));
        ValidateRange(settings.CurveHighlights, -100, 100, nameof(settings.CurveHighlights));
        return EvaluateCurve(CreateCurveNodes(settings), input);
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
        ValidateRange(settings.Saturation, -100, 100, nameof(settings.Saturation));
        ValidateRange(settings.Hue, -360, 360, nameof(settings.Hue));
        ValidateRange(settings.Denoise, 0, 100, nameof(settings.Denoise));
        ValidateRange(settings.CurveShadows, -100, 100, nameof(settings.CurveShadows));
        ValidateRange(settings.CurveMidtones, -100, 100, nameof(settings.CurveMidtones));
        ValidateRange(settings.CurveHighlights, -100, 100, nameof(settings.CurveHighlights));
        ValidateRange(settings.Straighten, -45, 45, nameof(settings.Straighten));
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
        int turns = (settings.RotationQuarterTurns % 4 + 4) % 4;
        int orientedWidth = (turns & 1) == 0 ? width : height;
        int orientedHeight = (turns & 1) == 0 ? height : width;
        var crop = settings.Crop;
        int cropX = crop is null ? 0 : Math.Min(orientedWidth - 1, (int)Math.Floor(crop.X * orientedWidth));
        int cropY = crop is null ? 0 : Math.Min(orientedHeight - 1, (int)Math.Floor(crop.Y * orientedHeight));
        int cropEndX = crop is null ? orientedWidth : Math.Clamp((int)Math.Ceiling((crop.X + crop.Width) * orientedWidth), cropX + 1, orientedWidth);
        int cropEndY = crop is null ? orientedHeight : Math.Clamp((int)Math.Ceiling((crop.Y + crop.Height) * orientedHeight), cropY + 1, orientedHeight);
        int targetWidth = outputWidth = cropEndX - cropX;
        int targetHeight = outputHeight = cropEndY - cropY;
        var target = GC.AllocateUninitializedArray<byte>(checked(targetWidth * targetHeight * 4));
        if (turns == 0 && !settings.FlipHorizontal && settings.Straighten == 0)
        {
            Parallel.For(0, targetHeight, options, y =>
                Buffer.BlockCopy(source, ((y + cropY) * width + cropX) * 4, target, y * targetWidth * 4, targetWidth * 4));
            return target;
        }

        double radians = settings.Straighten * Math.PI / 180;
        double cosine = Math.Cos(radians), sine = Math.Sin(radians);
        // Fit the inverse-rotated output rectangle inside the source to avoid empty corners.
        double zoom = Math.Max(Math.Abs(cosine) + Math.Abs(sine) * orientedHeight / orientedWidth,
            Math.Abs(cosine) + Math.Abs(sine) * orientedWidth / orientedHeight);
        double centerX = (orientedWidth - 1) * 0.5, centerY = (orientedHeight - 1) * 0.5;
        bool interpolate = settings.Straighten != 0;
        Parallel.For(0, targetHeight, options, y =>
        {
            for (int x = 0; x < targetWidth; x++)
            {
                double orientedX = x + cropX, orientedY = y + cropY;
                if (interpolate)
                {
                    double dx = orientedX - centerX, dy = orientedY - centerY;
                    orientedX = centerX + (cosine * dx + sine * dy) / zoom;
                    orientedY = centerY + (-sine * dx + cosine * dy) / zoom;
                }
                if (settings.FlipHorizontal) orientedX = orientedWidth - 1 - orientedX;
                (double sx, double sy) = turns switch
                {
                    1 => (orientedY, height - 1 - orientedX),
                    2 => (width - 1 - orientedX, height - 1 - orientedY),
                    3 => (width - 1 - orientedY, orientedX),
                    _ => (orientedX, orientedY)
                };
                int destination = (y * targetWidth + x) * 4;
                if (interpolate)
                    SampleBilinear(source, width, height, sx, sy, target, destination);
                else
                {
                    int input = ((int)sy * width + (int)sx) * 4;
                    target[destination] = source[input];
                    target[destination + 1] = source[input + 1];
                    target[destination + 2] = source[input + 2];
                    target[destination + 3] = source[input + 3];
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

    private static void ApplyColor(byte[] pixels, int width, int height, EditSettings settings, CubeLut? lut,
        float lutStrength, ParallelOptions options)
    {
        bool tone = settings.Exposure != 0 || settings.Temperature != 0 || settings.Tint != 0 ||
            settings.Contrast != 0 || settings.Highlights != 0 || settings.Shadows != 0 ||
            settings.CurveShadows != 0 || settings.CurveMidtones != 0 || settings.CurveHighlights != 0;
        bool mix = settings.Saturation != 0 || settings.Hue != 0;
        bool applyLut = lut is not null && lutStrength > 0;
        if (!tone && !mix && !applyLut) return;
        float[][] tables = BuildToneTables(settings);
        float[] redTable = tables[0], greenTable = tables[1], blueTable = tables[2];
        float hue = (float)(settings.Hue * Math.PI / 180), cosine = MathF.Cos(hue), sine = MathF.Sin(hue);
        float shared = (1 - cosine) / 3, spin = sine / MathF.Sqrt(3);
        float diagonal = cosine + shared, positive = shared + spin, negative = shared - spin;
        float saturation = (float)(1 + settings.Saturation / 100);
        // Rotate around the neutral RGB axis, then apply luminance-based saturation.
        var rotation = new Vector3(diagonal, positive, negative);
        var luminance = new Vector3(0.2126f, 0.7152f, 0.0722f);
        Vector3 rRow = new(rotation.X, rotation.Z, rotation.Y);
        Vector3 gRow = new(rotation.Y, rotation.X, rotation.Z);
        Vector3 bRow = new(rotation.Z, rotation.Y, rotation.X);
        Vector3 mixedLuminance = rRow * luminance.X + gRow * luminance.Y + bRow * luminance.Z;
        rRow = rRow * saturation + mixedLuminance * (1 - saturation);
        gRow = gRow * saturation + mixedLuminance * (1 - saturation);
        bRow = bRow * saturation + mixedLuminance * (1 - saturation);
        Parallel.For(0, height, options, y =>
        {
            int end = (y + 1) * width * 4;
            for (int index = y * width * 4; index < end; index += 4)
            {
                var color = new Vector3(redTable[pixels[index]], greenTable[pixels[index + 1]], blueTable[pixels[index + 2]]);
                if (mix) color = new Vector3(Vector3.Dot(rRow, color), Vector3.Dot(gRow, color), Vector3.Dot(bRow, color));
                color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
                if (applyLut)
                    color = Vector3.Lerp(color, lut!.TransformUnchecked(color.X, color.Y, color.Z), lutStrength);
                pixels[index] = ToByte(color.X);
                pixels[index + 1] = ToByte(color.Y);
                pixels[index + 2] = ToByte(color.Z);
            }
        });
    }

    private static float[][] BuildToneTables(EditSettings settings)
    {
        var tables = new[] { new float[256], new float[256], new float[256] };
        double exposure = Math.Pow(2, settings.Exposure);
        double[] gains = [exposure * Math.Pow(2, settings.Temperature * 0.0035),
            exposure * Math.Pow(2, -settings.Tint * 0.0025), exposure * Math.Pow(2, -settings.Temperature * 0.0035)];
        double contrast = Math.Pow(2, settings.Contrast / 100);
        double[] curve = CreateCurveNodes(settings);
        bool curves = settings.CurveShadows != 0 || settings.CurveMidtones != 0 || settings.CurveHighlights != 0;
        for (int channel = 0; channel < 3; channel++)
        for (int i = 0; i < 256; i++)
        {
            double value = i / 255.0;
            if (gains[channel] != 1)
            {
                double linear = value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                linear *= gains[channel];
                value = Math.Clamp(linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055, 0, 1);
            }
            if (settings.Shadows != 0 || settings.Highlights != 0)
            {
                double inverse = 1 - value;
                value = Math.Clamp(value + settings.Shadows * 0.035 * value * inverse * inverse * inverse +
                    settings.Highlights * 0.035 * inverse * value * value * value, 0, 1);
            }
            if (settings.Contrast != 0)
                value = value < 0.5 ? 0.5 * Math.Pow(value * 2, contrast) : 1 - 0.5 * Math.Pow((1 - value) * 2, contrast);
            if (curves) value = EvaluateCurve(curve, value);
            tables[channel][i] = (float)value;
        }
        return tables;
    }

    private static double[] CreateCurveNodes(EditSettings settings) =>
        [0, Math.Clamp(0.25 + settings.CurveShadows * 0.002, 0, 1), Math.Clamp(0.5 + settings.CurveMidtones * 0.002, 0, 1),
            Math.Clamp(0.75 + settings.CurveHighlights * 0.002, 0, 1), 1];

    private static double EvaluateCurve(double[] nodes, double value)
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

    private static void Denoise(byte[] pixels, int width, int height, float amount, ParallelOptions options)
    {
        byte[] temporary = ArrayPool<byte>.Shared.Rent(pixels.Length);
        try
        {
            var rangeWeights = new float[256];
            float sigma = 3 + amount * 0.35f;
            for (int difference = 0; difference < rangeWeights.Length; difference++)
                rangeWeights[difference] = MathF.Exp(-(difference * difference) / (2 * sigma * sigma));
            float blend = amount / 100;
            BilateralPass(pixels, temporary, width, height, rangeWeights, blend, horizontal: true, options);
            BilateralPass(temporary, pixels, width, height, rangeWeights, blend, horizontal: false, options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temporary);
        }
    }

    private static void BilateralPass(byte[] source, byte[] target, int width, int height, float[] rangeWeights,
        float blend, bool horizontal, ParallelOptions options)
    {
        Parallel.For(0, height, options, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                int centerR = source[index], centerG = source[index + 1], centerB = source[index + 2];
                float sumR = centerR, sumG = centerG, sumB = centerB, weightSum = 1;
                for (int offset = -2; offset <= 2; offset++)
                {
                    if (offset == 0) continue;
                    int sampleX = horizontal ? Math.Clamp(x + offset, 0, width - 1) : x;
                    int sampleY = horizontal ? y : Math.Clamp(y + offset, 0, height - 1);
                    int sampleIndex = (sampleY * width + sampleX) * 4;
                    int r = source[sampleIndex], g = source[sampleIndex + 1], b = source[sampleIndex + 2];
                    int difference = Math.Max(Math.Abs(r - centerR), Math.Max(Math.Abs(g - centerG), Math.Abs(b - centerB)));
                    float weight = rangeWeights[difference] * (Math.Abs(offset) == 1 ? 0.8f : 0.4f) * (source[sampleIndex + 3] / 255f);
                    sumR += r * weight;
                    sumG += g * weight;
                    sumB += b * weight;
                    weightSum += weight;
                }
                target[index] = (byte)Math.Clamp((int)MathF.Round(centerR + (sumR / weightSum - centerR) * blend), 0, 255);
                target[index + 1] = (byte)Math.Clamp((int)MathF.Round(centerG + (sumG / weightSum - centerG) * blend), 0, 255);
                target[index + 2] = (byte)Math.Clamp((int)MathF.Round(centerB + (sumB / weightSum - centerB) * blend), 0, 255);
                target[index + 3] = source[index + 3];
            }
        });
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

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}
