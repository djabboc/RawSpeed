using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageMagick;
using MetadataExtractor;
using RawSpeed.Core;
using RawSpeed.Imaging;
using RawSpeed.Metadata;

namespace RawSpeed.IntegrationTests;

internal static class Program
{
    private const int PreviewEdge = 1600;
    private static readonly ImageFileService Images = new();
    private static readonly MetadataService Metadata = new();
    private static readonly List<CheckResult> Checks = [];
    private static readonly List<SampleResult> Samples = [];

    public static async Task<int> Main(string[] args)
    {
        string root = args.Length == 0 ? FindRoot() : Path.GetFullPath(args[0]);
        if (args.Length > 1 || !System.IO.Directory.Exists(Path.Combine(root, "example")) ||
            !System.IO.Directory.Exists(Path.Combine(root, "src")))
        {
            Console.Error.WriteLine("Usage: RawSpeed.IntegrationTests [repository-root]; the root must contain example and src.");
            return 2;
        }
        string artifacts = Path.Combine(root, "artifacts");
        string output = Path.Combine(artifacts, "integration-runs", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(output);
        string lutPath = Path.Combine(output, "integration-style.cube");
        await File.WriteAllTextAsync(lutPath, BuildCube());
        CubeLut lut = CubeLut.Load(lutPath);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        foreach (string name in new[] { "nikon.NEF", "olympus.ORF", "P1010002.JPG" })
        {
            string path = Path.Combine(root, "example", name);
            await CheckAsync($"{name}: real decode, metadata, edits and full-resolution export", async () =>
                await ValidateSampleAsync(path, output, lut));
        }

        string jpegSource = Path.Combine(root, "example", "P1010002.JPG");
        string rawSource = Path.Combine(root, "example", "nikon.NEF");
        await CheckAsync("Corrupt JPEG, NEF and ORF are rejected", async () =>
        {
            foreach (string extension in new[] { ".jpg", ".nef", ".orf" })
            {
                string path = Path.Combine(output, "corrupt" + extension);
                await File.WriteAllTextAsync(path, "This deliberately invalid fixture is not a photograph.");
                await ThrowsAsync<MagickException>(() => Images.LoadPreviewAsync(path, PreviewEdge, CancellationToken.None));
                Throws<ImageProcessingException>(() => Metadata.Read(path));
            }
        });
        await CheckAsync("Missing and unsupported inputs are rejected", async () =>
        {
            await ThrowsAsync<FileNotFoundException>(() => Images.LoadFullAsync(Path.Combine(output, "missing.nef"), CancellationToken.None));
            await ThrowsAsync<NotSupportedException>(() => Images.LoadFullAsync(lutPath, CancellationToken.None));
        });
        await CheckAsync("Pre-cancelled decode, render and export publish no output", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => Images.LoadPreviewAsync(jpegSource, PreviewEdge, cancellation.Token));
            Throws<OperationCanceledException>(() => ImageProcessor.Render([10, 20, 30, 255], 1, 1, new EditSettings(), cancellationToken: cancellation.Token));
            string path = Path.Combine(output, "cancelled.png");
            await ThrowsAsync<OperationCanceledException>(() => Images.ExportAsync(path, 1, 1, [10, 20, 30, 255], 95, cancellation.Token));
            Assert(!File.Exists(path), "Cancelled export left a published output.");
        });
        await CheckAsync("In-flight RAW cancellation rejects result after native operation", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            Task<DecodedImage> loading = Images.LoadFullAsync(rawSource, cancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
            await ThrowsAsync<OperationCanceledException>(async () => await loading);
            // The process-wide native gate must still be usable after cancellation.
            DecodedImage recovered = await Images.LoadPreviewAsync(jpegSource, 64, CancellationToken.None);
            Assert(recovered.Width > 0 && recovered.Height > 0, "Decode gate did not recover after cancellation.");
        });
        await CheckAsync("Existing destination is preserved and simultaneous exports cannot overwrite", async () =>
        {
            string existing = Path.Combine(output, "existing.jpg");
            byte[] sentinel = Encoding.UTF8.GetBytes("Existing user content must survive.");
            await File.WriteAllBytesAsync(existing, sentinel);
            await ThrowsAsync<IOException>(() => Images.ExportAsync(existing, 1, 1, [10, 20, 30, 255], 95, CancellationToken.None));
            Assert((await File.ReadAllBytesAsync(existing)).AsSpan().SequenceEqual(sentinel), "Existing destination changed.");

            string collision = Path.Combine(output, "simultaneous.png");
            async Task<bool> AttemptAsync()
            {
                try
                {
                    await Images.ExportAsync(collision, 1, 1, [10, 20, 30, 255], 95, CancellationToken.None);
                    return true;
                }
                catch (IOException) { return false; }
            }
            bool[] successes = await Task.WhenAll(AttemptAsync(), AttemptAsync());
            Assert(successes.Count(success => success) == 1, "Exactly one simultaneous export must succeed.");
            using var decoded = new MagickImage(collision);
            using var pixels = decoded.GetPixels();
            Assert(pixels.ToByteArray(PixelMapping.RGBA)!.AsSpan().SequenceEqual(new byte[] { 10, 20, 30, 255 }), "Collision result is incomplete or changed.");
            Assert(!System.IO.Directory.EnumerateFiles(output, ".rawspeed-*.tmp").Any(), "Temporary export files were not cleaned up.");
        });

        var process = Process.GetCurrentProcess();
        var report = new
        {
            startedUtc = started,
            completedUtc = DateTimeOffset.UtcNow,
            success = Checks.All(check => check.Passed),
            environment = new
            {
                operatingSystem = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                logicalProcessors = Environment.ProcessorCount,
                availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                imageMagick = MagickNET.Version,
#if DEBUG
                configuration = "Debug",
#else
                configuration = "Release",
#endif
                previewEdge = PreviewEdge,
                timingMethod = "Stopwatch wall-clock; decoded sample timings are single observations, preview render uses five warm observations after at least ten warmup renders over at least 1500 ms to allow tiered JIT compilation; OS disk cache and concurrent processes are uncontrolled."
            },
            outputDirectory = Path.GetRelativePath(root, output),
            samples = Samples,
            checks = Checks
        };
        string reportPath = Path.Combine(artifacts, "integration-results.json");
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), json);
        await File.WriteAllTextAsync(reportPath, json);
        Console.WriteLine($"{Checks.Count(check => check.Passed)}/{Checks.Count} integration checks passed. Report: {reportPath}");
        return report.success ? 0 : 1;
    }

    private static async Task ValidateSampleAsync(string path, string output, CubeLut lut)
    {
        string before = await FileHashAsync(path);
        var sample = new SampleResult(Path.GetFileName(path), new FileInfo(path).Length, before);
        Samples.Add(sample);
        try
        {
            bool raw = !Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase);
            var watch = Stopwatch.StartNew();
            DecodedImage? embedded = await Images.TryLoadEmbeddedPreviewAsync(path, PreviewEdge, CancellationToken.None);
            sample.TimingsMs["embeddedPreview"] = watch.Elapsed.TotalMilliseconds;
            if (raw)
            {
                Assert(embedded is not null, "Expected supplied RAW sample to contain an embedded preview.");
                Assert(embedded!.DecodeMode.StartsWith("Embedded", StringComparison.Ordinal), "Embedded JPEG was not clearly distinguished from RAW development.");
                Assert(Math.Max(embedded.Width, embedded.Height) <= PreviewEdge, "Embedded preview exceeds its resolution limit.");
                AssertNonBlank(embedded.Pixels);
            }
            else Assert(embedded is null, "JPEG must not pretend to contain a separate RAW preview.");
            watch.Restart();
            DecodedImage preview = await Images.LoadPreviewAsync(path, PreviewEdge, CancellationToken.None);
            sample.TimingsMs["previewDecode"] = watch.Elapsed.TotalMilliseconds;
            sample.PreviewWidth = preview.Width;
            sample.PreviewHeight = preview.Height;
            sample.DecodeMode = preview.DecodeMode;
            Assert(Math.Max(preview.Width, preview.Height) <= PreviewEdge, "Preview exceeds its resolution limit.");
            Assert(preview.Pixels.LongLength == (long)preview.Width * preview.Height * 4, "Preview pixel count mismatch.");
            Assert(preview.DecodeMode.Contains(raw ? "LibRaw" : "JPEG", StringComparison.Ordinal), "Wrong decoder path.");
            AssertNonBlank(preview.Pixels);
            string previewHash = Convert.ToHexString(SHA256.HashData(preview.Pixels));

            watch.Restart();
            IReadOnlyList<MetadataEntry> metadata = Metadata.Read(path);
            sample.TimingsMs["metadata"] = watch.Elapsed.TotalMilliseconds;
            sample.MetadataFields = metadata.Count;
            sample.MetadataWarnings = metadata.Count(entry => entry.Name == "Decoder warning");
            sample.MetadataGroups = metadata.Select(entry => entry.Group).Distinct().ToArray();
            Assert(metadata.Count > 100, "Expected camera metadata is missing.");
            Assert(metadata.Any(entry => entry.Name.StartsWith("Model", StringComparison.Ordinal)), "Camera model is missing.");
            string vendor = Path.GetExtension(path).Equals(".nef", StringComparison.OrdinalIgnoreCase) ? "Nikon" : "Olympus";
            Assert(metadata.Any(entry => entry.Group.StartsWith(vendor, StringComparison.Ordinal)), "Camera maker notes are missing.");
            if (vendor == "Nikon") Assert(metadata.Any(entry => entry.Group == "XMP" && entry.Name.Contains(':')), "Actual XMP properties were not expanded.");

            foreach (var (name, settings) in new (string, EditSettings)[]
            {
                ("exposure", new() { Exposure = 0.6 }),
                ("whiteBalance", new() { Temperature = 25, Tint = -15 }),
                ("curve", new() { CurveShadows = -12, CurveMidtones = 20, CurveHighlights = 8 }),
                ("hueSaturation", new() { Hue = 15, Saturation = 20 }),
                ("denoise", new() { Denoise = 65 }),
                ("straighten", new() { Straighten = 3 })
            })
            {
                watch.Restart();
                RenderedImage edited = ImageProcessor.Render(preview.Pixels, preview.Width, preview.Height, settings);
                sample.TimingsMs[$"previewSingleEdit.{name}"] = watch.Elapsed.TotalMilliseconds;
                Assert(!edited.Pixels.AsSpan().SequenceEqual(preview.Pixels), $"{name} did not change the sample.");
                Assert(edited.Width == preview.Width && edited.Height == preview.Height, $"{name} unexpectedly resized the sample.");
            }
            RenderedImage styled = ImageProcessor.Render(preview.Pixels, preview.Width, preview.Height, new EditSettings(), lut, 0.65);
            Assert(!styled.Pixels.AsSpan().SequenceEqual(preview.Pixels), "Imported LUT did not change the sample.");

            EditSettings combined = CombinedSettings();
            var warmup = Stopwatch.StartNew();
            int warmupRuns = 0;
            do
            {
                ImageProcessor.Render(preview.Pixels, preview.Width, preview.Height, combined, lut, 0.45);
                warmupRuns++;
            } while (warmupRuns < 10 || warmup.ElapsedMilliseconds < 1500);
            double[] warmTimes = new double[5];
            for (int i = 0; i < warmTimes.Length; i++)
            {
                watch.Restart();
                RenderedImage rendered = ImageProcessor.Render(preview.Pixels, preview.Width, preview.Height, combined, lut, 0.45);
                warmTimes[i] = watch.Elapsed.TotalMilliseconds;
                Assert(rendered.Width < preview.Height && rendered.Height < preview.Width, "Crop and quarter-turn did not affect output geometry.");
                for (int channel = 0; channel < 3; channel++)
                    Assert(rendered.Histogram.Skip(channel * 256).Take(256).Sum() == rendered.Width * rendered.Height, "Histogram does not cover the output image.");
            }
            sample.WarmCombinedPreviewMs = warmTimes;
            sample.TimingsMs["previewCombinedMedian"] = warmTimes.Order().ElementAt(2);
            Assert(Convert.ToHexString(SHA256.HashData(preview.Pixels)) == previewHash, "Editing mutated the preview input buffer.");

            watch.Restart();
            DecodedImage full = await Images.LoadFullAsync(path, CancellationToken.None);
            sample.TimingsMs["fullDecode"] = watch.Elapsed.TotalMilliseconds;
            sample.FullWidth = full.Width;
            sample.FullHeight = full.Height;
            Assert(full.Width == preview.OriginalWidth && full.Height == preview.OriginalHeight, "Full-resolution dimensions differ from preview source dimensions.");
            Assert(full.Width > preview.Width && full.Height > preview.Height, "Full-size source did not contain more detail than the preview.");
            watch.Restart();
            RenderedImage final = ImageProcessor.Render(full.Pixels, full.Width, full.Height, combined, lut, 0.45);
            sample.TimingsMs["fullRender"] = watch.Elapsed.TotalMilliseconds;
            sample.ExportWidth = final.Width;
            sample.ExportHeight = final.Height;

            foreach (string extension in new[] { ".jpg", ".png" })
            {
                string destination = Path.Combine(output, Path.GetFileName(path) + ".edited" + extension);
                watch.Restart();
                await Images.ExportAsync(destination, final.Width, final.Height, final.Pixels, 95, CancellationToken.None);
                sample.TimingsMs["export" + extension] = watch.Elapsed.TotalMilliseconds;
                Assert(new FileInfo(destination).Length > 1000, "Export is unexpectedly empty.");
                using var encoded = new MagickImage(destination);
                Assert(encoded.Width == final.Width && encoded.Height == final.Height, "Export dimensions changed.");
                // PNG may losslessly replace the standard sRGB ICC with its normative sRGB chunk.
                bool hasProfile = encoded.GetColorProfile() is not null;
                Assert(hasProfile || extension == ".png" && HasSrgbPngChunk(destination), "Export omitted its color-space declaration.");
                sample.ExportColorDeclarations[extension] = hasProfile ? "ICC profile" : "PNG sRGB chunk";
                using var pixels = encoded.GetPixels();
                byte[] roundTrip = pixels.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("Export could not be decoded.");
                if (extension == ".png") Assert(roundTrip.AsSpan().SequenceEqual(final.Pixels), "PNG round-trip was not pixel-exact.");
                else
                {
                    sample.JpegMeanAbsoluteError = MeanRgbError(final.Pixels, roundTrip);
                    Assert(sample.JpegMeanAbsoluteError < 15, $"JPEG round-trip error is excessive: {sample.JpegMeanAbsoluteError:F2}.");
                    DecodedImage reopened = await Images.LoadFullAsync(destination, CancellationToken.None);
                    Assert(reopened.Width == final.Width && reopened.Height == final.Height, "ImageFileService could not reopen its JPEG output.");
                }
            }
        }
        finally
        {
            sample.SourceSha256After = await FileHashAsync(path);
            Assert(before == sample.SourceSha256After, "Source file was modified.");
        }
    }

    private static EditSettings CombinedSettings() => new()
    {
        Exposure = 0.25, Temperature = 12, Tint = -5, Contrast = 8, Highlights = -12, Shadows = 8,
        Saturation = 12, Hue = 4, CurveShadows = -5, CurveMidtones = 8, CurveHighlights = 3,
        Denoise = 35, RotationQuarterTurns = 1, Straighten = 2, FlipHorizontal = true,
        Crop = new NormalizedRect(0.1, 0.1, 0.8, 0.8)
    };

    private static string BuildCube()
    {
        const int size = 17;
        var cube = new StringBuilder($"TITLE \"Integration style\"\nLUT_3D_SIZE {size}\n");
        for (int b = 0; b < size; b++)
        for (int g = 0; g < size; g++)
        for (int r = 0; r < size; r++)
            cube.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{r / 16.0 * 0.92 + 0.04} {g / 16.0 * 0.96 + 0.02} {b / 16.0 * 0.88 + 0.06}"));
        return cube.ToString();
    }

    private static void AssertNonBlank(byte[] pixels)
    {
        int minimum = 255, maximum = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            minimum = Math.Min(minimum, pixels[i]);
            maximum = Math.Max(maximum, pixels[i]);
            Assert(pixels[i + 3] == 255, "Opaque photo unexpectedly contains transparency.");
        }
        Assert(maximum - minimum > 50, "Decoded photo appears blank or invalid.");
    }

    private static double MeanRgbError(byte[] expected, byte[] actual)
    {
        Assert(expected.Length == actual.Length, "Decoded export buffer length differs.");
        long error = 0;
        for (int i = 0; i < expected.Length; i += 4)
            for (int channel = 0; channel < 3; channel++) error += Math.Abs(expected[i + channel] - actual[i + channel]);
        return error / (expected.Length / 4.0 * 3);
    }

    private static bool HasSrgbPngChunk(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        if (!header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        while (stream.Position + 12 <= stream.Length)
        {
            stream.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > stream.Length - stream.Position - 4) return false;
            if (header[4..].SequenceEqual("sRGB"u8)) return length == 1 && stream.ReadByte() is >= 0 and <= 3;
            stream.Seek((long)length + 4, SeekOrigin.Current);
        }
        return false;
    }

    private static async Task CheckAsync(string name, Func<Task> check)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await check();
            Checks.Add(new CheckResult(name, true, watch.Elapsed.TotalMilliseconds, null));
            Console.WriteLine($"PASS {name} ({watch.Elapsed.TotalSeconds:F2}s)");
        }
        catch (Exception exception)
        {
            Checks.Add(new CheckResult(name, false, watch.Elapsed.TotalMilliseconds, exception.ToString()));
            Console.Error.WriteLine($"FAIL {name}: {exception}");
        }
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (System.IO.Directory.Exists(Path.Combine(directory.FullName, "example")) &&
                System.IO.Directory.Exists(Path.Combine(directory.FullName, "src"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run from this repository or pass its root path as the first argument.");
    }

    private sealed record CheckResult(string Name, bool Passed, double Milliseconds, string? Error);

    private sealed class SampleResult(string fileName, long sourceBytes, string hash)
    {
        public string FileName { get; } = fileName;
        public long SourceBytes { get; } = sourceBytes;
        public string SourceSha256Before { get; } = hash;
        public string? SourceSha256After { get; set; }
        public string? DecodeMode { get; set; }
        public int PreviewWidth { get; set; }
        public int PreviewHeight { get; set; }
        public int FullWidth { get; set; }
        public int FullHeight { get; set; }
        public int ExportWidth { get; set; }
        public int ExportHeight { get; set; }
        public int MetadataFields { get; set; }
        public int MetadataWarnings { get; set; }
        public string[] MetadataGroups { get; set; } = [];
        public double[] WarmCombinedPreviewMs { get; set; } = [];
        public double JpegMeanAbsoluteError { get; set; }
        public Dictionary<string, string> ExportColorDeclarations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> TimingsMs { get; } = new(StringComparer.Ordinal);
    }
}
