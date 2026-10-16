using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RawSpeed.App.Models;

namespace RawSpeed.App.Services;

/// <summary>Loads bounded, validated UI preferences and saves them with an atomic file replacement.</summary>
public sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private readonly string _filePath;

    public PreferencesStore(string? filePath = null) => _filePath = Path.GetFullPath(filePath ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RawSpeed", "preferences.json"));

    public string? LastLoadError { get; private set; }

    public EditorPreferences Load()
    {
        LastLoadError = null;
        if (!File.Exists(_filePath)) return new EditorPreferences();
        try
        {
            if (new FileInfo(_filePath).Length > 256 * 1024) throw new InvalidDataException("Preferences file is too large.");
            var preferences = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(_filePath), JsonOptions)
                ?? throw new InvalidDataException("Preferences file is empty.");
            Validate(preferences);
            return preferences;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LastLoadError = exception.Message;
            return new EditorPreferences();
        }
    }

    public void Save(EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Validate(preferences);
        var snapshot = preferences.Clone();
        foreach (var action in snapshot.Shortcuts.Keys.ToArray())
        {
            ShortcutService.TryNormalize(snapshot.Shortcuts[action], out string normalized, out _);
            snapshot.Shortcuts[action] = normalized;
        }
        string directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".rawspeed-preferences-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporary, _filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(EditorPreferences preferences)
    {
        if (preferences.Version != 1) throw new ArgumentException("Unsupported preferences version.");
        Check(preferences.LibraryWidth, 100, 1200, nameof(preferences.LibraryWidth));
        Check(preferences.ToolsWidth, 180, 1400, nameof(preferences.ToolsWidth));
        Check(preferences.HistogramHeight, 40, 800, nameof(preferences.HistogramHeight));
        Check(preferences.ThumbnailSize, 32, 400, nameof(preferences.ThumbnailSize));
        if (!ShortcutService.TryValidate(preferences, out string error)) throw new ArgumentException(error);
    }

    private static void Check(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"Expected {minimum}–{maximum}.");
    }
}
