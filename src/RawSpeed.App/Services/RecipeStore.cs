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
        return recipe;
    }

    private static void Validate(EditRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Version != 1 || recipe.Settings is null) throw new InvalidDataException("不支持的编辑配方版本。");
        if (!double.IsFinite(recipe.LutStrength) || recipe.LutStrength is < 0 or > 1) throw new InvalidDataException("滤镜强度无效。");
        ImageProcessor.ValidateSettings(recipe.Settings);
    }
}
