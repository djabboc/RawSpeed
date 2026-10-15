using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using RawSpeed.Core;

var cases = new (string Name, Action Run)[]
{
    ("Default render is lossless, independent, with exact channel histograms", DefaultIsLossless),
    ("Clockwise rotation, negative rotation, flip, and oriented crop", Geometry),
    ("Straighten handles non-square images and single-pixel dimensions", Straighten),
    ("3D cube red-fastest interpolation, non-default domain, and strength", LutInterpolation),
    ("Malformed, oversized, non-finite and shaper LUTs are rejected", InvalidLuts),
    ("Exposure, white balance, saturation, hue, and curves", ColorAdjustments),
    ("Public curve evaluation matches rendered curve pixels", CurvePreview),
    ("Bilateral denoise lowers variance while preserving an edge and alpha", Denoise),
    ("Invalid dimensions, ranges and crop rectangles are rejected", InvalidInputs),
    ("Pre-cancellation and cancellation during rendering", Cancellation),
    ("Concurrent renders are deterministic and do not mutate their source", ConcurrentRenders)
};
int failures = 0;
foreach (var (name, run) in cases)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception}"); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} tests passed.");
if (failures == 0 && args.Contains("--benchmark", StringComparer.Ordinal)) Benchmark();
return failures == 0 ? 0 : 1;

static void DefaultIsLossless()
{
    byte[] source = new byte[257 * 13 * 4];
    new Random(42).NextBytes(source);
    byte[] before = (byte[])source.Clone();
    var rendered = ImageProcessor.Render(source, 257, 13, new EditSettings());
    True(source.SequenceEqual(rendered.Pixels), "Default render changed bytes.");
    True(source.SequenceEqual(before), "Input was modified.");
    True(!ReferenceEquals(source, rendered.Pixels), "Output aliases input.");
    Equal(768, rendered.Histogram.Length);
    for (int channel = 0; channel < 3; channel++)
    {
        int[] expected = new int[256];
        for (int i = channel; i < source.Length; i += 4) expected[source[i]]++;
        True(expected.SequenceEqual(rendered.Histogram.Skip(channel * 256).Take(256)), "Histogram mismatch.");
    }
}

static void Geometry()
{
    byte[] source = Gray([1, 2, 3, 4, 5, 6]);
    var clockwise = ImageProcessor.Render(source, 3, 2, new EditSettings { RotationQuarterTurns = 1 });
    Equal(2, clockwise.Width); Equal(3, clockwise.Height);
    EqualPixels([4, 1, 5, 2, 6, 3], clockwise);
    EqualPixels([3, 6, 2, 5, 1, 4], ImageProcessor.Render(source, 3, 2, new EditSettings { RotationQuarterTurns = -1 }));
    EqualPixels([6, 5, 4, 3, 2, 1], ImageProcessor.Render(source, 3, 2, new EditSettings { RotationQuarterTurns = 2 }));
    EqualPixels([3, 2, 1, 6, 5, 4], ImageProcessor.Render(source, 3, 2, new EditSettings { FlipHorizontal = true }));
    EqualPixels([1, 4, 2, 5, 3, 6], ImageProcessor.Render(source, 3, 2,
        new EditSettings { RotationQuarterTurns = 1, FlipHorizontal = true }));
    var crop = ImageProcessor.Render(source, 3, 2, new EditSettings { RotationQuarterTurns = 1, Crop = new NormalizedRect(0.5, 0, 0.5, 1) });
    Equal(1, crop.Width); Equal(3, crop.Height); EqualPixels([1, 2, 3], crop);
    var noRotation = ImageProcessor.Render(source, 3, 2, new EditSettings { RotationQuarterTurns = 4 });
    True(source.SequenceEqual(noRotation.Pixels), "Four quarter turns must be identity.");
}

static void Straighten()
{
    foreach ((int width, int height) in new[] { (79, 13), (13, 79), (1, 17), (17, 1), (1, 1) })
    foreach (double angle in new[] { -45d, -12d, 12d, 45d })
    {
        byte[] source = Gray(Enumerable.Repeat((byte)123, width * height).ToArray());
        var rendered = ImageProcessor.Render(source, width, height, new EditSettings { Straighten = angle });
        Equal(width, rendered.Width); Equal(height, rendered.Height);
        True(source.SequenceEqual(rendered.Pixels), "Straighten introduced corners or changed a constant image.");
    }
    // At the invariant image center, a symmetric gradient must preserve its value.
    byte[] gradient = Gray(Enumerable.Range(0, 25).Select(i => (byte)(i * 10)).ToArray());
    var rotated = ImageProcessor.Render(gradient, 5, 5, new EditSettings { Straighten = 30 });
    Equal(120, rotated.Pixels[(2 * 5 + 2) * 4]);
    True(!gradient.SequenceEqual(rotated.Pixels), "Nonzero straighten did not affect a gradient.");
}

static void LutInterpolation()
{
    var identity = CubeLut.Parse(new StringReader(CubeText((r, g, b) => new Vector3(r, g, b))));
    Near(new Vector3(0.17f, 0.45f, 0.83f), identity.Transform(0.17f, 0.45f, 0.83f), 0.00001f);
    var invert = CubeLut.Parse(new StringReader(CubeText((r, g, b) => new Vector3(1 - r, 1 - g, 1 - b))));
    Near(new Vector3(0.83f, 0.55f, 0.17f), invert.Transform(0.17f, 0.45f, 0.83f), 0.00001f);
    var permute = CubeLut.Parse(new StringReader(CubeText((r, g, b) => new Vector3(b, r, g))));
    Near(new Vector3(0.8f, 0.2f, 0.4f), permute.Transform(0.2f, 0.4f, 0.8f), 0.00001f);
    var products = CubeLut.Parse(new StringReader(CubeText((r, g, b) => new Vector3(r * g, g * b, b * r))));
    Near(new Vector3(0.08f, 0.32f, 0.16f), products.Transform(0.2f, 0.4f, 0.8f), 0.00001f);
    var largerIdentity = CubeLut.Parse(new StringReader(CubeText((r, g, b) => new Vector3(r, g, b), size: 5)));
    Near(new Vector3(0.17f, 0.45f, 0.83f), largerIdentity.Transform(0.17f, 0.45f, 0.83f), 0.00001f);
    var domain = CubeLut.Parse(new StringReader(CubeText((r, g, b) => new Vector3(r, g, b), "DOMAIN_MIN -1 -2 -3\nDOMAIN_MAX 1 2 3\n")));
    Near(new Vector3(0.5f, 0.75f, 1), domain.Transform(0, 1, 3), 0.00001f);
    Near(new Vector3(0, 1, 1), domain.Transform(-5, 8, 9), 0.00001f);
    byte[] sample = [20, 80, 140, 77];
    var half = ImageProcessor.Render(sample, 1, 1, new EditSettings(), invert, 0.5);
    for (int c = 0; c < 3; c++) True(Math.Abs(half.Pixels[c] - 128) <= 1, "LUT strength mismatch.");
    Equal(77, half.Pixels[3]);
    True(sample.SequenceEqual(ImageProcessor.Render(sample, 1, 1, new EditSettings(), invert, 0).Pixels), "Zero LUT strength must be identity.");
    byte[] ramp = Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i, (byte)(255 - i), (byte)(i / 2), (byte)255 }).ToArray();
    True(ramp.SequenceEqual(ImageProcessor.Render(ramp, 256, 1, new EditSettings(), identity).Pixels), "Identity cube must preserve bytes.");
    Throws<ArgumentOutOfRangeException>(() => identity.Transform(float.NaN, 0, 0));
}

static void InvalidLuts()
{
    string identity = CubeText((r, g, b) => new Vector3(r, g, b));
    foreach (string invalid in new[]
    {
        "", "LUT_3D_SIZE 1", "LUT_3D_SIZE 129", "LUT_3D_SIZE 99999999999999", "LUT_1D_SIZE 2\n0 0 0\n1 1 1",
        identity + "1 1 1\n", identity.Replace("0 0 0", "NaN 0 0", StringComparison.Ordinal),
        identity.Replace("0 0 0", "Infinity 0 0", StringComparison.Ordinal),
        "LUT_3D_SIZE 2\n0 0 0\n", "LUT_3D_SIZE 2\nLUT_3D_SIZE 2\n",
        identity.Replace("LUT_3D_SIZE 2", "LUT_3D_SIZE 2\nDOMAIN_MIN 1 0 0\nDOMAIN_MAX 1 1 1", StringComparison.Ordinal),
        identity.Replace("LUT_3D_SIZE 2", "LUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\nDOMAIN_MIN 0 0 0", StringComparison.Ordinal),
        identity.Replace("LUT_3D_SIZE 2", "LUT_3D_SIZE 2\nDOMAIN_MAX 1e-44 1e-44 1e-44", StringComparison.Ordinal),
        identity + "DOMAIN_MIN 0 0 0\n", new string('x', 4097)
    }) Throws<FormatException>(() => CubeLut.Parse(new StringReader(invalid)));
    var previousCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        var cube = CubeLut.Parse(new StringReader(identity.Replace("1 1 1", "0.75 0.75 0.75", StringComparison.Ordinal)));
        Near(new Vector3(0.75f), cube.Transform(1, 1, 1), 0.00001f);
    }
    finally { CultureInfo.CurrentCulture = previousCulture; }
}

static void ColorAdjustments()
{
    byte[] source = [64, 96, 128, 42];
    var lighter = ImageProcessor.Render(source, 1, 1, new EditSettings { Exposure = 1 }).Pixels;
    True(lighter[0] > source[0] && lighter[1] > source[1] && lighter[2] > source[2], "Exposure must brighten each channel.");
    Equal(42, lighter[3]);
    byte[] neutral = [100, 100, 100, 255];
    var warm = ImageProcessor.Render(neutral, 1, 1, new EditSettings { Temperature = 100 }).Pixels;
    True(warm[0] > 100 && warm[2] < 100, "Positive temperature must warm the image.");
    var magenta = ImageProcessor.Render(neutral, 1, 1, new EditSettings { Tint = 100 }).Pixels;
    True(magenta[1] < 100, "Positive tint must remove green.");
    var desaturated = ImageProcessor.Render(source, 1, 1, new EditSettings { Saturation = -100 }).Pixels;
    Equal(desaturated[0], desaturated[1]); Equal(desaturated[1], desaturated[2]);
    var hue = ImageProcessor.Render([255, 0, 0, 255], 1, 1, new EditSettings { Hue = 120 }).Pixels;
    True(hue[0] <= 1 && hue[1] >= 254 && hue[2] <= 1, "A 120-degree hue rotation must map red to green.");
    var middle = ImageProcessor.Render([128, 128, 128, 255], 1, 1, new EditSettings { CurveMidtones = 100 }).Pixels;
    True(middle[0] > 160, "Midtone curve must brighten middle values.");
    byte[] endpoints = [0, 0, 0, 255, 255, 255, 255, 255];
    True(endpoints.SequenceEqual(ImageProcessor.Render(endpoints, 2, 1, new EditSettings { CurveShadows = 50, CurveMidtones = 50, CurveHighlights = -50 }).Pixels), "Curves must anchor black and white.");
}

static void Denoise()
{
    const int width = 128, height = 64;
    byte[] source = new byte[width * height * 4];
    var random = new Random(123);
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        int index = (y * width + x) * 4;
        byte value = (byte)((x < width / 2 ? 45 : 205) + random.Next(-12, 13));
        source[index] = source[index + 1] = source[index + 2] = value;
        source[index + 3] = 255;
    }
    var result = ImageProcessor.Render(source, width, height, new EditSettings { Denoise = 100 });
    double before = 0, after = 0;
    for (int y = 4; y < height - 4; y++)
    for (int x = 4; x < width / 2 - 4; x++)
    {
        int index = (y * width + x) * 4;
        before += Math.Pow(source[index] - 45, 2);
        after += Math.Pow(result.Pixels[index] - 45, 2);
    }
    True(after < before * 0.45, $"Noise variance was not sufficiently reduced: {after / before:P1}.");
    for (int y = 4; y < height - 4; y++)
    {
        int left = result.Pixels[(y * width + width / 2 - 1) * 4], right = result.Pixels[(y * width + width / 2) * 4];
        True(right - left > 140, "Strong edge was blurred.");
    }
    for (int i = 3; i < source.Length; i += 4) Equal(source[i], result.Pixels[i]);
    byte[] constant = Gray(Enumerable.Repeat((byte)111, 17 * 19).ToArray());
    True(constant.SequenceEqual(ImageProcessor.Render(constant, 17, 19, new EditSettings { Denoise = 100 }).Pixels), "Denoise changed a constant image.");
}

static void CurvePreview()
{
    foreach (var settings in new[] { new EditSettings(), new EditSettings { CurveShadows = 45, CurveMidtones = -35, CurveHighlights = 80 } })
    {
        byte[] ramp = Gray(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        byte[] rendered = ImageProcessor.Render(ramp, 256, 1, settings).Pixels;
        for (int i = 0; i < 256; i++)
            Equal((int)Math.Round(ImageProcessor.EvaluateCurve(i / 255.0, settings) * 255), rendered[i * 4]);
    }
    Throws<ArgumentOutOfRangeException>(() => ImageProcessor.EvaluateCurve(double.NaN, new EditSettings()));
}

static void InvalidInputs()
{
    ImageProcessor.ValidateSettings(new EditSettings());
    Throws<ArgumentNullException>(() => ImageProcessor.ValidateSettings(null!));
    Throws<ArgumentOutOfRangeException>(() => ImageProcessor.ValidateSettings(new EditSettings { Exposure = 11 }));
    Throws<ArgumentException>(() => ImageProcessor.Render(new byte[3], 1, 1, new EditSettings()));
    Throws<ArgumentException>(() => ImageProcessor.Render([], int.MaxValue, int.MaxValue, new EditSettings()));
    Throws<ArgumentException>(() => ImageProcessor.Render([], 0, 1, new EditSettings()));
    foreach (var settings in new[]
    {
        new EditSettings { Exposure = double.NaN }, new EditSettings { Saturation = 101 },
        new EditSettings { Denoise = -1 }, new EditSettings { Hue = double.PositiveInfinity },
        new EditSettings { Crop = new NormalizedRect(0, 0, 0, 1) },
        new EditSettings { Crop = new NormalizedRect(0.9, 0, 0.2, 1) },
        new EditSettings { Crop = new NormalizedRect(0, double.NaN, 1, 1) }
    }) Throws<ArgumentOutOfRangeException>(() => ImageProcessor.Render([1, 2, 3, 4], 1, 1, settings));
    Throws<ArgumentOutOfRangeException>(() => ImageProcessor.Render([1, 2, 3, 4], 1, 1, new EditSettings(), null, double.NaN));
}

static void Cancellation()
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Throws<OperationCanceledException>(() => ImageProcessor.Render([1, 2, 3, 4], 1, 1, new EditSettings(), cancellationToken: cancelled.Token));
    byte[] large = new byte[4096 * 2048 * 4];
    using var during = new CancellationTokenSource();
    var task = Task.Run(() => ImageProcessor.Render(large, 4096, 2048, new EditSettings { Denoise = 100, Straighten = 12 }, cancellationToken: during.Token));
    during.CancelAfter(TimeSpan.FromMilliseconds(10));
    Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
}

static void ConcurrentRenders()
{
    byte[] source = new byte[97 * 83 * 4];
    new Random(991).NextBytes(source);
    byte[] original = (byte[])source.Clone();
    var settings = new EditSettings { Exposure = 0.5, Contrast = 20, Denoise = 50, Hue = -13, Straighten = 5 };
    var expected = ImageProcessor.Render(source, 97, 83, settings);
    Parallel.For(0, 4, _ =>
    {
        var actual = ImageProcessor.Render(source, 97, 83, settings);
        True(expected.Pixels.SequenceEqual(actual.Pixels), "Concurrent render mismatch.");
        True(expected.Histogram.SequenceEqual(actual.Histogram), "Concurrent histogram mismatch.");
    });
    True(original.SequenceEqual(source), "Rendering modified shared input.");
}

static void Benchmark()
{
    Console.WriteLine($"Benchmark: {Environment.ProcessorCount} logical processors; .NET {Environment.Version}; {Environment.OSVersion}");
    foreach ((int width, int height) in new[] { (1600, 1067), (6000, 4000) })
    {
        byte[] source = new byte[width * height * 4];
        new Random(44).NextBytes(source);
        for (int i = 3; i < source.Length; i += 4) source[i] = 255;
        var identity = CubeLut.Parse(new StringReader(CubeText((r, g, b) => new Vector3(r, g, b), size: 33)));
        foreach (var (label, settings, lut) in new (string, EditSettings, CubeLut?)[]
        {
            ("identity + histogram", new EditSettings(), null),
            ("color + histogram", new EditSettings { Exposure = 0.4, Contrast = 20, Saturation = 10, Temperature = 15, CurveMidtones = 10 }, null),
            ("color + 3D LUT", new EditSettings { Exposure = 0.4, Saturation = 10 }, identity),
            ("denoise 60", new EditSettings { Denoise = 60 }, null),
            ("straighten + color", new EditSettings { Straighten = 5, Exposure = 0.4 }, null)
        })
        {
            var warmup = Stopwatch.StartNew();
            int warmupRuns = 0;
            do
            {
                ImageProcessor.Render(source, width, height, settings, lut);
                warmupRuns++;
            } while (warmupRuns < 3 || warmup.ElapsedMilliseconds < 300);
            double[] times = new double[5];
            for (int i = 0; i < times.Length; i++)
            {
                var watch = Stopwatch.StartNew();
                ImageProcessor.Render(source, width, height, settings, lut);
                times[i] = watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            Console.WriteLine($"{width}x{height} {label,-23}: median {times[2],8:F1} ms; {width * (double)height / times[2] / 1000:F1} MP/s");
        }
    }
}

static string CubeText(Func<float, float, float, Vector3> transform, string domain = "", int size = 2)
{
    var text = new StringBuilder($"# test cube\nTITLE \"Test cube\"\nLUT_3D_SIZE {size}\n" + domain);
    for (int b = 0; b < size; b++)
    for (int g = 0; g < size; g++)
    for (int r = 0; r < size; r++)
    {
        Vector3 value = transform(r / (size - 1f), g / (size - 1f), b / (size - 1f));
        text.AppendLine(FormattableString.Invariant($"{value.X} {value.Y} {value.Z}"));
    }
    return text.ToString();
}

static byte[] Gray(byte[] values) => values.SelectMany(value => new[] { value, value, value, (byte)255 }).ToArray();
static void EqualPixels(byte[] expected, RenderedImage actual) => True(Gray(expected).SequenceEqual(actual.Pixels), "Pixel arrangement mismatch.");
static void Near(Vector3 expected, Vector3 actual, float tolerance) => True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}.");
static void Equal(int expected, int actual) => True(expected == actual, $"Expected {expected}, got {actual}.");
static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
