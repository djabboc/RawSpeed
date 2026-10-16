using System.Text.Json;
using RawSpeed.Core;

internal static class AdvancedTests
{
    internal static readonly (string Name, Action Run)[] Cases =
    [
        ("Immutable point curves have structural equality and JSON compatibility", CurvePersistence),
        ("PCHIP preserves monotonic intervals, moved endpoints and inversion", CurveShape),
        ("Master and independent RGB curves match their displayed transfer functions", ChannelCurves),
        ("Luminance-targeted shadows, highlights and neutral midtones", TargetedTones),
        ("Free rotation and bidirectional geometry/crop mapping", GeometryMapping),
        ("Neutral sample estimation removes a source white-balance cast", NeutralSampling),
        ("Separated luminance/chroma noise reduction preserves channel independence", IndependentNoise),
        ("Multiscale chroma reduction removes correlated color blotches", ChromaBlotches),
        ("Denoising preserves edges, alpha, tile boundaries and the source", NoiseBoundaries),
        ("Source-coordinate denoise cache is pixel-equivalent to direct rendering", DenoiseCache)
    ];

    private static void CurvePersistence()
    {
        CurvePoint[] points = [new(0, 0.1), new(0.4, 0.6), new(1, 0.9)];
        var curve = new ToneCurve(points);
        var equal = new ToneCurve(points.ToArray());
        points[1] = new CurvePoint(0.4, 0.2);
        Require(curve.Points[1].Y == 0.6 && curve.Equals(equal) && curve.GetHashCode() == equal.GetHashCode(), "Curve mutation/equality failure.");
        var settings = new EditSettings { MasterCurve = curve, RedCurve = ToneCurve.Identity, Midtones = 20, ColorDenoise = 65 };
        var restored = JsonSerializer.Deserialize<EditSettings>(JsonSerializer.Serialize(settings));
        Require(settings == restored, "Structural equality did not survive JSON; dirty-state tracking would break.");
        var old = JsonSerializer.Deserialize<EditSettings>("{\"Exposure\":0.5,\"Denoise\":35,\"CurveMidtones\":20}")!;
        Require(old.DenoiseDetail == 50 && old.MasterCurve is null && old.LuminanceDenoise == 0 && old.Denoise == 35, "Legacy recipe defaults changed.");
        foreach (CurvePoint[] invalid in new[]
        {
            new[] { new CurvePoint(0, 0) }, new[] { new CurvePoint(0, 0), new CurvePoint(0, 1), new CurvePoint(1, 1) },
            new[] { new CurvePoint(0.1, 0), new CurvePoint(1, 1) }, new[] { new CurvePoint(0, double.NaN), new CurvePoint(1, 1) },
            Enumerable.Range(0, 65).Select(i => new CurvePoint(i / 64d, i / 64d)).ToArray()
        }) ExpectArgument(() => new ToneCurve(invalid));
    }

    private static void CurveShape()
    {
        var curve = new ToneCurve([new(0, 0.12), new(0.1, 0.8), new(0.3, 0.8), new(0.7, 0.2), new(1, 0.9)]);
        for (int interval = 0; interval < curve.Points.Count - 1; interval++)
        {
            CurvePoint a = curve.Points[interval], b = curve.Points[interval + 1];
            double previous = a.Y;
            for (int step = 1; step <= 100; step++)
            {
                double value = curve.Evaluate(a.X + (b.X - a.X) * step / 100);
                Require(value >= Math.Min(a.Y, b.Y) - 1e-12 && value <= Math.Max(a.Y, b.Y) + 1e-12, "Curve overshoot.");
                Require((value - previous) * Math.Sign(b.Y - a.Y) >= -1e-12, "Interval changed monotonic direction.");
                previous = value;
            }
        }
        var reverse = new ToneCurve([new(0, 1), new(1, 0)]);
        for (int i = 0; i <= 100; i++) Near(1 - i / 100d, reverse.Evaluate(i / 100d), 1e-12);
        Require(curve.Evaluate(0) == 0.12 && curve.Evaluate(1) == 0.9, "Moved endpoint Y values were ignored.");
    }

    private static void ChannelCurves()
    {
        var master = new ToneCurve([new(0, 0.05), new(0.35, 0.55), new(1, 0.95)]);
        var red = new ToneCurve([new(0, 1), new(1, 0)]);
        var green = new ToneCurve([new(0, 0), new(0.6, 0.35), new(1, 1)]);
        byte[] pixels = new byte[256 * 4];
        for (int i = 0; i < 256; i++) { pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = (byte)i; pixels[i * 4 + 3] = 177; }
        var result = ImageProcessor.Render(pixels, 256, 1, new EditSettings { MasterCurve = master, RedCurve = red, GreenCurve = green });
        for (int i = 0; i < 256; i++)
        {
            double value = master.Evaluate(i / 255d);
            Near(red.Evaluate(value) * 255, result.Pixels[i * 4], 1);
            Near(green.Evaluate(value) * 255, result.Pixels[i * 4 + 1], 1);
            Near(value * 255, result.Pixels[i * 4 + 2], 1);
            Require(result.Pixels[i * 4 + 3] == 177, "Curves changed alpha.");
        }
    }

    private static void TargetedTones()
    {
        byte[] ramp = [32, 32, 32, 255, 128, 128, 128, 255, 224, 224, 224, 255];
        var shadows = ImageProcessor.Render(ramp, 3, 1, new EditSettings { Shadows = 80 }).Pixels;
        var highlights = ImageProcessor.Render(ramp, 3, 1, new EditSettings { Highlights = -80 }).Pixels;
        var midtones = ImageProcessor.Render(ramp, 3, 1, new EditSettings { Midtones = 80 }).Pixels;
        Require(shadows[0] > 70 && shadows[8] == 224, "Shadows did not selectively lift dark luminance.");
        Require(highlights[8] < 190 && highlights[0] == 32, "Highlights did not selectively compress bright luminance.");
        Require(midtones[4] - 128 > 3 * (midtones[0] - 32), "Midtones lack a localized middle response.");
        for (int i = 0; i < 3; i++) Require(midtones[i * 4] == midtones[i * 4 + 1] && midtones[i * 4] == midtones[i * 4 + 2], "Neutral gray acquired a cast.");
        var color = ImageProcessor.Render([70, 100, 130, 255], 1, 1, new EditSettings { Midtones = 30 }).Pixels;
        Near(30, color[1] - color[0], 1); Near(30, color[2] - color[1], 1);
    }

    private static void GeometryMapping()
    {
        foreach (double angle in new[] { -180d, -121, -90, -13, 0, 63, 90, 179, 180 })
        foreach (int turns in new[] { -1, 0, 1, 2 })
        {
            var settings = new EditSettings { Straighten = angle, RotationQuarterTurns = turns, FlipHorizontal = true, Crop = new NormalizedRect(0.1, 0.2, 0.7, 0.6) };
            var map = ImageGeometry.Create(79, 31, settings);
            foreach (var output in new[] { new ImagePoint(0, 0), new ImagePoint(map.OutputWidth - 1, map.OutputHeight - 1), new ImagePoint(4.2, 3.7) })
            {
                var source = map.MapOutputToSource(output.X, output.Y);
                Require(source.X >= -1e-9 && source.X <= 78 + 1e-9 && source.Y >= -1e-9 && source.Y <= 30 + 1e-9, "Free rotation samples outside its fitted source.");
                var roundtrip = map.MapSourceToOutput(source.X, source.Y);
                Near(output.X, roundtrip.X, 1e-9); Near(output.Y, roundtrip.Y, 1e-9);
            }
        }
        var cropMap = ImageGeometry.Create(100, 80, new EditSettings { Crop = new NormalizedRect(0.2, 0.25, 0.6, 0.5) });
        var nested = cropMap.MapOutputCropToCanvas(new NormalizedRect(0.25, 0.25, 0.5, 0.5));
        Near(0.35, nested.X, 1e-12); Near(0.375, nested.Y, 1e-12); Near(0.3, nested.Width, 1e-12); Near(0.25, nested.Height, 1e-12);
        byte[] constant = Constant(13, 29, 100, 100, 100);
        foreach (double angle in new[] { -180d, -90, 90, 135, 180 })
            Require(constant.SequenceEqual(ImageProcessor.Render(constant, 13, 29, new EditSettings { Straighten = angle }).Pixels), "Free rotation introduced empty corners.");
    }

    private static void NeutralSampling()
    {
        byte[] sample = Constant(16, 16, 118, 125, 130);
        NeutralBalance balance = ColorSampling.EstimateNeutralBalance(sample, 16, 16, 8, 8);
        byte[] corrected = ImageProcessor.Render(sample, 16, 16, new EditSettings { Temperature = balance.Temperature, Tint = balance.Tint }).Pixels;
        Near(corrected[0], corrected[1], 1); Near(corrected[1], corrected[2], 1);
        ExpectArgument(() => ColorSampling.EstimateNeutralBalance(Constant(1, 1, 255, 255, 255), 1, 1, 0, 0));
    }

    private static void IndependentNoise()
    {
        const int width = 192, height = 128;
        var random = new Random(340);
        byte[] luminance = Constant(width, height, 128, 128, 128), chroma = (byte[])luminance.Clone();
        for (int i = 0; i < width * height; i++)
        {
            int noise = random.Next(-24, 25);
            luminance[i * 4] = luminance[i * 4 + 1] = luminance[i * 4 + 2] = (byte)(128 + noise);
            int co = random.Next(-35, 36), cg = random.Next(-24, 25) * 2;
            chroma[i * 4] = (byte)(128 - cg / 2 + co); chroma[i * 4 + 1] = (byte)(128 + cg / 2); chroma[i * 4 + 2] = (byte)(128 - cg / 2 - co);
        }
        byte[] lumaResult = NoiseReducer.Reduce(luminance, width, height, 100, 0);
        byte[] colorResult = NoiseReducer.Reduce(chroma, width, height, 0, 100);
        double lumaRatio = Energy(lumaResult, width, height, false) / Energy(luminance, width, height, false);
        double colorRatio = Energy(colorResult, width, height, true) / Energy(chroma, width, height, true);
        Console.WriteLine($"  independent noise variance ratios: luminance={lumaRatio:F4}, chroma={colorRatio:F4}");
        Require(lumaRatio < 0.30 && colorRatio < 0.18, "Separated denoising did not substantially reduce noise.");
        Require(luminance.SequenceEqual(NoiseReducer.Reduce(luminance, width, height, 0, 100)), "Color-only denoise modified achromatic luminance noise.");
        for (int i = 0; i < width * height; i++) Near(128, (colorResult[i * 4] + 2 * colorResult[i * 4 + 1] + colorResult[i * 4 + 2]) / 4d, 0.5);
    }

    private static void ChromaBlotches()
    {
        const int width = 192, height = 128;
        byte[] pixels = Constant(width, height, 128, 128, 128);
        var random = new Random(45);
        for (int by = 0; by < height; by += 4)
        for (int bx = 0; bx < width; bx += 4)
        {
            int co = random.Next(-42, 43), cg = random.Next(-28, 29) * 2;
            for (int y = by; y < Math.Min(by + 4, height); y++)
            for (int x = bx; x < Math.Min(bx + 4, width); x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = (byte)(128 - cg / 2 + co); pixels[i + 1] = (byte)(128 + cg / 2); pixels[i + 2] = (byte)(128 - cg / 2 - co);
            }
        }
        byte[] result = NoiseReducer.Reduce(pixels, width, height, 0, 100, 30);
        double ratio = Energy(result, width, height, true) / Energy(pixels, width, height, true);
        Console.WriteLine($"  correlated 4px chroma blotch variance ratio: {ratio:F4}");
        Require(ratio < 0.28, "Large chroma blotches survived multiscale filtering.");
    }

    private static void NoiseBoundaries()
    {
        const int width = 600, height = 80;
        byte[] pixels = Constant(width, height, 35, 35, 35);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            int value = x < 256 ? 35 : 215;
            int noise = (x * 31 + y * 17) % 19 - 9;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(value + noise);
            pixels[i + 3] = (byte)(64 + (x + y) % 192);
        }
        byte[] original = (byte[])pixels.Clone();
        byte[] result = NoiseReducer.Reduce(pixels, width, height, 100, 100, 40);
        Require(pixels.SequenceEqual(original), "Denoiser mutated its caller's input.");
        for (int i = 3; i < pixels.Length; i += 4) Require(pixels[i] == result[i], "Denoiser modified alpha.");
        for (int y = 8; y < height - 8; y++)
        {
            Require(result[(y * width + 256) * 4] - result[(y * width + 255) * 4] > 155, "Strong boundary was softened across a tile seam.");
            for (int x = 245; x < 266; x++)
                Require(result[(y * width + x) * 4] is >= 26 and <= 224, "Noise reduction created a halo outside the source range.");
        }
        byte[] flat = Constant(width, height, 112, 112, 112);
        Require(flat.SequenceEqual(NoiseReducer.Reduce(flat, width, height, 100, 100)), "Flat image gained seams or a color cast.");
    }

    private static void DenoiseCache()
    {
        byte[] pixels = new byte[83 * 59 * 4]; new Random(120).NextBytes(pixels);
        var settings = new EditSettings { Denoise = 20, LuminanceDenoise = 60, ColorDenoise = 70, Exposure = 0.7, Midtones = 25, Straighten = 21, RotationQuarterTurns = 1, Crop = new NormalizedRect(0.1, 0.1, 0.7, 0.8) };
        var direct = ImageProcessor.Render(pixels, 83, 59, settings);
        byte[] denoised = NoiseReducer.Reduce(pixels, 83, 59, settings);
        var cached = ImageProcessor.Render(denoised, 83, 59, settings with { Denoise = 0, LuminanceDenoise = 0, ColorDenoise = 0 });
        Require(direct.Pixels.SequenceEqual(cached.Pixels), "Reusing a source denoise buffer changed geometry/color output.");
    }

    private static double Energy(byte[] pixels, int width, int height, bool chroma)
    {
        double sum = 0; int count = 0;
        for (int y = 16; y < height - 16; y++)
        for (int x = 16; x < width - 16; x++)
        {
            int i = (y * width + x) * 4;
            double r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
            sum += chroma ? Math.Pow((r - b) / 2, 2) + Math.Pow(g - (r + b) / 2, 2) : Math.Pow((r + 2 * g + b) / 4 - 128, 2);
            count++;
        }
        return sum / count;
    }

    private static byte[] Constant(int width, int height, byte r, byte g, byte b)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255; }
        return pixels;
    }
    private static void Near(double expected, double actual, double tolerance) => Require(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectArgument(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid arguments accepted."); }
}
