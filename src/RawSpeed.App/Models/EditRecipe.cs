using RawSpeed.Core;

namespace RawSpeed.App.Models;

public sealed record EditRecipe(int Version, EditSettings Settings, string? LutPath, double LutStrength)
{
    public static EditRecipe Default => new(1, new EditSettings(), null, 1);
}
