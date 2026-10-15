using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;

namespace RawSpeed.Metadata;

/// <summary>
/// Reads every metadata directory supported by MetadataExtractor, including camera maker notes
/// and all XMP property paths. This service does not change the source file.
/// </summary>
/// <remarks>
/// This is a synchronous I/O operation; callers should run it off the UI thread. Proprietary
/// or encrypted fields unsupported by the decoder cannot be interpreted. Unknown tags and
/// decoder warnings are retained so the viewer does not imply that extraction was complete.
/// </remarks>
public sealed class MetadataService
{
    /// <summary>Returns all available fields in decoder order, retaining repeated directories.</summary>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="IOException">The source cannot be read.</exception>
    /// <exception cref="ImageProcessingException">The file format cannot be decoded.</exception>
    public IReadOnlyList<MetadataEntry> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directories = ImageMetadataReader.ReadMetadata(path);
        var entries = new List<MetadataEntry>();

        foreach (var directory in directories)
        {
            foreach (var tag in directory.Tags)
            {
                // Include the numeric identifier so unknown/vendor-specific fields remain useful.
                string name = $"{tag.Name} (0x{tag.Type.ToString("X4", CultureInfo.InvariantCulture)})";
                string value = tag.Description ?? directory.GetString(tag.Type) ?? string.Empty;
                entries.Add(new MetadataEntry(directory.Name, name, value));
            }

            // XMP property names are strings, so they are not exposed through Directory.Tags.
            if (directory is XmpDirectory xmp)
            {
                foreach (var property in xmp.GetXmpProperties())
                    entries.Add(new MetadataEntry(directory.Name, property.Key, property.Value));
            }

            foreach (string error in directory.Errors)
                entries.Add(new MetadataEntry(directory.Name, "Decoder warning", error));
        }

        return entries.AsReadOnly();
    }
}
