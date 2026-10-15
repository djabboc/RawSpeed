using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawSpeed.App.Models;
using RawSpeed.App.Services;
using RawSpeed.App.ViewModels;
using RawSpeed.Core;

namespace RawSpeed.App.Tests;

internal static class Program
{
    private static readonly string TestDirectory = Path.Combine(Path.GetTempPath(), "rawspeed-app-tests-" + Guid.NewGuid().ToString("N"));
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(TestDirectory);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunTestsAsync(); }
            catch (Exception exception) { _failures++; Console.Error.WriteLine(exception); }
            finally
            {
                // Every test artifact is a direct child of this uniquely created directory.
                foreach (string file in Directory.EnumerateFiles(TestDirectory)) File.Delete(file);
                Directory.Delete(TestDirectory);
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }));
        Dispatcher.Run();
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunTestsAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Recipe roundtrip, replacement and missing-file behavior", RecipeRoundTripAsync),
            ("Broken, unsupported, oversized and invalid recipes are rejected", InvalidRecipesAsync),
            ("Latest photo selection wins and preserves independent edit sessions", PhotoSelectionAsync),
            ("Slider history coalesces, undo/redo restores, save clears dirty state", EditHistoryAsync),
            ("First slider edit on a new photo retains an undo entry", HistoryAcrossPhotosAsync),
            ("Crop, rotation, original comparison and reset render consistently", GeometryAsync),
            ("Rapid edits publish the final value and freeze WPF bitmaps", LatestRenderAsync),
            ("Missing LUT fallback can be saved without leaving dirty state", MissingLutAsync),
            ("Malformed image shows a recoverable error and next selection loads", FailedDecodeAsync),
            ("Thumbnail memory remains bounded independently of source dimensions", ThumbnailAsync),
            ("Cancelled loading can be retried by reopening the same photo", CancelAndRetryAsync)
        };
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception exception) { _failures++; Console.Error.WriteLine($"FAIL {name}: {exception}"); }
        }
        Console.WriteLine($"{tests.Length - _failures}/{tests.Length} app tests passed; no application window or system dialog was opened.");
    }

    private static async Task RecipeRoundTripAsync()
    {
        string source = Path.Combine(TestDirectory, "roundtrip.jpg");
        True(await RecipeStore.LoadAsync(source) is null, "Missing sidecar must return null.");
        var recipe = EditRecipe.Default with
        {
            Settings = new EditSettings { Exposure = 1.25, Tint = -22, RotationQuarterTurns = 3, Crop = new NormalizedRect(0.1, 0.2, 0.7, 0.6) },
            LutPath = "C:\\色彩\\film.cube",
            LutStrength = 0.65
        };
        await RecipeStore.SaveAsync(source, recipe);
        True(recipe == await RecipeStore.LoadAsync(source), "Recipe must roundtrip exactly.");
        await RecipeStore.SaveAsync(source, EditRecipe.Default);
        True(EditRecipe.Default == await RecipeStore.LoadAsync(source), "Saving must atomically replace the earlier recipe.");
        True(!Directory.EnumerateFiles(TestDirectory, "*.tmp").Any(), "Saving left temporary files behind.");
    }

    private static async Task InvalidRecipesAsync()
    {
        string source = Path.Combine(TestDirectory, "invalid.jpg");
        string sidecar = RecipeStore.GetPath(source);
        foreach (string text in new[]
        {
            "{this is not JSON}", "null", "{}",
            JsonSerializer.Serialize(EditRecipe.Default with { Version = 99 }),
            JsonSerializer.Serialize(EditRecipe.Default with { LutStrength = 2 }),
            JsonSerializer.Serialize(EditRecipe.Default with { Settings = new EditSettings { Exposure = 999 } }),
            JsonSerializer.Serialize(EditRecipe.Default with { Settings = new EditSettings { Denoise = -10 } }),
            JsonSerializer.Serialize(EditRecipe.Default with { Settings = new EditSettings { CurveMidtones = 101 } }),
            JsonSerializer.Serialize(EditRecipe.Default with { Settings = new EditSettings { Crop = new NormalizedRect(0.8, 0, 0.5, 1) } }),
            JsonSerializer.Serialize(EditRecipe.Default with { Settings = new EditSettings { Crop = new NormalizedRect(0, 0, 0, 1) } }),
            "{\"Version\":1,\"Settings\":null,\"LutStrength\":1}", new string(' ', 1024 * 1024 + 1)
        })
        {
            await File.WriteAllTextAsync(sidecar, text);
            await ThrowsAsync(() => RecipeStore.LoadAsync(source), $"Recipe was accepted: {text[..Math.Min(text.Length, 140)]}");
        }
        await RecipeStore.SaveAsync(source, EditRecipe.Default);
        await ThrowsAsync(() => RecipeStore.SaveAsync(source, EditRecipe.Default with
        {
            Settings = new EditSettings { Exposure = 999 }
        }), "Save accepted an invalid edit and could replace a valid recipe.");
        True(await RecipeStore.LoadAsync(source) == EditRecipe.Default, "Rejected save modified an existing valid recipe.");
    }

    private static async Task PhotoSelectionAsync()
    {
        string red = CreateJpeg("select-red", 64, 48, 220, 12, 5);
        string blue = CreateJpeg("select-blue", 80, 32, 5, 12, 220);
        using var editor = new EditorViewModel();
        editor.AddFiles([red, blue]);
        editor.SelectedPhoto = editor.Photos[1];
        await ReadyAsync(editor);
        True(editor.SelectedPhoto?.Path == blue, "Latest photo selection was lost.");
        byte[] bluePixel = FirstBgra(editor.Preview!);
        True(bluePixel[0] > bluePixel[2] + 100, "An older red image replaced the latest blue preview.");
        editor.Exposure = 1;
        editor.SelectedPhoto = editor.Photos[0];
        await ReadyAsync(editor);
        Equal(0, editor.Exposure);
        editor.Temperature = 20;
        editor.SelectedPhoto = editor.Photos[1];
        await ReadyAsync(editor);
        Equal(1, editor.Exposure); Equal(0, editor.Temperature);
    }

    private static async Task EditHistoryAsync()
    {
        string source = CreateJpeg("history", 64, 48, 100, 120, 140);
        using var editor = new EditorViewModel();
        editor.AddFiles([source]);
        await ReadyAsync(editor);
        True(!editor.HasUnsavedChanges, "A newly opened photo must not be dirty.");
        editor.Exposure = 0.2; editor.Exposure = 0.4; editor.Exposure = 0.8;
        editor.Tint = 10;
        True(editor.HasUnsavedChanges && editor.UndoCommand.CanExecute(null), "Edits must become undoable and dirty.");
        editor.UndoCommand.Execute(null);
        Equal(0, editor.Tint); Equal(0.8, editor.Exposure);
        editor.UndoCommand.Execute(null);
        Equal(0, editor.Exposure);
        True(!editor.HasUnsavedChanges, "Undoing all edits must restore the saved state.");
        editor.RedoCommand.Execute(null);
        Equal(0.8, editor.Exposure);
        await SaveAsync(editor, source);
        True(!editor.HasUnsavedChanges, "Saved edits must clear dirty state.");
        True((await RecipeStore.LoadAsync(source))?.Settings.Exposure == 0.8, "Save command did not persist current edits.");
        editor.Tint = 33;
        True(!editor.RedoCommand.CanExecute(null), "A new edit must clear the redo branch.");
    }

    private static async Task HistoryAcrossPhotosAsync()
    {
        string a = CreateJpeg("undo-a", 48, 32, 80, 90, 100);
        string b = CreateJpeg("undo-b", 48, 32, 90, 100, 110);
        using var editor = new EditorViewModel();
        editor.AddFiles([a, b]);
        await ReadyAsync(editor);
        // Prime both preview caches, so switching is much faster than the 550 ms coalescing interval.
        editor.SelectedPhoto = editor.Photos[1]; await ReadyAsync(editor);
        editor.SelectedPhoto = editor.Photos[0]; await ReadyAsync(editor);
        editor.Exposure = 0.5;
        editor.SelectedPhoto = editor.Photos[1]; await ReadyAsync(editor);
        editor.Exposure = 0.7;
        True(editor.UndoCommand.CanExecute(null), "First edit on the second photo was accidentally coalesced with the first photo.");
        editor.UndoCommand.Execute(null);
        Equal(0, editor.Exposure);
    }

    private static async Task GeometryAsync()
    {
        string source = CreateJpeg("geometry", 64, 48, 100, 120, 140);
        using var editor = new EditorViewModel();
        editor.AddFiles([source]); await ReadyAsync(editor);
        True(editor.Preview?.PixelWidth == 64 && editor.Preview.PixelHeight == 48,
            $"Small JPEG was unexpectedly resized: {editor.Preview?.PixelWidth}x{editor.Preview?.PixelHeight}.");
        editor.ApplyCrop(new NormalizedRect(0.25, 0, 0.5, 1));
        await UntilAsync(() => editor.Preview?.PixelWidth == 32,
            () => $"Cropped preview (current {editor.Preview?.PixelWidth}x{editor.Preview?.PixelHeight}, crop {editor.Settings.Crop}, loading {editor.IsLoading}, error {editor.Error})");
        editor.ShowBefore = true;
        await UntilAsync(() => editor.Preview?.PixelWidth == 64, "Original comparison");
        editor.ShowBefore = false;
        await UntilAsync(() => editor.Preview?.PixelWidth == 32, "Return from original comparison");
        editor.RotateRightCommand.Execute(null);
        await UntilAsync(() => editor.Preview?.PixelWidth == 48 && editor.Preview.PixelHeight == 64, "Rotated preview");
        True(editor.Settings.Crop is null, "Rotation must clear the crop to avoid interpreting it in a different canvas.");
        editor.ResetCommand.Execute(null);
        await UntilAsync(() => editor.Preview?.PixelWidth == 64 && editor.Preview.PixelHeight == 48, "Reset geometry");
        True(editor.Settings == new EditSettings(), "Reset must restore default settings.");
    }

    private static async Task LatestRenderAsync()
    {
        string source = CreateJpeg("latest-render", 96, 64, 60, 100, 140);
        using var editor = new EditorViewModel();
        editor.AddFiles([source]); await ReadyAsync(editor);
        for (int i = 0; i < 20; i++) editor.Exposure = i / 20d;
        editor.Exposure = -2;
        await UntilAsync(() => editor.Preview is { } preview && FirstBgra(preview)[2] < 35, "Final rapid edit");
        byte[] first = FirstBgra(editor.Preview!);
        await Task.Delay(100);
        True(first.SequenceEqual(FirstBgra(editor.Preview!)), "An older render replaced the latest adjustment.");
        True(editor.Preview!.IsFrozen, "Preview bitmap must be frozen for safe sharing.");
    }

    private static async Task MissingLutAsync()
    {
        string source = CreateJpeg("missing-lut", 48, 32, 100, 120, 140);
        await RecipeStore.SaveAsync(source, EditRecipe.Default with { LutPath = Path.Combine(TestDirectory, "missing.cube") });
        using var editor = new EditorViewModel();
        editor.AddFiles([source]); await ReadyAsync(editor, allowError: true);
        True(editor.HasError, "Missing LUT should produce an actionable message.");
        await SaveAsync(editor, source);
        True((await RecipeStore.LoadAsync(source))?.LutPath is null, "Missing-LUT fallback should save the effective recipe.");
        True(!editor.HasUnsavedChanges, "Saving a missing-LUT fallback left a stale session dirty.");
    }

    private static async Task FailedDecodeAsync()
    {
        string broken = Path.Combine(TestDirectory, "broken.jpg");
        await File.WriteAllTextAsync(broken, "not an image");
        string valid = CreateJpeg("after-failure", 32, 24, 90, 120, 150);
        using var editor = new EditorViewModel();
        editor.AddFiles([broken, valid]);
        await UntilAsync(() => !editor.IsLoading && editor.HasError, "Decode error");
        True(!editor.HasImage, "A failed decode must not leave a previous source active.");
        editor.SelectedPhoto = editor.Photos[1]; await ReadyAsync(editor);
        True(!editor.HasError, "Selecting a valid photo must recover from a previous decode error.");
    }

    private static async Task ThumbnailAsync()
    {
        string source = CreateJpeg("thumbnail", 1024, 768, 100, 120, 140);
        using var editor = new EditorViewModel();
        editor.AddFiles([source]); await ReadyAsync(editor);
        PhotoItem photo = editor.Photos[0];
        True(photo.Thumbnail is { PixelWidth: > 0 and <= 160, PixelHeight: > 0 and <= 160 },
            "Filmstrip retained a full preview instead of a thumbnail bounded to 160 pixels.");
        True(photo.Thumbnail!.IsFrozen, "Filmstrip thumbnail must be frozen.");
        Equal(1024, photo.OriginalWidth); Equal(768, photo.OriginalHeight);
    }

    private static async Task CancelAndRetryAsync()
    {
        string source = CreateJpeg("cancel-retry", 96, 64, 100, 120, 140);
        using var editor = new EditorViewModel();
        editor.AddFiles([source]);
        True(editor.CancelCommand.CanExecute(null), "Loading should be cancellable.");
        editor.CancelCommand.Execute(null);
        await UntilAsync(() => !editor.IsLoading, "Cancellation");
        True(!editor.HasImage, "Cancelled initial loading unexpectedly published an editable source.");
        editor.AddFiles([source]);
        await ReadyAsync(editor);
        True(editor.Photos.Count == 1, "Retry should not duplicate the photo in the filmstrip.");
    }

    private static async Task SaveAsync(EditorViewModel editor, string source)
    {
        True(editor.SaveCommand.CanExecute(null), "Save command is unexpectedly disabled.");
        editor.SaveCommand.Execute(null);
        await UntilAsync(() => File.Exists(RecipeStore.GetPath(source)) && editor.SaveCommand.CanExecute(null), "Recipe save");
    }

    private static async Task ReadyAsync(EditorViewModel editor, bool allowError = false)
    {
        await UntilAsync(() => !editor.IsLoading, "Photo loading");
        True(editor.HasImage && editor.Preview is not null, "Expected a loaded preview. " + editor.Error);
        if (!allowError) True(!editor.HasError, editor.Error);
    }

    private static async Task UntilAsync(Func<bool> predicate, string operation)
        => await UntilAsync(predicate, () => operation);

    private static async Task UntilAsync(Func<bool> predicate, Func<string> operation)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(operation() + " timed out.");
            await Task.Delay(5);
        }
    }

    private static string CreateJpeg(string name, int width, int height, byte red, byte green, byte blue)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(TestDirectory, name + ".jpg");
        using var stream = File.Create(path);
        encoder.Save(stream);
        stream.Position = 0;
        var decoded = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        True(decoded.Frames[0].PixelWidth == width && decoded.Frames[0].PixelHeight == height,
            $"Fixture dimensions changed in WPF encoder: {decoded.Frames[0].PixelWidth}x{decoded.Frames[0].PixelHeight} instead of {width}x{height}.");
        return path;
    }

    private static byte[] FirstBgra(BitmapSource source)
    {
        byte[] pixel = new byte[4];
        source.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static async Task ThrowsAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Equal(double expected, double actual) => True(expected == actual, $"Expected {expected}, got {actual}.");
    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
