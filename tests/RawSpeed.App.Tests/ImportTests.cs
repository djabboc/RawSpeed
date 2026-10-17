using System.Collections.Specialized;
using System.IO;
using System.Windows.Threading;
using RawSpeed.App.Models;
using RawSpeed.App.Services;
using RawSpeed.App.ViewModels;

namespace RawSpeed.App.Tests;

internal static partial class Program
{
    private static async Task LargeImportAsync()
    {
        const int count = 1024;
        string directory = Path.Combine(TestDirectory, "large-import");
        Directory.CreateDirectory(directory);
        string valid = CreateJpeg("import-first", 32, 24, 100, 120, 140);
        var paths = Enumerable.Range(0, count).Select(i => Path.Combine(directory, $"{i:0000}.jpg")).ToArray();
        File.Copy(valid, paths[0]);
        foreach (string path in paths.Skip(1)) File.WriteAllBytes(path, []);
        File.WriteAllText(Path.Combine(directory, "ignored.txt"), "ignored");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.Copy(valid, Path.Combine(directory, "nested", "excluded.jpg"));
        new RatingStore(TestDirectory).SetRating(paths[600], 4);

        using var editor = new EditorViewModel(TestDirectory);
        int uiThread = Environment.CurrentManagedThreadId;
        bool scannedOnWorker = false, publishedOnUi = true, pulseBeforeCompletion = false, selectedEarly = false;
        int countAtPulse = 0;
        editor.Photos.CollectionChanged += (_, _) =>
        {
            publishedOnUi &= Environment.CurrentManagedThreadId == uiThread;
            if (editor.Photos.Count != 1) return;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                pulseBeforeCompletion = editor.IsImporting;
                countAtPulse = editor.Photos.Count;
                selectedEarly = editor.SelectedPhoto?.Path == paths[0];
            }), DispatcherPriority.Normal);
        };
        IEnumerable<string> Inputs()
        {
            scannedOnWorker = Environment.CurrentManagedThreadId != uiThread;
            yield return directory;
            // Exercise normalization and duplicate inputs without adding duplicate cards.
            foreach (string path in paths.Reverse()) yield return path.ToUpperInvariant();
            foreach (string path in paths) yield return path;
        }

        await editor.AddFilesAsync(Inputs());
        True(scannedOnWorker, "The input iterator ran on the UI thread.");
        True(publishedOnUi, "Photo collection changes escaped the UI dispatcher.");
        True(pulseBeforeCompletion && countAtPulse < count, "UI work did not run between import batches.");
        True(selectedEarly, "The first photo was not selected until the entire import completed.");
        True(editor.Photos.Select(photo => photo.Path).SequenceEqual(paths), "Folder order, duplicate filtering, or non-recursive enumeration changed.");
        True(editor.Photos[600].Rating == 4, "Imported photos lost their persisted ratings.");
        True(!editor.IsImporting && editor.ImportStatus.Contains(count.ToString("N0")), "Import completion/count was not published.");
        await ReadyAsync(editor);
    }

    private static async Task ImportIndexConsistencyAsync()
    {
        string first = CreateJpeg("import-index-first", 32, 24, 90, 100, 120);
        string second = CreateJpeg("import-index-second", 32, 24, 110, 120, 140);
        using var editor = new EditorViewModel(TestDirectory);
        var external = new PhotoItem(first);
        editor.Photos.Add(external); // Mutation before the index is initialized.
        await editor.AddFilesAsync([first.ToUpperInvariant()]);
        True(editor.Photos.Count == 1 && ReferenceEquals(editor.Photos[0], external), "A pre-existing public collection item was duplicated.");

        editor.Photos.Add(external); // Even repeated references must be counted correctly.
        editor.Photos.Remove(external);
        await editor.AddFilesAsync([first]);
        True(editor.Photos.Count == 1, "Removing one occurrence dropped an item still present in Photos from the index.");

        editor.Photos[0] = new PhotoItem(second);
        await editor.AddFilesAsync([first, second]);
        True(editor.Photos.Count == 2 && editor.Photos.Any(photo => photo.Path == first), "Collection replacement left a stale path in the index.");
        editor.Photos.Move(0, 1);
        await editor.AddFilesAsync([first, second]);
        True(editor.Photos.Count == 2, "Moving a photo corrupted duplicate detection.");
        editor.Photos.Clear();
        await editor.AddFilesAsync([first, second]);
        True(editor.Photos.Count == 2, "Clearing the collection left stale entries in the index.");
        editor.Photos.RemoveAt(0);
        await editor.AddFilesAsync([first]);
        True(editor.Photos.Count == 2, "Removed paths could not be imported again.");
        await ReadyAsync(editor);
    }

    private static async Task ImportCancellationAsync()
    {
        string directory = Path.Combine(TestDirectory, "cancel-import");
        Directory.CreateDirectory(directory);
        var paths = Enumerable.Range(0, 256).Select(i => Path.Combine(directory, $"{i:0000}.jpg")).ToArray();
        foreach (string path in paths) File.WriteAllBytes(path, []);
        using var editor = new EditorViewModel(TestDirectory);
        bool cancelled = false;
        NotifyCollectionChangedEventHandler cancelAfterBatch = (_, _) =>
        {
            if (editor.Photos.Count != 65) return;
            True(editor.CancelCommand.CanExecute(null), "Cancel is disabled while importing.");
            cancelled = true;
            editor.CancelCommand.Execute(null);
        };
        editor.Photos.CollectionChanged += cancelAfterBatch;
        await editor.AddFilesAsync(paths);
        True(cancelled && !editor.IsImporting && editor.Photos.Count == 65, "Cancellation did not stop pending batches while retaining imported photos.");
        True(editor.ImportStatus.Contains("取消"), "Cancelled import did not show its outcome.");
        editor.Photos.CollectionChanged -= cancelAfterBatch;
        await editor.AddFilesAsync(paths);
        True(editor.Photos.Count == paths.Length, "Import could not resume without duplicates after cancellation.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        editor.Photos.Clear();
        await editor.AddFilesAsync(paths, cancellation.Token);
        True(editor.Photos.Count == 0 && !editor.IsImporting, "A pre-cancelled import still published photos.");
        await UntilAsync(() => !editor.IsLoading, "Cancelled import's preview completion");
    }

    private static async Task ImportOwnershipAsync()
    {
        string first = CreateJpeg("import-owned-first", 32, 24, 100, 120, 140);
        string stale = CreateJpeg("import-owned-stale", 32, 24, 110, 130, 150);
        string latest = CreateJpeg("import-owned-latest", 32, 24, 120, 140, 160);
        using (var editor = new EditorViewModel(TestDirectory))
        {
            Task? replacement = null;
            editor.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditorViewModel.PhotoCount) && editor.Photos.Count == 1 && replacement is null)
                    replacement = editor.AddFilesAsync([latest]);
            };
            await editor.AddFilesAsync([first, stale]);
            True(replacement is not null, "Replacement import was not started.");
            await replacement!;
            True(editor.Photos.Count == 2 && editor.Photos.All(photo => photo.Path != stale), "A superseded import appended stale work.");
            True(editor.SelectedPhoto?.Path == latest && !editor.IsImporting, "The old import changed the replacement's selection or state.");
            await ReadyAsync(editor);
        }

        var disposed = new EditorViewModel(TestDirectory);
        disposed.Photos.CollectionChanged += (_, _) => disposed.Dispose();
        await disposed.AddFilesAsync([first, stale]);
        True(disposed.Photos.Count == 1 && !disposed.IsImporting && disposed.SelectedPhoto is null,
            "Disposing an editor allowed queued import work or photo loading to continue.");
        await disposed.AddFilesAsync([latest]);
        True(disposed.Photos.Count == 1, "A disposed editor accepted a new import.");
    }
}
