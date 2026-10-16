using System.Numerics;

namespace RawSpeed.Core;

internal sealed class ColorPipeline
{
    private const int Resolution = 4096;
    private readonly float[][] _balance = [new float[256], new float[256], new float[256]];
    private readonly float[][]? _curves;
    private readonly float[]? _luminance;
    private readonly bool _mix;
    private readonly Vector3 _rRow, _gRow, _bRow;
    private readonly CubeLut? _lut;
    private readonly float _lutStrength;

    private ColorPipeline(EditSettings settings, CubeLut? lut, float lutStrength)
    {
        _lut = lutStrength == 0 ? null : lut; _lutStrength = lutStrength;
        double exposure = Math.Pow(2, settings.Exposure);
        double[] gains = [exposure * Math.Pow(2, settings.Temperature * 0.0035),
            exposure * Math.Pow(2, -settings.Tint * 0.0025), exposure * Math.Pow(2, -settings.Temperature * 0.0035)];
        for (int channel = 0; channel < 3; channel++)
        for (int i = 0; i < 256; i++)
        {
            double value = i / 255d;
            if (gains[channel] != 1)
            {
                double linear = (value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4)) * gains[channel];
                value = Math.Clamp(linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055, 0, 1);
            }
            _balance[channel][i] = (float)value;
        }
        if (settings.Contrast != 0 || settings.Shadows != 0 || settings.Highlights != 0 || settings.Midtones != 0)
        {
            _luminance = new float[Resolution + 1];
            double contrast = Math.Pow(2, settings.Contrast / 100);
            for (int i = 0; i <= Resolution; i++)
            {
                double value = i / (double)Resolution;
                double darkWeight = (1 - SmoothStep(0.10, 0.65, value)) * (1 - Math.Exp(-value / 0.04));
                double lightWeight = SmoothStep(0.35, 0.90, value) * (1 - Math.Exp(-(1 - value) / 0.04));
                double middleWeight = 16 * value * value * (1 - value) * (1 - value);
                value = Math.Clamp(value + settings.Shadows * 0.0035 * darkWeight +
                    settings.Highlights * 0.0035 * lightWeight + settings.Midtones * 0.0025 * middleWeight, 0, 1);
                if (settings.Contrast != 0)
                    value = value < 0.5 ? 0.5 * Math.Pow(2 * value, contrast) : 1 - 0.5 * Math.Pow(2 * (1 - value), contrast);
                _luminance[i] = (float)value;
            }
        }
        if (settings.CurveShadows != 0 || settings.CurveMidtones != 0 || settings.CurveHighlights != 0 ||
            settings.MasterCurve is not null || settings.RedCurve is not null || settings.GreenCurve is not null || settings.BlueCurve is not null)
        {
            _curves = [new float[Resolution + 1], new float[Resolution + 1], new float[Resolution + 1]];
            ToneCurve?[] channelCurves = [settings.RedCurve, settings.GreenCurve, settings.BlueCurve];
            for (int i = 0; i <= Resolution; i++)
            {
                double value = ImageProcessor.EvaluateCurve(i / (double)Resolution, settings);
                for (int channel = 0; channel < 3; channel++) _curves[channel][i] = (float)(channelCurves[channel]?.Evaluate(value) ?? value);
            }
        }
        _mix = settings.Saturation != 0 || settings.Hue != 0;
        float radians = (float)(settings.Hue * Math.PI / 180), cosine = MathF.Cos(radians), sine = MathF.Sin(radians);
        float shared = (1 - cosine) / 3, spin = sine / MathF.Sqrt(3);
        float diagonal = cosine + shared, positive = shared + spin, negative = shared - spin;
        Vector3 r = new(diagonal, negative, positive), g = new(positive, diagonal, negative), b = new(negative, positive, diagonal);
        Vector3 luminance = r * 0.2126f + g * 0.7152f + b * 0.0722f;
        float saturation = (float)(1 + settings.Saturation / 100);
        _rRow = r * saturation + luminance * (1 - saturation);
        _gRow = g * saturation + luminance * (1 - saturation);
        _bRow = b * saturation + luminance * (1 - saturation);
    }

    internal static void Apply(byte[] pixels, int width, int height, EditSettings settings, CubeLut? lut, float strength, ParallelOptions options)
    {
        if (settings.Exposure == 0 && settings.Temperature == 0 && settings.Tint == 0 && settings.Contrast == 0 &&
            settings.Highlights == 0 && settings.Shadows == 0 && settings.Midtones == 0 && settings.Saturation == 0 && settings.Hue == 0 &&
            settings.CurveShadows == 0 && settings.CurveMidtones == 0 && settings.CurveHighlights == 0 &&
            settings.MasterCurve is null && settings.RedCurve is null && settings.GreenCurve is null && settings.BlueCurve is null && (lut is null || strength == 0)) return;
        var pipeline = new ColorPipeline(settings, lut, strength);
        Parallel.For(0, height, options, y => pipeline.ApplyRow(pixels, y * width * 4, (y + 1) * width * 4));
    }

    private void ApplyRow(byte[] pixels, int start, int end)
    {
        for (int index = start; index < end; index += 4)
        {
            var color = new Vector3(_balance[0][pixels[index]], _balance[1][pixels[index + 1]], _balance[2][pixels[index + 2]]);
            if (_luminance is not null)
            {
                float y = 0.2126f * color.X + 0.7152f * color.Y + 0.0722f * color.Z;
                color = Vector3.Clamp(color + new Vector3(Lookup(_luminance, y) - y), Vector3.Zero, Vector3.One);
            }
            if (_curves is not null) color = new Vector3(Lookup(_curves[0], color.X), Lookup(_curves[1], color.Y), Lookup(_curves[2], color.Z));
            if (_mix) color = new Vector3(Vector3.Dot(_rRow, color), Vector3.Dot(_gRow, color), Vector3.Dot(_bRow, color));
            color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
            if (_lut is not null) color = Vector3.Lerp(color, _lut.TransformUnchecked(color.X, color.Y, color.Z), _lutStrength);
            pixels[index] = ToByte(color.X); pixels[index + 1] = ToByte(color.Y); pixels[index + 2] = ToByte(color.Z);
        }
    }

    private static float Lookup(float[] table, float input)
    {
        float coordinate = Math.Clamp(input, 0, 1) * Resolution;
        int index = Math.Min((int)coordinate, Resolution - 1);
        return table[index] + (table[index + 1] - table[index]) * (coordinate - index);
    }

    private static double SmoothStep(double start, double end, double value)
    {
        double t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}
