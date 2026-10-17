using RawSpeed.Core;

namespace RawSpeed.App.Models;

public sealed record EditRecipe(int Version, EditSettings Settings, string? LutPath, double LutStrength,
    LutSourceReference? LutSource = null)
{
    public const int CurrentVersion = 2;
    public static EditRecipe Default => new(CurrentVersion, new EditSettings(), null, 1);
}
