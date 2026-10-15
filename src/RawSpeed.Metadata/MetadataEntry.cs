namespace RawSpeed.Metadata;

/// <summary>A metadata field in its original directory, with a human-readable value.</summary>
public sealed record MetadataEntry(string Group, string Name, string Value);
