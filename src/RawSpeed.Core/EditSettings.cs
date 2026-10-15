namespace RawSpeed.Core;

/// <summary>Non-destructive adjustments. Color controls operate on decoded, display-referred RGB.</summary>
public sealed record EditSettings
{
    public double Exposure { get; init; }
    public double Temperature { get; init; }
    public double Tint { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Saturation { get; init; }
    public double Hue { get; init; }
    public double Denoise { get; init; }
    public double CurveShadows { get; init; }
    public double CurveMidtones { get; init; }
    public double CurveHighlights { get; init; }
    public int RotationQuarterTurns { get; init; }
    public double Straighten { get; init; }
    public bool FlipHorizontal { get; init; }
    public NormalizedRect? Crop { get; init; }
}

/// <summary>A crop in the oriented image, after clockwise rotation, flip and straightening.</summary>
public sealed record NormalizedRect(double X, double Y, double Width, double Height);

/// <summary>Contiguous RGBA8 pixels; histogram contains 256 red, 256 green, then 256 blue bins.</summary>
public sealed record RenderedImage(int Width, int Height, byte[] Pixels, int[] Histogram);
