namespace RawSpeed.Imaging;

/// <summary>An oriented, display-referred sRGB image with tightly packed, straight-alpha RGBA8 pixels.</summary>
/// <remarks>
/// Original dimensions describe the oriented source before preview resizing. A RAW decode is
/// developed by LibRaw at 16-bit precision before conversion to this display/editing buffer.
/// The current editing pipeline is therefore 8-bit sRGB, not a scene-linear RAW pipeline.
/// </remarks>
public sealed record DecodedImage(
    int Width,
    int Height,
    byte[] Pixels,
    string SourcePath,
    int OriginalWidth,
    int OriginalHeight,
    string DecodeMode);
