using System.IO;

namespace RawSpeed.App.Services;

/// <summary>Enumerates inputs on the import worker; no WPF collections are accessed here.</summary>
internal static class PhotoImportService
{
    internal readonly record struct Photo(string Path, int Rating);

    public static IEnumerable<Photo> Enumerate(IEnumerable<string> inputs, RatingStore ratings, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string input in inputs)
        {
            token.ThrowIfCancellationRequested();
            // Directory imports remain non-recursive, as in the original folder picker.
            IEnumerable<string> paths = Directory.Exists(input)
                ? EnumerateDirectory(input, token).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                : [input];
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (!ViewModels.EditorViewModel.IsSupported(path) || !File.Exists(path)) continue;
                string full = Path.GetFullPath(path);
                if (!seen.Add(full)) continue;
                yield return new Photo(full, ratings.GetRating(full));
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectory(string directory, CancellationToken token)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            token.ThrowIfCancellationRequested();
            if (ViewModels.EditorViewModel.IsSupported(path)) yield return path;
        }
    }
}
