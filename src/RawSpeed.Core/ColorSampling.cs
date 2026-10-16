namespace RawSpeed.Core;

public readonly record struct NeutralBalance(double Temperature, double Tint);

public static class ColorSampling
{
    /// <summary>Estimates absolute relative-white-balance controls from an opaque, neutral source-image patch.</summary>
    /// <remarks>Use decoded source pixels and ImageGeometry to map a preview click. This is not a Kelvin estimate.</remarks>
    public static NeutralBalance EstimateNeutralBalance(byte[] rgba, int width, int height, int x, int y, int radius = 4)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4 || rgba.Length != (long)width * height * 4)
            throw new ArgumentException("Invalid RGBA dimensions.", nameof(rgba));
        if (x < 0 || x >= width || y < 0 || y >= height || radius is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(x));
        double red = 0, green = 0, blue = 0; int count = 0;
        for (int row = Math.Max(0, y - radius); row <= Math.Min(height - 1, y + radius); row++)
        for (int column = Math.Max(0, x - radius); column <= Math.Min(width - 1, x + radius); column++)
        {
            int index = (row * width + column) * 4;
            if (rgba[index + 3] < 128 || Math.Min(rgba[index], Math.Min(rgba[index + 1], rgba[index + 2])) < 4 ||
                Math.Max(rgba[index], Math.Max(rgba[index + 1], rgba[index + 2])) > 250) continue;
            red += ToLinear(rgba[index]); green += ToLinear(rgba[index + 1]); blue += ToLinear(rgba[index + 2]); count++;
        }
        if (count == 0) throw new ArgumentException("Choose a neutral patch that is neither clipped nor nearly black.");
        double temperature = Math.Log2(blue / red) / 0.007;
        double neutral = Math.Sqrt(red * blue);
        double tint = Math.Log2(green / neutral) / 0.0025;
        return new NeutralBalance(Math.Clamp(temperature, -100, 100), Math.Clamp(tint, -100, 100));
    }

    private static double ToLinear(byte value)
    {
        double s = value / 255d;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
