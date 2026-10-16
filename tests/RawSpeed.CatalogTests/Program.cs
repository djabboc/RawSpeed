using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using RawSpeed.App.Models;
using RawSpeed.App.Services;
using RawSpeed.Core;

string root = args.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(args[0]);
string artifacts = Path.Combine(root, "artifacts", "catalog-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(artifacts);
int passed = 0;
var service = new LutCatalogService(Path.Combine(root, "presets"), Path.Combine(artifacts, "cache"));
var catalog = service.Scan();
var metrics = new List<object>();

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Test(string name, Action test)
{
    test(); passed++; Console.WriteLine("PASS " + name);
}

Test("catalog and source SHA256", () =>
{
    Check(service.ScanWarnings.Count == 0, string.Join("; ", service.ScanWarnings));
    Check(catalog.Take(3).Select(p => p.BuiltInKey).SequenceEqual(new[] { "natural", "film", "mono" }), "Built-ins must be first.");
    Check(catalog.Count(p => p.Kind == FilterPresetKind.RapidRawFilm) == 6, "Expected six actual film LUTs.");
    foreach (string relative in new[] { "film/sources.json", "community-sources.json" })
    {
        string manifestPath = Path.Combine(root, "presets", "rapidraw", relative);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string folder = relative.StartsWith("film", StringComparison.Ordinal) ? "film/originals" : "community-local/originals";
            string source = Path.Combine(root, "presets", "rapidraw", folder, file.GetProperty("file").GetString()!);
            if (!File.Exists(source) && !relative.StartsWith("film", StringComparison.Ordinal)) continue;
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Source hash mismatch: " + source);
        }
    }
});

Test("all original films and downloaded community recipes produce finite LUTs", () =>
{
    foreach (FilterPreset preset in catalog.Where(p => p.Kind is FilterPresetKind.RapidRawFilm or FilterPresetKind.RapidRawCommunity))
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(preset.OriginalPath!));
        var watch = Stopwatch.StartNew();
        var lut = service.LoadLut(preset)!;
        watch.Stop();
        foreach (var color in new[] { Vector3.Zero, new Vector3(0.18f), new Vector3(0.5f), Vector3.One, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            Vector3 output = lut.Transform(color.X, color.Y, color.Z);
            Check(float.IsFinite(output.X) && float.IsFinite(output.Y) && float.IsFinite(output.Z), "Non-finite sample: " + preset.Name);
        }
        Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(preset.OriginalPath!))), "Original source was modified.");
        metrics.Add(new { preset.Name, preset.Kind, GenerateMilliseconds = watch.Elapsed.TotalMilliseconds, preset.UnsupportedAdjustments });
    }
});

Test("film input conversion is baked within measured sampling tolerance", () =>
{
    float worst = 0;
    foreach (FilterPreset preset in catalog.Where(p => p.Kind == FilterPresetKind.RapidRawFilm))
    {
        var original = CubeLut.Load(preset.OriginalPath!);
        var adapted = service.LoadLut(preset)!;
        var random = new Random(1234);
        for (int i = 0; i < 1000; i++)
        {
            Vector3 rgb = new(random.NextSingle(), random.NextSingle(), random.NextSingle());
            Vector3 expected = Vector3.Clamp(original.Transform(Log(rgb.X), Log(rgb.Y), Log(rgb.Z)), Vector3.Zero, Vector3.One);
            Vector3 actual = adapted.Transform(rgb.X, rgb.Y, rgb.Z);
            Vector3 error = Vector3.Abs(expected - actual);
            worst = Math.Max(worst, Math.Max(error.X, Math.Max(error.Y, error.Z)));
        }
    }
    Console.WriteLine($"  Film max channel error against direct conversion: {worst:F6}");
    Check(worst < 0.025f, "33-cube film adaptation deviates too much from direct conversion.");
    metrics.Add(new { FilmMaximumChannelError = worst, SamplesPerFilm = 1000 });
});

Test("cache reuse, corruption recovery, cancellation", () =>
{
    FilterPreset preset = catalog.First(p => p.Kind == FilterPresetKind.RapidRawFilm);
    string path = service.GetLutPath(preset)!;
    DateTime stamp = File.GetLastWriteTimeUtc(path);
    Check(service.GetLutPath(preset) == path && File.GetLastWriteTimeUtc(path) == stamp, "Cache was rewritten without source change.");
    File.WriteAllText(path, "broken cache");
    Check(service.LoadLut(preset)!.Size == 33, "Corrupted cache was not repaired.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    bool cancelled = false;
    try { service.GetLutPath(preset, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Cancelled work must not generate a LUT.");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Temporary file leaked.");
});

Test("community piecewise curves, grayscale, unknown parameters and SHA invalidation", () =>
{
    string fixtureRoot = Path.Combine(artifacts, "fixture-presets");
    string original = Path.Combine(fixtureRoot, "rapidraw", "community-local", "originals", "test.rrpreset");
    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
    File.WriteAllText(original, """
        {"creator":"Test fixture","presets":[{"preset":{"name":"Curve fixture","adjustments":{
        "curves":{"luma":[{"x":255,"y":240},{"x":0,"y":20},{"x":128,"y":160}]},
        "clarity":12,"unknownFeature":7,"grainAmount":10}}}]}
        """);
    var fixture = new LutCatalogService(fixtureRoot, Path.Combine(artifacts, "fixture-cache"));
    var entry = fixture.Scan().Single(p => p.Kind == FilterPresetKind.RapidRawCommunity);
    Check(entry.UnsupportedAdjustments.Contains("clarity") && entry.UnsupportedAdjustments.Contains("unknownFeature") && entry.UnsupportedAdjustments.Contains("grainAmount"), "Active unsupported adjustments were hidden.");
    var curve = fixture.LoadLut(entry)!;
    float expected = (20 + (160 - 20) * (127.5f / 128)) / 255;
    Vector3 output = curve.Transform(0.5f, 0.5f, 0.5f);
    Check(Math.Abs(output.X - expected) < 0.001f && Math.Abs(output.Y - expected) < 0.001f, "Curve point order or interpolation is wrong.");
    string firstPath = fixture.GetLutPath(entry)!;
    File.WriteAllText(original, """
        {"creator":"Test fixture","presets":[{"preset":{"name":"Monochrome fixture","adjustments":{"saturation":-100}}}]}
        """);
    string changedPath = fixture.GetLutPath(entry)!;
    Check(firstPath != changedPath, "Changed source reused previous LUT.");
    var monochrome = fixture.LoadLut(entry)!.Transform(0.8f, 0.2f, 0.1f);
    Check(Math.Abs(monochrome.X - monochrome.Y) < 0.00001f && Math.Abs(monochrome.Y - monochrome.Z) < 0.00001f, "Saturation -100 is not grayscale.");
    foreach (var blackAndWhite in catalog.Where(p => p.Name is "AVOR - Dynamic Black and White" or "Dramatic B&W"))
    {
        var sample = service.LoadLut(blackAndWhite)!.Transform(0.7f, 0.35f, 0.2f);
        Check(Math.Abs(sample.X - sample.Y) < 0.00001f && Math.Abs(sample.Y - sample.Z) < 0.00001f,
            "Original community monochrome recipe is not grayscale: " + blackAndWhite.Name);
    }
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(original)!, "broken.rrpreset"), "{bad");
    Check(fixture.Scan().Count == 4 && fixture.ScanWarnings.Count == 1, "Malformed catalog entry must be reported without losing valid presets.");
});

Test("rating persistence, normalized path aliases, invalid values, malformed recovery", () =>
{
    string directory = Path.Combine(artifacts, "ratings");
    var first = new RatingStore(directory);
    var second = new RatingStore(directory);
    string source = Path.Combine(root, "example", "nikon.NEF");
    Check(first.GetRating(source) == 0, "New rating should be zero.");
    first.SetRating(source, 4);
    Check(second.GetRating(source.ToUpperInvariant()) == 4, "Case-insensitive persistence failed.");
    Check(second.GetRating(Path.Combine(root, "example", "..", "example", "nikon.NEF")) == 4, "Full-path normalization failed.");
    second.SetRating(source, 0);
    Check(first.GetRating(source) == 0, "Clearing a rating failed.");
    bool rejected = false;
    try { first.SetRating(source, 6); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "Invalid rating was accepted.");
    File.WriteAllText(Path.Combine(directory, "ratings.json"), "malformed");
    Check(first.GetRating(source) == 0, "Malformed ratings should recover to an empty view.");
    first.SetRating(source, 5);
    Check(second.GetRating(source) == 5 && Directory.EnumerateFiles(directory, "ratings.json.corrupt-*").Any(), "Malformed data was not preserved and repaired.");
    Parallel.For(0, 30, i => new RatingStore(directory).SetRating(Path.Combine(root, $"source-{i}.jpg"), i % 5 + 1));
    for (int i = 0; i < 30; i++) Check(first.GetRating(Path.Combine(root, $"source-{i}.jpg")) == i % 5 + 1, "Concurrent rating update was lost.");
});

File.WriteAllText(Path.Combine(artifacts, "results.json"), JsonSerializer.Serialize(new { Passed = passed, CatalogCount = catalog.Count, metrics }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{passed} tests passed; {catalog.Count} catalog entries. Results: {artifacts}");

static float Log(float srgb)
{
    float linear = srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
    return linear <= 0.01f ? 5.6f * linear + 0.125f : 0.241514f * MathF.Log10(linear + 0.00873f) + 0.598206f;
}
