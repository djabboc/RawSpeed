using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace RawSpeed.Core;

public readonly record struct CurvePoint(double X, double Y);

/// <summary>Immutable, value-equal RGB transfer curve with shape-preserving cubic interpolation.</summary>
public sealed class ToneCurve : IEquatable<ToneCurve>
{
    private readonly CurvePoint[] _points;
    private readonly double[] _tangents;

    [JsonConstructor]
    public ToneCurve(IReadOnlyList<CurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count is < 2 or > 64) throw new ArgumentException("A curve requires 2–64 points.", nameof(points));
        _points = new CurvePoint[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            CurvePoint point = points[i];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X is < 0 or > 1 || point.Y is < 0 or > 1)
                throw new ArgumentException("Curve coordinates must be finite and within 0–1.", nameof(points));
            if (i > 0 && point.X - _points[i - 1].X < 1e-6)
                throw new ArgumentException("Curve X coordinates must increase by at least 0.000001.", nameof(points));
            _points[i] = point;
        }
        if (_points[0].X != 0 || _points[^1].X != 1)
            throw new ArgumentException("The first and last curve X coordinates must be 0 and 1.", nameof(points));
        Points = new ReadOnlyCollection<CurvePoint>(_points);
        _tangents = BuildTangents(_points);
    }

    public IReadOnlyList<CurvePoint> Points { get; }
    public static ToneCurve Identity { get; } = new([new(0, 0), new(1, 1)]);

    /// <summary>PCHIP preserves each interval's monotonic direction without overshoot, including inverted curves.</summary>
    public double Evaluate(double input)
    {
        if (!double.IsFinite(input) || input is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(input));
        if (input == 0) return _points[0].Y;
        if (input == 1) return _points[^1].Y;
        int low = 0, high = _points.Length - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (_points[middle].X <= input) low = middle; else high = middle;
        }
        double span = _points[high].X - _points[low].X;
        double t = (input - _points[low].X) / span, t2 = t * t, t3 = t2 * t;
        double value = (2 * t3 - 3 * t2 + 1) * _points[low].Y + (t3 - 2 * t2 + t) * span * _tangents[low] +
            (-2 * t3 + 3 * t2) * _points[high].Y + (t3 - t2) * span * _tangents[high];
        return Math.Clamp(value, Math.Min(_points[low].Y, _points[high].Y), Math.Max(_points[low].Y, _points[high].Y));
    }

    public bool Equals(ToneCurve? other) => ReferenceEquals(this, other) || other is not null && _points.AsSpan().SequenceEqual(other._points);
    public override bool Equals(object? obj) => obj is ToneCurve other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (CurvePoint point in _points) hash.Add(point);
        return hash.ToHashCode();
    }

    private static double[] BuildTangents(CurvePoint[] points)
    {
        int count = points.Length;
        var slope = new double[count - 1];
        var span = new double[count - 1];
        var tangent = new double[count];
        for (int i = 0; i < count - 1; i++)
        {
            span[i] = points[i + 1].X - points[i].X;
            slope[i] = (points[i + 1].Y - points[i].Y) / span[i];
        }
        if (count == 2) { tangent[0] = tangent[1] = slope[0]; return tangent; }
        for (int i = 1; i < count - 1; i++)
        {
            if (slope[i - 1] == 0 || slope[i] == 0 || Math.Sign(slope[i - 1]) != Math.Sign(slope[i])) tangent[i] = 0;
            else
            {
                double w1 = 2 * span[i] + span[i - 1], w2 = span[i] + 2 * span[i - 1];
                tangent[i] = (w1 + w2) / (w1 / slope[i - 1] + w2 / slope[i]);
            }
        }
        tangent[0] = Endpoint(span[0], span[1], slope[0], slope[1]);
        tangent[^1] = Endpoint(span[^1], span[^2], slope[^1], slope[^2]);
        return tangent;
    }

    private static double Endpoint(double h0, double h1, double d0, double d1)
    {
        double value = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
        if (Math.Sign(value) != Math.Sign(d0)) return 0;
        return Math.Sign(d0) != Math.Sign(d1) && Math.Abs(value) > 3 * Math.Abs(d0) ? 3 * d0 : value;
    }
}
