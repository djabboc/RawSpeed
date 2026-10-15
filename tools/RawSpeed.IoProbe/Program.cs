using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using RawSpeed.Imaging;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
string output = Path.Combine(root, "artifacts", "io", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
var service = new ImageFileService();
var results = new List<object>();
Console.WriteLine(MagickNET.Version);
Console.WriteLine(MagickNET.Features);
Console.WriteLine(MagickNET.Delegates);

foreach (string source in Directory.EnumerateFiles(Path.Combine(root, "example")))
{
    byte[] before = SHA256.HashData(File.ReadAllBytes(source));
    var timer = Stopwatch.StartNew();
    DecodedImage? embedded = await service.TryLoadEmbeddedPreviewAsync(source, 1600, CancellationToken.None);
    long embeddedMs = timer.ElapsedMilliseconds;
    if (embedded is not null)
    {
        await service.ExportAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(source) + "-embedded.jpg"),
            embedded.Width, embedded.Height, embedded.Pixels, 92, CancellationToken.None);
        Assert(embedded.DecodeMode.StartsWith("Embedded", StringComparison.Ordinal), "Embedded decode mode must be explicit.");
    }
    timer.Restart();
    DecodedImage preview = await service.LoadPreviewAsync(source, 1600, CancellationToken.None);
    long previewMs = timer.ElapsedMilliseconds;
    Assert(preview.Pixels.Length == checked(preview.Width * preview.Height * 4), "Preview RGBA size mismatch.");
    Assert(Math.Max(preview.Width, preview.Height) <= 1600, "Preview is too large.");
    Assert(preview.Pixels.Any(p => p != 0), "Preview is blank.");

    string exported = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + "-preview.jpg");
    await service.ExportAsync(exported, preview.Width, preview.Height, preview.Pixels, 92, CancellationToken.None);
    using (var roundTrip = new MagickImage(exported))
    {
        Assert(roundTrip.Width == preview.Width && roundTrip.Height == preview.Height, "Export dimensions mismatch.");
        Assert(roundTrip.GetColorProfile() is not null, "Export has no ICC profile.");
    }

    timer.Restart();
    DecodedImage full = await service.LoadFullAsync(source, CancellationToken.None);
    long fullMs = timer.ElapsedMilliseconds;
    Assert(full.Width == preview.OriginalWidth && full.Height == preview.OriginalHeight, "Full dimensions mismatch.");
    Assert(full.Pixels.Length == checked(full.Width * full.Height * 4), "Full RGBA size mismatch.");
    Assert(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Source file was modified.");
    if (args.Contains("--full-png", StringComparer.Ordinal))
    {
        string fullPng = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + "-full.png");
        timer.Restart();
        await service.ExportAsync(fullPng, full.Width, full.Height, full.Pixels, 95, CancellationToken.None);
        Console.WriteLine($"Full PNG export: {Path.GetFileName(source)} {timer.ElapsedMilliseconds} ms");
        using var png = new MagickImage(fullPng);
        using var pngPixels = png.GetPixels();
        Assert(full.Pixels.AsSpan().SequenceEqual(pngPixels.ToByteArray(PixelMapping.RGBA)), "Full PNG lost pixel values.");
    }
    results.Add(new { file = Path.GetFileName(source), preview.Width, preview.Height, preview.OriginalWidth, preview.OriginalHeight, preview.DecodeMode, embeddedMs, hasEmbedded = embedded is not null, previewMs, fullMs, bytes = full.Pixels.Length });
    Console.WriteLine(JsonSerializer.Serialize(results[^1]));
}

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        await service.LoadPreviewAsync(Path.Combine(root, "example", "nikon.NEF"), 1600, cancelled.Token);
        throw new Exception("Cancellation was not honored.");
    }
    catch (OperationCanceledException) { }
}

string pixelOutput = Path.Combine(output, "rgba.png");
byte[] fixture = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
await service.ExportAsync(pixelOutput, 2, 2, fixture, 95, CancellationToken.None);
using (var pixelsImage = new MagickImage(pixelOutput))
using (var pixels = pixelsImage.GetPixels())
    Assert(fixture.AsSpan().SequenceEqual(pixels.ToByteArray(PixelMapping.RGBA)), "PNG lossless round trip mismatch.");
try
{
    await service.ExportAsync(pixelOutput, 2, 2, fixture, 95, CancellationToken.None);
    throw new Exception("Existing export destination was overwritten.");
}
catch (IOException) { }
await service.ExportAsync(Path.Combine(output, "rgba.tiff"), 2, 2, fixture, 95, CancellationToken.None);
using (var tiff = new MagickImage(Path.Combine(output, "rgba.tiff")))
using (var tiffPixels = tiff.GetPixels())
    Assert(fixture.AsSpan().SequenceEqual(tiffPixels.ToByteArray(PixelMapping.RGBA)), "TIFF lossless round trip mismatch.");

// Orientation 6 must be applied once, including when JPEG uses a reduced IDCT preview.
string orientationPath = Path.Combine(output, "orientation-6.jpg");
byte[] orientationPixels = new byte[120 * 80 * 4];
for (int y = 0; y < 80; y++)
for (int x = 0; x < 120; x++)
{
    int offset = (y * 120 + x) * 4;
    orientationPixels[offset + (x < 60 ? 0 : 2)] = 255;
    orientationPixels[offset + 3] = 255;
}
using (var image = new MagickImage())
{
    image.ReadPixels(orientationPixels, new PixelReadSettings(120, 80, StorageType.Char, PixelMapping.RGBA));
    image.Quality = 100;
    image.Orientation = Orientation.RightTop;
    var exif = new ExifProfile();
    exif.SetValue(ExifTag.Orientation, (ushort)6);
    image.SetProfile(exif);
    image.Write(orientationPath);
}
var oriented = await service.LoadFullAsync(orientationPath, CancellationToken.None);
Assert(oriented.Width == 80 && oriented.Height == 120, "EXIF orientation did not swap axes.");
Assert(oriented.Pixels[(30 * 80 + 40) * 4] > 200, "EXIF orientation rotated in the wrong direction.");
Assert(oriented.Pixels[(90 * 80 + 40) * 4 + 2] > 200, "EXIF orientation rotated in the wrong direction.");
var orientedPreview = await service.LoadPreviewAsync(orientationPath, 60, CancellationToken.None);
Assert(orientedPreview.Width == 40 && orientedPreview.Height == 60 && orientedPreview.OriginalWidth == 80
    && orientedPreview.OriginalHeight == 120, "Reduced JPEG preview lost oriented original dimensions.");

// The same color encoded with Adobe RGB must return close to the original sRGB pixel.
string colorPath = Path.Combine(output, "adobe-rgb.jpg");
using (var image = new MagickImage(new MagickColor("#6e9ac8"), 64, 64))
{
    image.SetProfile(ColorProfiles.SRGB);
    image.TransformColorSpace(ColorProfiles.AdobeRGB1998);
    image.Quality = 100;
    image.Write(colorPath);
}
var colorConverted = await service.LoadFullAsync(colorPath, CancellationToken.None);
Assert(Math.Abs(colorConverted.Pixels[0] - 110) <= 4 && Math.Abs(colorConverted.Pixels[1] - 154) <= 4
    && Math.Abs(colorConverted.Pixels[2] - 200) <= 4, "Embedded ICC profile did not transform to sRGB correctly.");
var smallPreview = await service.LoadPreviewAsync(colorPath, 1600, CancellationToken.None);
Assert(smallPreview.Width == 64 && smallPreview.Height == 64, "JPEG preview must not enlarge a smaller source.");

string invalidPath = Path.Combine(output, "invalid.jpg");
File.WriteAllText(invalidPath, "This is not a JPEG.");
try
{
    await service.LoadFullAsync(invalidPath, CancellationToken.None);
    throw new Exception("Malformed input was accepted.");
}
catch (MagickException) { }

File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"IO probe passed. Artifacts: {output}");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}
