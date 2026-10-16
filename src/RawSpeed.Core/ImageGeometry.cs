namespace RawSpeed.Core;

public readonly record struct ImagePoint(double X, double Y);

public static class ImageGeometry
{
    public static GeometryTransform Create(int sourceWidth, int sourceHeight, EditSettings settings)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || (long)sourceWidth * sourceHeight > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        ImageProcessor.ValidateSettings(settings);
        return new GeometryTransform(sourceWidth, sourceHeight, settings);
    }
}

/// <summary>Exact renderer geometry, with coordinates measured at pixel centers (top-left pixel = 0,0).</summary>
public sealed class GeometryTransform
{
    private readonly int _sourceWidth, _sourceHeight, _turns;
    private readonly bool _flip;
    private readonly double _cosine, _sine, _centerX, _centerY;

    internal GeometryTransform(int width, int height, EditSettings settings)
    {
        _sourceWidth = width; _sourceHeight = height;
        _turns = (settings.RotationQuarterTurns % 4 + 4) % 4;
        _flip = settings.FlipHorizontal;
        OrientedWidth = (_turns & 1) == 0 ? width : height;
        OrientedHeight = (_turns & 1) == 0 ? height : width;
        var crop = settings.Crop;
        CropX = crop is null ? 0 : Math.Min(OrientedWidth - 1, (int)Math.Floor(crop.X * OrientedWidth));
        CropY = crop is null ? 0 : Math.Min(OrientedHeight - 1, (int)Math.Floor(crop.Y * OrientedHeight));
        int endX = crop is null ? OrientedWidth : Math.Clamp((int)Math.Ceiling((crop.X + crop.Width) * OrientedWidth), CropX + 1, OrientedWidth);
        int endY = crop is null ? OrientedHeight : Math.Clamp((int)Math.Ceiling((crop.Y + crop.Height) * OrientedHeight), CropY + 1, OrientedHeight);
        OutputWidth = endX - CropX; OutputHeight = endY - CropY;
        double radians = settings.Straighten * Math.PI / 180;
        _cosine = Snap(Math.Cos(radians)); _sine = Snap(Math.Sin(radians));
        double extentWidth = Math.Max(1, OrientedWidth - 1), extentHeight = Math.Max(1, OrientedHeight - 1);
        Zoom = Math.Max(Math.Abs(_cosine) + Math.Abs(_sine) * extentHeight / extentWidth,
            Math.Abs(_cosine) + Math.Abs(_sine) * extentWidth / extentHeight);
        _centerX = (OrientedWidth - 1) * 0.5; _centerY = (OrientedHeight - 1) * 0.5;
        RequiresInterpolation = settings.Straighten != 0;
        CanCopyRows = _turns == 0 && !_flip && !RequiresInterpolation;
    }

    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int OrientedWidth { get; }
    public int OrientedHeight { get; }
    public int CropX { get; }
    public int CropY { get; }
    public double Zoom { get; }
    internal bool RequiresInterpolation { get; }
    internal bool CanCopyRows { get; }

    public ImagePoint MapOutputToSource(double pixelX, double pixelY)
    {
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY)) throw new ArgumentOutOfRangeException(nameof(pixelX));
        return MapOutputToSourceUnchecked(pixelX, pixelY);
    }

    internal ImagePoint MapOutputToSourceUnchecked(double pixelX, double pixelY)
    {
        double dx = pixelX + CropX - _centerX, dy = pixelY + CropY - _centerY;
        double ox = _centerX + (_cosine * dx + _sine * dy) / Zoom;
        double oy = _centerY + (-_sine * dx + _cosine * dy) / Zoom;
        if (_flip) ox = OrientedWidth - 1 - ox;
        return _turns switch
        {
            1 => new ImagePoint(oy, _sourceHeight - 1 - ox),
            2 => new ImagePoint(_sourceWidth - 1 - ox, _sourceHeight - 1 - oy),
            3 => new ImagePoint(_sourceWidth - 1 - oy, ox),
            _ => new ImagePoint(ox, oy)
        };
    }

    /// <summary>Maps source pixel centers into the cropped output; points outside its crop may lie outside the output.</summary>
    public ImagePoint MapSourceToOutput(double pixelX, double pixelY)
    {
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY)) throw new ArgumentOutOfRangeException(nameof(pixelX));
        (double ox, double oy) = _turns switch
        {
            1 => (_sourceHeight - 1 - pixelY, pixelX),
            2 => (_sourceWidth - 1 - pixelX, _sourceHeight - 1 - pixelY),
            3 => (pixelY, _sourceWidth - 1 - pixelX),
            _ => (pixelX, pixelY)
        };
        if (_flip) ox = OrientedWidth - 1 - ox;
        double dx = (ox - _centerX) * Zoom, dy = (oy - _centerY) * Zoom;
        return new ImagePoint(_centerX + _cosine * dx - _sine * dy - CropX, _centerY + _sine * dx + _cosine * dy - CropY);
    }

    /// <summary>Converts an output selection to normalized crop coordinates in the full oriented canvas.</summary>
    public NormalizedRect MapOutputCropToCanvas(NormalizedRect selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ImageProcessor.ValidateSettings(new EditSettings { Crop = selection });
        return new NormalizedRect((CropX + selection.X * OutputWidth) / OrientedWidth,
            (CropY + selection.Y * OutputHeight) / OrientedHeight,
            selection.Width * OutputWidth / OrientedWidth, selection.Height * OutputHeight / OrientedHeight);
    }

    private static double Snap(double value) => Math.Abs(value) < 1e-14 ? 0 : Math.Abs(Math.Abs(value) - 1) < 1e-14 ? Math.Sign(value) : value;
}
