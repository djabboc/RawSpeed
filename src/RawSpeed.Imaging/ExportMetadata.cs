using ImageMagick;

namespace RawSpeed.Imaging;

internal static class ExportMetadata
{
    // Copy only descriptive capture information. MakerNotes, thumbnails, offsets, serial numbers,
    // old pixel layout, arbitrary XMP/IPTC and application-specific edits are intentionally excluded.
    private static readonly HashSet<string> CaptureTags = new(StringComparer.Ordinal)
    {
        "Make", "Model", "LensMake", "LensModel", "LensSpecification", "Artist", "Copyright",
        "DateTimeOriginal", "DateTimeDigitized", "OffsetTimeOriginal", "OffsetTimeDigitized",
        "SubsecTimeOriginal", "SubsecTimeDigitized", "ExposureTime", "FNumber", "ExposureProgram",
        "ISOSpeedRatings", "PhotographicSensitivity", "SensitivityType", "RecommendedExposureIndex",
        "ShutterSpeedValue", "ApertureValue", "BrightnessValue", "ExposureBiasValue", "MaxApertureValue",
        "SubjectDistance", "MeteringMode", "LightSource", "Flash", "FocalLength", "FocalLengthIn35mmFilm",
        "ExposureMode", "WhiteBalance", "SceneCaptureType",
    };

    private static readonly HashSet<string> GpsTags = new(StringComparer.Ordinal)
    {
        "GPSVersionID", "GPSLatitudeRef", "GPSLatitude", "GPSLongitudeRef", "GPSLongitude",
        "GPSAltitudeRef", "GPSAltitude", "GPSTimestamp", "GPSDateStamp", "GPSImgDirectionRef", "GPSImgDirection",
    };

    public static void Apply(MagickImage image, ExportOptions options)
    {
        if (options.MetadataMode == ExportMetadataMode.None) return;
        if (string.IsNullOrWhiteSpace(options.SourcePath))
            throw new ArgumentException("A source file is required to preserve capture metadata.", nameof(options));

        using var source = new MagickImage();
        source.Ping(options.SourcePath);
        var sourceProfile = source.GetExifProfile();
        // LibRaw exposes decoded attributes but does not attach the source TIFF EXIF profile for
        // NEF/ORF. Read standard IFDs directly in that case instead of treating synthetic LibRaw
        // GPS defaults (0 N / 0 W) as real location metadata.
        var profile = sourceProfile is null
            ? RawCaptureMetadata.Read(options.SourcePath, options.MetadataMode == ExportMetadataMode.SafeExifWithGps)
            : new ExifProfile(sourceProfile.ToByteArray());
        foreach (var value in profile.Values.ToArray())
        {
            string tag = value.Tag.ToString();
            if (!CaptureTags.Contains(tag) && !(options.MetadataMode == ExportMetadataMode.SafeExifWithGps && GpsTags.Contains(tag)))
                profile.RemoveValue(value.Tag);
        }
        profile.RemoveThumbnail();
        profile.SetValue(ExifTag.Orientation, (ushort)1);
        profile.SetValue(ExifTag.ImageWidth, new Number(image.Width));
        profile.SetValue(ExifTag.ImageLength, new Number(image.Height));
        profile.SetValue(ExifTag.PixelXDimension, new Number(image.Width));
        profile.SetValue(ExifTag.PixelYDimension, new Number(image.Height));
        profile.SetValue(ExifTag.XResolution, new Rational((uint)options.Dpi));
        profile.SetValue(ExifTag.YResolution, new Rational((uint)options.Dpi));
        profile.SetValue(ExifTag.ResolutionUnit, (ushort)2);
        profile.SetValue(ExifTag.ColorSpace, options.ColorProfile == ExportColorProfile.SRgb ? (ushort)1 : (ushort)65535);
        profile.SetValue(ExifTag.Software, "RawSpeed");
        image.SetProfile(profile);
    }
}
