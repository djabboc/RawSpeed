namespace RawSpeed.App.Models;

/// <summary>A catalog entry. Original files and adapted display LUTs are deliberately distinct.</summary>
public sealed record FilterPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required FilterPresetKind Kind { get; init; }
    public string Creator { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string License { get; init; } = "";
    public string? OriginalPath { get; init; }
    public string? LutPath { get; init; }
    public string? BuiltInKey { get; init; }
    public string Notes { get; init; } = "";
    public IReadOnlyList<string> UnsupportedAdjustments { get; init; } = [];
    public string SearchText => $"{Name} {Category} {Creator}";
    public string UnsupportedSummary => UnsupportedAdjustments.Count == 0
        ? "" : "未实现：" + string.Join("、", UnsupportedAdjustments);
}

public enum FilterPresetKind
{
    BuiltIn,
    Cube,
    RapidRawFilm,
    RapidRawCommunity
}
