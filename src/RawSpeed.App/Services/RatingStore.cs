using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace RawSpeed.App.Services;

/// <summary>Local, source-preserving star ratings keyed by normalized Windows paths.</summary>
public sealed class RatingStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _gate;
    private Dictionary<string, int>? _cachedRatings;
    private DateTime _cachedWriteTime;
    private long _cachedLength;

    public RatingStore(string? directory = null)
    {
        string root = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RawSpeed"));
        _path = Path.Combine(root, "ratings.json");
        _gate = Gates.GetOrAdd(_path, _ => new object());
    }

    public int GetRating(string sourcePath)
    {
        string key = Normalize(sourcePath);
        lock (_gate)
        {
            var info = new FileInfo(_path);
            DateTime modified = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            long length = info.Exists ? info.Length : 0;
            if (_cachedRatings is null || modified != _cachedWriteTime || length != _cachedLength)
            {
                _cachedRatings = Read(out _);
                _cachedWriteTime = modified;
                _cachedLength = length;
            }
            return _cachedRatings.GetValueOrDefault(key);
        }
    }

    public void SetRating(string sourcePath, int rating)
    {
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating), "Ratings must be between 0 and 5.");
        string key = Normalize(sourcePath);
        lock (_gate)
        {
            var ratings = Read(out bool malformed);
            if (rating == 0) ratings.Remove(key);
            else ratings[key] = rating;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Preserve malformed user data before replacing it with a recoverable store.
            if (malformed) File.Copy(_path, _path + ".corrupt-" + Guid.NewGuid().ToString("N"), overwrite: false);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, ratings);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                _cachedRatings = null;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private Dictionary<string, int> Read(out bool malformed)
    {
        malformed = false;
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return result;
        try
        {
            using var stream = File.OpenRead(_path);
            if (stream.Length > 16 * 1024 * 1024) { malformed = true; return result; }
            var stored = JsonSerializer.Deserialize<Dictionary<string, int>>(stream);
            if (stored is null) { malformed = true; return result; }
            foreach (var (path, rating) in stored)
            {
                if (rating is < 1 or > 5) { malformed = true; continue; }
                try { result[Normalize(path)] = rating; }
                catch (ArgumentException) { malformed = true; }
                catch (NotSupportedException) { malformed = true; }
            }
        }
        catch (JsonException) { malformed = true; }
        return result;
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
