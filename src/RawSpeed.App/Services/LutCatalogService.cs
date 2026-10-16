using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RawSpeed.App.Models;
using RawSpeed.Core;

namespace RawSpeed.App.Services;

/// <summary>
/// Enumerates original assets and bakes explicitly labelled, display-sRGB adaptations.
/// No image-sized work occurs during scanning; generated cubes are cached outside the install directory.
/// </summary>
public sealed class LutCatalogService
{
    public const string RapidRawCommit = "56aa54b1d16afab7f3ad53a32845c093545704b3";
    public const string CommunityCommit = "2e08bee4484a0a55db9b91553b19c96c926ef3cc";
    private const string AdapterVersion = "rawspeed-display-v1";
    private readonly string _presetsDirectory;
    private readonly string _cacheDirectory;
    private readonly object _cacheGate = new();
    private readonly List<string> _scanWarnings = [];

    public LutCatalogService(string? presetsDirectory = null, string? cacheDirectory = null)
    {
        _presetsDirectory = Path.GetFullPath(presetsDirectory ?? Path.Combine(AppContext.BaseDirectory, "presets"));
        _cacheDirectory = Path.GetFullPath(cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RawSpeed", "LutCache"));
    }

    public IReadOnlyList<string> ScanWarnings => _scanWarnings.AsReadOnly();

    public IReadOnlyList<FilterPreset> Scan()
    {
        _scanWarnings.Clear();
        var result = new List<FilterPreset>
        {
            BuiltIn("natural", "自然"), BuiltIn("film", "胶片"), BuiltIn("mono", "黑白")
        };
        if (!Directory.Exists(_presetsDirectory)) return result;
        foreach (string path in Directory.EnumerateFiles(_presetsDirectory, "*.cube").Order(StringComparer.OrdinalIgnoreCase))
            result.Add(new FilterPreset
            {
                Id = "cube:" + Path.GetFileName(path), Name = Path.GetFileNameWithoutExtension(path),
                Category = "本地 LUT", Kind = FilterPresetKind.Cube, OriginalPath = path, LutPath = path,
                Notes = "显示 sRGB 输入；强度可调。"
            });
        string films = Path.Combine(_presetsDirectory, "rapidraw", "film", "originals");
        if (Directory.Exists(films))
            foreach (string path in Directory.EnumerateFiles(films, "*.cube").Order(StringComparer.OrdinalIgnoreCase))
                result.Add(new FilterPreset
                {
                    Id = "rapidraw-film:" + Path.GetFileName(path), Name = Path.GetFileNameWithoutExtension(path),
                    Category = "RapidRAW 电影", Kind = FilterPresetKind.RapidRawFilm, OriginalPath = path,
                    Creator = "Andrea Volpato · spektrafilm", License = "CC BY-SA 4.0",
                    SourceUrl = $"https://github.com/CyberTimon/RapidRAW/tree/{RapidRawCommit}/src-tauri/resources/film_luts",
                    Notes = "原版 V-Log → sRGB LUT；在此使用显示 sRGB → 线性 RGB → V-Log 适配。与 RapidRAW 场景线性 RAW 管线不完全相同。"
                });
        string community = Path.Combine(_presetsDirectory, "rapidraw", "community-local", "originals");
        if (Directory.Exists(community))
            foreach (string path in Directory.EnumerateFiles(community, "*.rrpreset").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var document = ReadPreset(path);
                    JsonElement root = document.RootElement;
                    JsonElement preset = GetPreset(root);
                    JsonElement adjustments = preset.GetProperty("adjustments");
                    result.Add(new FilterPreset
                    {
                        Id = "rapidraw-community:" + Path.GetFileName(path),
                        Name = StringValue(preset, "name", Path.GetFileNameWithoutExtension(path)),
                        Category = "RapidRAW 社区", Kind = FilterPresetKind.RapidRawCommunity, OriginalPath = path,
                        Creator = StringValue(root, "creator", "Unknown"), License = "上游未提供独立许可 · 仅本地下载",
                        SourceUrl = $"https://github.com/CyberTimon/RapidRAW-Presets/blob/{CommunityCommit}/presets/{Uri.EscapeDataString(Path.GetFileName(path))}",
                        Notes = "完整原配方保留。颜色参数经独立算法近似适配为 sRGB LUT；空间效果和未支持项见下方，不承诺与 RapidRAW 逐像素一致。",
                        UnsupportedAdjustments = FindUnsupported(adjustments)
                    });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    _scanWarnings.Add($"{Path.GetFileName(path)}: {error.Message}");
                }
            }
        return result.AsReadOnly();
    }

    public CubeLut? LoadLut(FilterPreset preset, CancellationToken cancellationToken = default)
    {
        string? path = GetLutPath(preset, cancellationToken);
        return path is null ? null : CubeLut.Load(path);
    }

    /// <summary>Returns an existing portable/imported path or a content-addressed, atomically written cache path.</summary>
    public string? GetLutPath(FilterPreset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        cancellationToken.ThrowIfCancellationRequested();
        if (preset.Kind == FilterPresetKind.BuiltIn) return null;
        string original = preset.OriginalPath ?? throw new InvalidOperationException("The preset has no original file.");
        if (preset.Kind == FilterPresetKind.Cube) return original;
        // Read once: a file modified during generation cannot be cached under the previous file's hash.
        var info = new FileInfo(original);
        if (info.Length > 8 * 1024 * 1024) throw new FormatException("Preset files must be smaller than 8 MiB.");
        byte[] source = File.ReadAllBytes(original);
        string hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        string key = $"{AdapterVersion}-{preset.Kind}-{hash}";
        string cachePath = Path.Combine(_cacheDirectory, key + ".cube");
        lock (_cacheGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(cachePath))
            {
                try { _ = CubeLut.Load(cachePath); return cachePath; }
                catch (Exception error) when (error is FormatException or IOException) { /* Rebuild incomplete/corrupted caches. */ }
            }
            Directory.CreateDirectory(_cacheDirectory);
            Func<Vector3, Vector3> transform;
            if (preset.Kind == FilterPresetKind.RapidRawFilm)
            {
                var lut = CubeLut.Parse(new StringReader(Encoding.UTF8.GetString(source)));
                transform = rgb => lut.Transform(ToVLog(ToLinear(rgb.X)), ToVLog(ToLinear(rgb.Y)), ToVLog(ToLinear(rgb.Z)));
            }
            else if (preset.Kind == FilterPresetKind.RapidRawCommunity)
            {
                using var document = JsonDocument.Parse(source);
                var recipe = new ColorRecipe(GetPreset(document.RootElement).GetProperty("adjustments"));
                transform = recipe.Transform;
            }
            else throw new ArgumentOutOfRangeException(nameof(preset), "Unknown preset kind.");

            string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteCube(temporary, preset, hash, transform, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, cachePath, overwrite: true);
                if (preset.Kind == FilterPresetKind.RapidRawFilm)
                {
                    string license = Path.Combine(Path.GetDirectoryName(original)!, "SPEKTRAFILM_LICENSE.txt");
                    if (File.Exists(license)) File.Copy(license, Path.Combine(_cacheDirectory, "SPEKTRAFILM_LICENSE.txt"), true);
                    File.WriteAllText(Path.Combine(_cacheDirectory, "CHANGELOG.txt"),
                        "RawSpeed display adapter v1: sRGB inverse transfer → linear RGB → Panasonic V-Log → original Spektrafilm LUT.\n" +
                        "Original LUTs by Andrea Volpato, https://github.com/andreavolpato/spektrafilm.\n" +
                        "Adapted LUTs remain CC BY-SA 4.0. Original source files remain unchanged.\n", new UTF8Encoding(false));
                }
                return cachePath;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static FilterPreset BuiltIn(string key, string name) => new()
    {
        Id = "builtin:" + key, Name = name, Category = "内置", Kind = FilterPresetKind.BuiltIn,
        BuiltInKey = key, Notes = "RawSpeed 内置调整配方。"
    };

    private static JsonDocument ReadPreset(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 8 * 1024 * 1024) throw new FormatException("Preset exceeds the 8 MiB limit.");
        return JsonDocument.Parse(stream);
    }

    private static JsonElement GetPreset(JsonElement root)
    {
        var presets = root.GetProperty("presets");
        if (presets.ValueKind != JsonValueKind.Array || presets.GetArrayLength() != 1)
            throw new FormatException("A community catalog file must contain exactly one preset.");
        var preset = presets[0].GetProperty("preset");
        if (preset.GetProperty("adjustments").ValueKind != JsonValueKind.Object)
            throw new FormatException("Preset adjustments must be an object.");
        return preset;
    }

    private static string StringValue(JsonElement parent, string name, string fallback) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static float Number(JsonElement parent, string name, float fallback = 0) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number)
            ? number : fallback;

    private static readonly HashSet<string> ColorParameters = new(StringComparer.Ordinal)
    {
        "exposure", "brightness", "temperature", "tint", "contrast", "shadows", "highlights", "blacks", "whites",
        "saturation", "hue", "vibrance", "curves", "hsl", "colorGrading"
    };

    private static readonly HashSet<string> AuxiliaryParameters = new(StringComparer.Ordinal)
    {
        "grainSize", "grainRoughness", "vignetteFeather", "vignetteMidpoint", "vignetteRoundness", "sharpnessThreshold",
        "lutIntensity", "lutSize", "lutName", "curveMode", "pointCurves", "sectionVisibility", "showClipping",
        "negativeRedBalance", "negativeGreenBalance", "negativeBlueBalance", "filmBaseColor"
    };

    private static IReadOnlyList<string> FindUnsupported(JsonElement adjustments)
    {
        var result = new List<string>();
        foreach (var property in adjustments.EnumerateObject())
        {
            if (ColorParameters.Contains(property.Name) || AuxiliaryParameters.Contains(property.Name)) continue;
            if (property.Name == "toneMapper")
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() != "basic")
                    result.Add("toneMapper=" + property.Value.GetString());
            }
            else if (property.Name == "parametricCurve")
            {
                if (StringValue(adjustments, "curveMode", "point") == "parametric") result.Add("parametricCurve（使用已保存 curves 近似）");
            }
            else if (IsActive(property.Value)) result.Add(property.Name);
        }
        return result.AsReadOnly();
    }

    private static bool IsActive(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDouble(out double number) && Math.Abs(number) > 0.000001,
        JsonValueKind.True => true,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Object => value.EnumerateObject().Any(property => IsActive(property.Value)),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        _ => false
    };

    private static void WriteCube(string path, FilterPreset preset, string sourceHash,
        Func<Vector3, Vector3> transform, CancellationToken cancellationToken)
    {
        const int size = 33;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
        writer.WriteLine("# RawSpeed display-sRGB adaptation; not an unchanged RapidRAW export.");
        writer.WriteLine("# Source: " + preset.SourceUrl);
        writer.WriteLine("# Author: " + preset.Creator);
        writer.WriteLine("# License: " + preset.License);
        writer.WriteLine("# Original SHA256: " + sourceHash);
        writer.WriteLine("# Adapter: " + AdapterVersion);
        writer.WriteLine("# " + preset.Notes);
        writer.WriteLine("# " + preset.UnsupportedSummary);
        writer.WriteLine("TITLE \"" + preset.Name.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ') + " - sRGB adaptation\"");
        writer.WriteLine("LUT_3D_SIZE " + size);
        writer.WriteLine("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1");
        for (int b = 0; b < size; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int g = 0; g < size; g++)
                for (int r = 0; r < size; r++)
                {
                    Vector3 output = Vector3.Clamp(transform(new Vector3(r, g, b) / (size - 1)), Vector3.Zero, Vector3.One);
                    if (!float.IsFinite(output.X) || !float.IsFinite(output.Y) || !float.IsFinite(output.Z))
                        throw new FormatException("Preset produced non-finite RGB samples.");
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{output.X:G9} {output.Y:G9} {output.Z:G9}"));
                }
        }
    }

    private static float ToLinear(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    private static float ToVLog(float value) => value <= 0.01f ? 5.6f * value + 0.125f : 0.241514f * MathF.Log10(value + 0.00873f) + 0.598206f;
    private static float Unit(float value) => Math.Clamp(value, 0, 1);
    private static float Luma(Vector3 rgb) => Vector3.Dot(rgb, new Vector3(0.2126f, 0.7152f, 0.0722f));

    /// <summary>Independent, explicitly approximate color-only interpretation; not RapidRAW shader code.</summary>
    private sealed class ColorRecipe
    {
        private readonly float _exposure, _contrast, _shadows, _highlights, _blacks, _whites, _saturation, _hue, _vibrance;
        private readonly Vector3 _whiteBalance;
        private readonly float[][] _curves;
        private readonly Vector3[] _hsl = new Vector3[8];
        private readonly Vector3[] _grade = new Vector3[4];
        private readonly float _gradeBalance, _gradeBlending;
        private static readonly string[] HueNames = ["reds", "oranges", "yellows", "greens", "aquas", "blues", "purples", "magentas"];
        private static readonly float[] HueCenters = [0, 30, 60, 120, 180, 240, 270, 300, 360];

        public ColorRecipe(JsonElement settings)
        {
            _exposure = MathF.Pow(2, Math.Clamp(Number(settings, "exposure") + Number(settings, "brightness"), -10, 10));
            float temperature = Number(settings, "temperature") / 100, tint = Number(settings, "tint") / 100;
            _whiteBalance = new Vector3(MathF.Pow(2, temperature * 0.35f + tint * 0.12f), MathF.Pow(2, -tint * 0.24f), MathF.Pow(2, -temperature * 0.35f + tint * 0.12f));
            _contrast = Math.Clamp(1 + Number(settings, "contrast") / 100, 0, 3);
            _shadows = Number(settings, "shadows") / 100; _highlights = Number(settings, "highlights") / 100;
            _blacks = Number(settings, "blacks") / 100; _whites = Number(settings, "whites") / 100;
            _saturation = Math.Clamp(1 + Number(settings, "saturation") / 100, 0, 3);
            _hue = Number(settings, "hue"); _vibrance = Number(settings, "vibrance") / 100;
            settings.TryGetProperty("curves", out var curves);
            _curves = [Curve(curves, "luma"), Curve(curves, "red"), Curve(curves, "green"), Curve(curves, "blue")];
            if (settings.TryGetProperty("hsl", out var hsl))
                for (int i = 0; i < HueNames.Length; i++)
                    if (hsl.TryGetProperty(HueNames[i], out var band))
                        _hsl[i] = new Vector3(Number(band, "hue"), Number(band, "saturation"), Number(band, "luminance"));
            if (settings.TryGetProperty("colorGrading", out var grading))
            {
                string[] names = ["shadows", "midtones", "highlights", "global"];
                for (int i = 0; i < names.Length; i++)
                    if (grading.TryGetProperty(names[i], out var band))
                    {
                        Vector3 color = FromHsl(Number(band, "hue"), 1, 0.5f);
                        _grade[i] = (color - new Vector3(Luma(color))) * (Number(band, "saturation") / 100 * 0.22f)
                            + new Vector3(Number(band, "luminance") / 100 * 0.15f);
                    }
                _gradeBalance = Number(grading, "balance") / 100;
                _gradeBlending = Math.Clamp(Number(grading, "blending", 50) / 100, 0, 1);
            }
        }

        public Vector3 Transform(Vector3 rgb)
        {
            rgb *= _whiteBalance * _exposure;
            rgb = new Vector3(Tone(rgb.X), Tone(rgb.Y), Tone(rgb.Z));
            rgb = new Vector3(Lookup(_curves[1], Lookup(_curves[0], rgb.X)),
                Lookup(_curves[2], Lookup(_curves[0], rgb.Y)), Lookup(_curves[3], Lookup(_curves[0], rgb.Z)));
            var (hue, saturation, lightness) = ToHsl(rgb);
            int band = 0;
            while (band < 7 && hue >= HueCenters[band + 1]) band++;
            float weight = (hue - HueCenters[band]) / (HueCenters[band + 1] - HueCenters[band]);
            Vector3 selective = Vector3.Lerp(_hsl[band], _hsl[(band + 1) % 8], weight);
            hue += _hue + selective.X * 0.3f;
            saturation = Unit(saturation * Math.Max(0, 1 + selective.Y / 100));
            lightness = Unit(lightness + selective.Z / 100 * 0.3f * (1 - Math.Abs(2 * lightness - 1)));
            saturation = Unit(saturation * _saturation * Math.Max(0, 1 + _vibrance * (1 - saturation)));
            rgb = FromHsl(hue, saturation, lightness);
            float luminance = Unit(Luma(rgb) + _gradeBalance * 0.25f);
            float shadow = MathF.Pow(1 - luminance, 3 - 2 * _gradeBlending);
            float highlight = MathF.Pow(luminance, 3 - 2 * _gradeBlending);
            float middle = Math.Max(0, 1 - shadow - highlight);
            return Vector3.Clamp(rgb + _grade[0] * shadow + _grade[1] * middle + _grade[2] * highlight + _grade[3], Vector3.Zero, Vector3.One);
        }

        private float Tone(float value)
        {
            value = Unit(value);
            value += _shadows * 0.3f * (1 - value) * (1 - value) * MathF.Sqrt(value)
                + _highlights * 0.3f * value * value * MathF.Sqrt(1 - value);
            value += _blacks * 0.2f * MathF.Pow(1 - value, 4) + _whites * 0.2f * MathF.Pow(value, 4);
            return Unit((value - 0.5f) * _contrast + 0.5f);
        }

        private static float[] Curve(JsonElement curves, string channel)
        {
            var points = new List<Vector2>();
            if (curves.ValueKind == JsonValueKind.Object && curves.TryGetProperty(channel, out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var point in values.EnumerateArray()) points.Add(new Vector2(Unit(Number(point, "x") / 255), Unit(Number(point, "y") / 255)));
            if (points.Count < 2) points = [Vector2.Zero, Vector2.One];
            points = points.OrderBy(point => point.X).GroupBy(point => point.X).Select(group => group.Last()).ToList();
            var table = new float[1025];
            int segment = 0;
            for (int i = 0; i < table.Length; i++)
            {
                float x = i / 1024f;
                while (segment < points.Count - 2 && x > points[segment + 1].X) segment++;
                Vector2 first = points[segment], last = points[Math.Min(segment + 1, points.Count - 1)];
                float t = last.X > first.X ? Unit((x - first.X) / (last.X - first.X)) : 0;
                table[i] = first.Y + (last.Y - first.Y) * t;
            }
            return table;
        }

        private static float Lookup(float[] table, float value)
        {
            float index = Unit(value) * 1024;
            int lower = Math.Min((int)index, 1023);
            return table[lower] + (table[lower + 1] - table[lower]) * (index - lower);
        }

        private static (float Hue, float Saturation, float Lightness) ToHsl(Vector3 rgb)
        {
            float maximum = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z)), minimum = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
            float delta = maximum - minimum, lightness = (maximum + minimum) * 0.5f;
            if (delta < 0.000001f) return (0, 0, lightness);
            float hue = maximum == rgb.X ? (rgb.Y - rgb.Z) / delta : maximum == rgb.Y ? (rgb.Z - rgb.X) / delta + 2 : (rgb.X - rgb.Y) / delta + 4;
            return ((hue * 60 + 360) % 360, delta / Math.Max(0.000001f, 1 - Math.Abs(2 * lightness - 1)), lightness);
        }

        private static Vector3 FromHsl(float hue, float saturation, float lightness)
        {
            hue = ((hue % 360) + 360) % 360 / 60;
            float chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            float x = chroma * (1 - Math.Abs(hue % 2 - 1));
            Vector3 rgb = hue < 1 ? new(chroma, x, 0) : hue < 2 ? new(x, chroma, 0) : hue < 3 ? new(0, chroma, x)
                : hue < 4 ? new(0, x, chroma) : hue < 5 ? new(x, 0, chroma) : new(chroma, 0, x);
            return rgb + new Vector3(lightness - chroma * 0.5f);
        }
    }
}
