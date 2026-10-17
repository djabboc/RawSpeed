using System.IO;
using System.Text.Json;
using RawSpeed.App.Models;
using RawSpeed.Core;

namespace RawSpeed.App.Services;

public static class RecipeStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string GetPath(string source) => source + ".rawspeed.json";

    public static async Task SaveAsync(string source, EditRecipe recipe)
    {
        Validate(recipe);
        recipe = Upgrade(recipe);
        var path = GetPath(source);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(recipe, Options));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<EditRecipe?> LoadAsync(string source)
    {
        var path = GetPath(source);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("编辑配方超过 1 MB 限制。");
        var recipe = JsonSerializer.Deserialize<EditRecipe>(await File.ReadAllTextAsync(path), Options)
            ?? throw new InvalidDataException("编辑配方内容为空。");
        Validate(recipe);
        return Upgrade(recipe);
    }

    // Version 1 has no source identity. Its LUT path is retained until the catalog can
    // identify an old generated cache by kind/hash, or the user explicitly replaces it.
    private static EditRecipe Upgrade(EditRecipe recipe) => recipe with { Version = EditRecipe.CurrentVersion };

    private static void Validate(EditRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Version is not (1 or EditRecipe.CurrentVersion) || recipe.Settings is null)
            throw new InvalidDataException("不支持的编辑配方版本。");
        if (recipe.LutSource is { } source)
        {
            if (recipe.Version == 1 || recipe.LutPath is not null ||
                string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name) ||
                string.IsNullOrWhiteSpace(source.OriginalPath) || !Path.IsPathFullyQualified(source.OriginalPath) ||
                source.Kind is not (FilterPresetKind.Cube or FilterPresetKind.RapidRawFilm or FilterPresetKind.RapidRawCommunity) ||
                source.SourceSha256 is not { Length: 64 } || !source.SourceSha256.All(Uri.IsHexDigit) ||
                (source.Kind == FilterPresetKind.Cube ? source.AdapterVersion is not null : string.IsNullOrWhiteSpace(source.AdapterVersion)))
                throw new InvalidDataException("滤镜来源标识无效。");
        }
        if (!double.IsFinite(recipe.LutStrength) || recipe.LutStrength is < 0 or > 1) throw new InvalidDataException("滤镜强度无效。");
        ImageProcessor.ValidateSettings(recipe.Settings);
    }
}
