using System.Globalization;
using System.Numerics;

namespace RawSpeed.Core;

/// <summary>An immutable 3D .cube LUT, using red-fastest storage and trilinear interpolation.</summary>
public sealed class CubeLut
{
    private readonly float[] _values;
    private readonly Vector3 _domainScale;

    private CubeLut(int size, string? title, Vector3 domainMinimum, Vector3 domainMaximum, float[] values)
    {
        Size = size;
        Title = title;
        DomainMinimum = domainMinimum;
        DomainMaximum = domainMaximum;
        _domainScale = new Vector3(size - 1) / (domainMaximum - domainMinimum);
        _values = values;
    }

    public int Size { get; }
    public string? Title { get; }
    public Vector3 DomainMinimum { get; }
    public Vector3 DomainMaximum { get; }

    /// <summary>Loads a 2–128 point 3D cube. 1D/shaper cubes are deliberately rejected.</summary>
    public static CubeLut Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 128L * 1024 * 1024)
            throw new FormatException("The LUT exceeds the 128 MiB file limit.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    /// <summary>Parses a cube from text. The caller owns the reader.</summary>
    public static CubeLut Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? title = null;
        var minimum = Vector3.Zero;
        var maximum = Vector3.One;
        float[]? values = null;
        int size = 0, count = 0, lineNumber = 0;
        bool seenMinimum = false, seenMaximum = false;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            if (raw.Length > 4096 || lineNumber > 2_100_000)
                throw Error("LUT text exceeds the supported limits", lineNumber);
            var line = raw.AsSpan().Trim();
            if (lineNumber == 1 && line.Length > 0 && line[0] == '\uFEFF') line = line[1..].TrimStart();
            if (line.IsEmpty || line[0] == '#') continue;
            if (line.StartsWith("TITLE", StringComparison.Ordinal) && (line.Length == 5 || char.IsWhiteSpace(line[5])))
            {
                title = line[5..].Trim().Trim('"').ToString();
                continue;
            }
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment].TrimEnd();
            string[] parts = line.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0])
            {
                case "LUT_3D_SIZE":
                    if (values is not null || parts.Length != 2 ||
                        !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 2 or > 128)
                        throw Error("LUT_3D_SIZE must appear once and be between 2 and 128", lineNumber);
                    values = new float[checked(size * size * size * 3)];
                    break;
                case "DOMAIN_MIN":
                    if (seenMinimum || count != 0) throw Error("DOMAIN_MIN must appear once before samples", lineNumber);
                    minimum = ReadVector(parts, 1, lineNumber);
                    seenMinimum = true;
                    break;
                case "DOMAIN_MAX":
                    if (seenMaximum || count != 0) throw Error("DOMAIN_MAX must appear once before samples", lineNumber);
                    maximum = ReadVector(parts, 1, lineNumber);
                    seenMaximum = true;
                    break;
                case "LUT_1D_SIZE":
                    throw Error("1D and combined shaper LUTs are not supported; export a 3D .cube LUT", lineNumber);
                default:
                    if (values is null) throw Error("Expected LUT_3D_SIZE before sample values", lineNumber);
                    if (count >= values.Length) throw Error("Too many LUT samples", lineNumber);
                    var sample = ReadVector(parts, 0, lineNumber);
                    values[count++] = sample.X;
                    values[count++] = sample.Y;
                    values[count++] = sample.Z;
                    break;
            }
        }
        if (values is null || count != values.Length)
            throw Error("The sample count does not match LUT_3D_SIZE", lineNumber);
        var domain = maximum - minimum;
        if (!float.IsFinite(domain.X) || !float.IsFinite(domain.Y) || !float.IsFinite(domain.Z) ||
            domain.X <= 0 || domain.Y <= 0 || domain.Z <= 0)
            throw Error("Each DOMAIN_MAX value must be greater than DOMAIN_MIN", lineNumber);
        Vector3 scale = new Vector3(size - 1) / domain;
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z))
            throw Error("The LUT domain is too narrow for finite interpolation", lineNumber);
        return new CubeLut(size, title, minimum, maximum, values);
    }

    public Vector3 Transform(float red, float green, float blue)
    {
        if (!float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue))
            throw new ArgumentOutOfRangeException(nameof(red), "LUT inputs must be finite.");
        return TransformUnchecked(red, green, blue);
    }

    internal Vector3 TransformUnchecked(float red, float green, float blue)
    {
        float r = Math.Clamp((red - DomainMinimum.X) * _domainScale.X, 0, Size - 1);
        float g = Math.Clamp((green - DomainMinimum.Y) * _domainScale.Y, 0, Size - 1);
        float b = Math.Clamp((blue - DomainMinimum.Z) * _domainScale.Z, 0, Size - 1);
        int r0 = Math.Min((int)r, Size - 2), g0 = Math.Min((int)g, Size - 2), b0 = Math.Min((int)b, Size - 2);
        float fr = r - r0, fg = g - g0, fb = b - b0;
        int index = ((b0 * Size + g0) * Size + r0) * 3;
        int row = Size * 3, plane = Size * row;
        Vector3 low = Vector3.Lerp(Vector3.Lerp(Read(index), Read(index + 3), fr),
            Vector3.Lerp(Read(index + row), Read(index + row + 3), fr), fg);
        Vector3 high = Vector3.Lerp(Vector3.Lerp(Read(index + plane), Read(index + plane + 3), fr),
            Vector3.Lerp(Read(index + plane + row), Read(index + plane + row + 3), fr), fg);
        return Vector3.Lerp(low, high, fb);
    }

    private Vector3 Read(int index) => new(_values[index], _values[index + 1], _values[index + 2]);

    private static Vector3 ReadVector(string[] parts, int start, int lineNumber)
    {
        if (parts.Length != start + 3) throw Error("Expected three numeric components", lineNumber);
        Span<float> components = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[start + i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
                throw Error("Components must be finite invariant-culture numbers", lineNumber);
            // Large values do not represent useful RGB and can overflow interpolation.
            if (MathF.Abs(value) > 65536) throw Error("Component is outside the supported range", lineNumber);
            components[i] = value;
        }
        return new Vector3(components[0], components[1], components[2]);
    }

    private static FormatException Error(string message, int lineNumber) => new($"{message} (line {lineNumber}).");
}
