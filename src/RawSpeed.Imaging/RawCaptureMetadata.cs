using System.Globalization;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using ExifTag = ImageMagick.ExifTag;
using MetadataDirectory = MetadataExtractor.Directory;
using Rational = ImageMagick.Rational;

namespace RawSpeed.Imaging;

/// <summary>Maps only standard capture fields from RAW TIFF IFDs; proprietary and structural data never enter the export.</summary>
internal static class RawCaptureMetadata
{
    public static ExifProfile Read(string path, bool includeGps)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);
        var capture = directories.Where(directory => directory is ExifIfd0Directory or ExifSubIfdDirectory).ToArray();
        var profile = new ExifProfile();
        // Tag identifiers are the standard TIFF/EXIF field numbers, independent of camera vendor.
        (int Id, ExifTag<string> Tag)[] strings =
        [
            (0x010F, ExifTag.Make), (0x0110, ExifTag.Model), (0x013B, ExifTag.Artist), (0x8298, ExifTag.Copyright),
            (0x9003, ExifTag.DateTimeOriginal), (0x9004, ExifTag.DateTimeDigitized),
            (0x9291, ExifTag.SubsecTimeOriginal), (0x9292, ExifTag.SubsecTimeDigitized),
            (0xA433, ExifTag.LensMake), (0xA434, ExifTag.LensModel),
        ];
        foreach (var (id, tag) in strings) CopyText(profile, Find(capture, id), id, tag);
        (int Id, ExifTag<Rational> Tag)[] rationals =
        [
            (0x829A, ExifTag.ExposureTime), (0x829D, ExifTag.FNumber), (0x9202, ExifTag.ApertureValue),
            (0x9205, ExifTag.MaxApertureValue), (0x9206, ExifTag.SubjectDistance), (0x920A, ExifTag.FocalLength),
        ];
        foreach (var (id, tag) in rationals) CopyRational(profile, Find(capture, id), id, tag);
        (int Id, ExifTag<ushort> Tag)[] numbers =
        [
            (0x8822, ExifTag.ExposureProgram), (0x8830, ExifTag.SensitivityType), (0x9207, ExifTag.MeteringMode),
            (0x9208, ExifTag.LightSource), (0x9209, ExifTag.Flash), (0xA402, ExifTag.ExposureMode),
            (0xA403, ExifTag.WhiteBalance), (0xA405, ExifTag.FocalLengthIn35mmFilm), (0xA406, ExifTag.SceneCaptureType),
        ];
        foreach (var (id, tag) in numbers)
            if (Number(Find(capture, id), id) is double number && number is >= 0 and <= ushort.MaxValue)
                profile.SetValue(tag, (ushort)number);
        if (Number(Find(capture, 0x8827), 0x8827) is double iso && iso is >= 0 and <= ushort.MaxValue)
            profile.SetValue(ExifTag.ISOSpeedRatings, new[] { (ushort)iso });
        foreach (var (id, tag) in new (int, ExifTag<SignedRational>)[]
            { (0x9201, ExifTag.ShutterSpeedValue), (0x9203, ExifTag.BrightnessValue), (0x9204, ExifTag.ExposureBiasValue) })
            if (Number(Find(capture, id), id) is double value && double.IsFinite(value))
                profile.SetValue(tag, new SignedRational(value));

        if (includeGps && directories.OfType<GpsDirectory>().FirstOrDefault() is { } gps)
        {
            foreach (var (id, tag) in new (int, ExifTag<string>)[]
                { (1, ExifTag.GPSLatitudeRef), (3, ExifTag.GPSLongitudeRef), (16, ExifTag.GPSImgDirectionRef), (29, ExifTag.GPSDateStamp) })
                CopyText(profile, gps, id, tag);
            foreach (var (id, tag) in new (int, ExifTag<Rational[]>)[]
                { (2, ExifTag.GPSLatitude), (4, ExifTag.GPSLongitude), (7, ExifTag.GPSTimestamp) })
                if (gps.GetObject(id) is MetadataExtractor.Rational[] values && values.Length == 3
                    && values.All(value => value.Denominator != 0 && value.Numerator >= 0))
                    profile.SetValue(tag, values.Select(value => new Rational(value.ToDouble())).ToArray());
            CopyRational(profile, gps, 6, ExifTag.GPSAltitude);
            CopyRational(profile, gps, 17, ExifTag.GPSImgDirection);
            if (Number(gps, 5) is double altitudeRef && altitudeRef is 0 or 1)
                profile.SetValue(ExifTag.GPSAltitudeRef, (byte)altitudeRef);
        }
        return profile;
    }

    private static MetadataDirectory? Find(IEnumerable<MetadataDirectory> directories, int id) => directories.FirstOrDefault(directory => directory.ContainsTag(id));
    private static void CopyText(ExifProfile profile, MetadataDirectory? directory, int id, ExifTag<string> tag)
    {
        string? value = directory?.GetString(id);
        if (!string.IsNullOrWhiteSpace(value)) profile.SetValue(tag, value);
    }
    private static void CopyRational(ExifProfile profile, MetadataDirectory? directory, int id, ExifTag<Rational> tag)
    {
        if (Number(directory, id) is double value && double.IsFinite(value) && value >= 0)
            profile.SetValue(tag, new Rational(value));
    }
    private static double? Number(MetadataDirectory? directory, int id)
    {
        object? value = directory?.GetObject(id);
        if (value is null) return null;
        if (value is MetadataExtractor.Rational rational)
            return rational.Denominator == 0 ? null : rational.ToDouble();
        if (value is Array array && array.Length == 1) value = array.GetValue(0);
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return null; }
    }
}
