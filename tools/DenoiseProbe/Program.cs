using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;
using RawSpeed.Core;
using RawSpeed.Imaging;

// An observation tool, not a quality benchmark: the supplied photograph has no clean ground truth.
string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
string path = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, "example", "nikon.NEF");
string output = Path.Combine(root, "artifacts", "denoise", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
Directory.CreateDirectory(output);
var service = new ImageFileService();
byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(path));
var stopwatch = Stopwatch.StartNew();
DecodedImage source = await service.LoadFullAsync(path, CancellationToken.None);
double decodeMs = stopwatch.Elapsed.TotalMilliseconds;
Console.WriteLine($"Decoded {Path.GetFileName(path)}: {source.Width}x{source.Height}, {decodeMs:F1} ms.");
var noiseSettings = new EditSettings { LuminanceDenoise = 70, ColorDenoise = 85, DenoiseDetail = 50 };
var displaySettings = new EditSettings { Exposure = 2 };

using var previewImage = AsMagick(source.Pixels, source.Width, source.Height);
previewImage.Resize(new MagickGeometry(1600, 1600));
byte[] preview;
using (var pixels = previewImage.GetPixels()) preview = pixels.ToByteArray(PixelMapping.RGBA)!;
int previewWidth = (int)previewImage.Width, previewHeight = (int)previewImage.Height;
NoiseReducer.Reduce(preview, previewWidth, previewHeight, noiseSettings); // JIT and pool warm-up.
double[] previewTimes = new double[5];
for (int i = 0; i < previewTimes.Length; i++)
{
    stopwatch.Restart();
    NoiseReducer.Reduce(preview, previewWidth, previewHeight, noiseSettings);
    previewTimes[i] = stopwatch.Elapsed.TotalMilliseconds;
}
double[] fullTimes = new double[3];
byte[] denoised = [];
for (int i = 0; i < fullTimes.Length; i++)
{
    stopwatch.Restart();
    denoised = NoiseReducer.Reduce(source.Pixels, source.Width, source.Height, noiseSettings);
    fullTimes[i] = stopwatch.Elapsed.TotalMilliseconds;
}
byte[] before = ImageProcessor.Render(source.Pixels, source.Width, source.Height, displaySettings).Pixels;
byte[] after = ImageProcessor.Render(denoised, source.Width, source.Height, displaySettings).Pixels;
var positions = new (string Name, double X, double Y)[]
{
    ("sky", 0.26, 0.20), ("shadow", 0.16, 0.64), ("detail", 0.59, 0.55)
};
int size = Math.Min(512, Math.Min(source.Width, source.Height));
int sheetWidth = size * 2, rowHeight = size + 52;
byte[] sheet = new byte[sheetWidth * rowHeight * positions.Length * 4];
for (int i = 3; i < sheet.Length; i += 4) sheet[i] = 255;
var metrics = new List<object>();
var regions = new List<(string Name, int X, int Y)>();
for (int row = 0; row < positions.Length; row++)
{
    var position = positions[row];
    int x = Math.Clamp((int)Math.Round(position.X * source.Width), 0, source.Width - size);
    int y = Math.Clamp((int)Math.Round(position.Y * source.Height), 0, source.Height - size);
    regions.Add((position.Name, x, y));
    byte[] originalCrop = Crop(before, source.Width, x, y, size);
    byte[] processedCrop = Crop(after, source.Width, x, y, size);
    await service.ExportAsync(Path.Combine(output, position.Name + "-before.png"), size, size, originalCrop, 95, CancellationToken.None);
    await service.ExportAsync(Path.Combine(output, position.Name + "-after.png"), size, size, processedCrop, 95, CancellationToken.None);
    for (int cropY = 0; cropY < size; cropY++)
    {
        int target = ((row * rowHeight + 52 + cropY) * sheetWidth) * 4;
        Buffer.BlockCopy(originalCrop, cropY * size * 4, sheet, target, size * 4);
        Buffer.BlockCopy(processedCrop, cropY * size * 4, sheet, target + size * 4, size * 4);
    }
    metrics.Add(new { position.Name, x, y, width = size, height = size,
        before = Measure(originalCrop, size), after = Measure(processedCrop, size) });
}
using (var contact = AsMagick(sheet, sheetWidth, rowHeight * positions.Length))
{
    var captions = new Drawables().Font("Arial").FontPointSize(19).FillColor(MagickColors.White);
    for (int row = 0; row < positions.Length; row++)
    {
        captions.Text(12, row * rowHeight + 32, $"{positions[row].Name} | before | +2 EV");
        captions.Text(size + 12, row * rowHeight + 32, "after | luma 70 / color 85 / detail 50");
    }
    captions.Draw(contact);
    contact.Write(Path.Combine(output, "comparison.png"));
}
using (var context = AsMagick(before, source.Width, source.Height))
{
    context.Resize(new MagickGeometry(1200, 1200));
    double sx = context.Width / (double)source.Width, sy = context.Height / (double)source.Height;
    var boxes = new Drawables().Font("Arial").FontPointSize(20).StrokeColor(MagickColors.Yellow).StrokeWidth(2).FillColor(MagickColors.Transparent);
    foreach (var region in regions) boxes.Rectangle(region.X * sx, region.Y * sy, (region.X + size) * sx, (region.Y + size) * sy);
    boxes.FillColor(MagickColors.Yellow).StrokeWidth(0);
    foreach (var region in regions) boxes.Text(region.X * sx, Math.Max(22, region.Y * sy - 5), region.Name);
    boxes.Draw(context);
    context.Write(Path.Combine(output, "context.png"));
}
if (!sourceHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
    throw new InvalidOperationException("The source file changed during evaluation.");
Array.Sort(previewTimes); Array.Sort(fullTimes);
var report = new
{
    source = Path.GetFileName(path), sha256 = Convert.ToHexString(sourceHash), source.Width, source.Height,
    source.DecodeMode, decodeMs, noiseSettings, displaySettings,
    timing = new { previewWidth, previewHeight, previewMedianMs = previewTimes[2], fullMedianMs = fullTimes[1],
        previewRuns = previewTimes, fullRuns = fullTimes },
    environment = new { logicalProcessors = Environment.ProcessorCount, framework = Environment.Version.ToString(), os = Environment.OSVersion.ToString() },
    regions = metrics,
    limitation = "No clean ground truth: local high-frequency metrics mix noise with photograph texture. They are proxies, not PSNR/SSIM, quality scores, or comparisons with DxO/AI products. Both sides use the same +2 EV display adjustment after source-coordinate denoising. PNG crops are 1:1 source pixels."
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "report.json"), json);
Console.WriteLine($"Noise medians: preview {previewTimes[2]:F1} ms; full {fullTimes[1]:F1} ms.");
Console.WriteLine(output);

static MagickImage AsMagick(byte[] rgba, int width, int height)
{
    var image = new MagickImage();
    image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
    image.ColorSpace = ColorSpace.sRGB;
    image.SetProfile(ColorProfiles.SRGB);
    image.Depth = 8;
    return image;
}

static byte[] Crop(byte[] rgba, int width, int x, int y, int size)
{
    var output = new byte[size * size * 4];
    for (int row = 0; row < size; row++)
        Buffer.BlockCopy(rgba, ((y + row) * width + x) * 4, output, row * size * 4, size * 4);
    return output;
}

static object Measure(byte[] rgba, int width)
{
    double lumaDifference = 0, chromaDifference = 0, mean = 0;
    int samples = 0;
    for (int y = 1; y < width - 1; y++)
    for (int x = 1; x < width - 1; x++)
    {
        int i = (y * width + x) * 4;
        var center = YCoCg(rgba, i);
        var left = YCoCg(rgba, i - 4);
        var right = YCoCg(rgba, i + 4);
        var top = YCoCg(rgba, i - width * 4);
        var bottom = YCoCg(rgba, i + width * 4);
        double dy = center.Y - (left.Y + right.Y + top.Y + bottom.Y) / 4;
        double dc = center.Co - (left.Co + right.Co + top.Co + bottom.Co) / 4;
        double dg = center.Cg - (left.Cg + right.Cg + top.Cg + bottom.Cg) / 4;
        lumaDifference += dy * dy;
        chromaDifference += (dc * dc + dg * dg) / 2;
        mean += center.Y; samples++;
    }
    return new { meanLuma = mean / samples, lumaHighpassRms = Math.Sqrt(lumaDifference / samples),
        chromaHighpassRms = Math.Sqrt(chromaDifference / samples) };
}

static (double Y, double Co, double Cg) YCoCg(byte[] rgba, int i) =>
    ((rgba[i] + 2 * rgba[i + 1] + rgba[i + 2]) / 4d, (rgba[i] - rgba[i + 2]) / 2d,
        rgba[i + 1] - (rgba[i] + rgba[i + 2]) / 2d);
