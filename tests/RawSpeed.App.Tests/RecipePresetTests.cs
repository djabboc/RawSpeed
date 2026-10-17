using System.IO;
using System.Text;
using System.Text.Json;
using RawSpeed.App.Models;
using RawSpeed.App.Services;
using RawSpeed.App.ViewModels;

namespace RawSpeed.App.Tests;

internal static partial class Program
{
    private const string IdentityCube = "LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n";

    private static async Task EncodedCubeRecipesAsync()
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode })
        {
            string root = Path.Combine(TestDirectory, "encoded-cube-" + encoding.CodePage);
            string presets = Path.Combine(root, "presets");
            Directory.CreateDirectory(presets);
            await File.WriteAllTextAsync(Path.Combine(presets, "identity.cube"), IdentityCube, encoding);
            string photo = CreateJpeg("encoded-cube-" + encoding.CodePage, 48, 32, 75, 90, 115);
            byte[] expected;
            using (var editor = new EditorViewModel(root, presets, Path.Combine(root, "cache")))
            {
                editor.AddFiles([photo]); await ReadyAsync(editor);
                expected = FirstBgra(editor.Preview!);
                await SelectFixturePresetAsync(editor, FilterPresetKind.Cube);
                True(expected.SequenceEqual(FirstBgra(editor.Preview!)), "An encoded identity Cube changed the image.");
                await SaveAsync(editor, photo);
            }
            using var reopened = new EditorViewModel(root, presets, Path.Combine(root, "cache"));
            reopened.AddFiles([photo]); await ReadyAsync(reopened);
            True(reopened.HasLut && expected.SequenceEqual(FirstBgra(reopened.Preview!)), "A saved encoded Cube did not reload correctly.");
        }
    }

    private static async Task PersistentPresetCachesAsync()
    {
        foreach (var kind in new[] { FilterPresetKind.RapidRawFilm, FilterPresetKind.RapidRawCommunity })
        {
            var fixture = CreatePresetFixture("persistent-" + kind, kind);
            byte[] expected;
            EditRecipe saved;
            using (var editor = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache))
            {
                editor.AddFiles([fixture.Photo]); await ReadyAsync(editor);
                await SelectFixturePresetAsync(editor, kind);
                var previous = editor.Preview;
                editor.LutStrength = 65;
                await UntilAsync(() => editor.Preview != previous, "Partial-strength filter rendering");
                expected = FirstBgra(editor.Preview!);
                await SaveAsync(editor, fixture.Photo);
                saved = (await RecipeStore.LoadAsync(fixture.Photo))!;
                True(saved.Version == 2 && saved.LutSource is { SourceSha256.Length: 64 } && saved.LutPath is null,
                    "A catalog recipe must store source identity, never its disposable cache path.");
            }
            True(Directory.EnumerateFiles(fixture.Cache, "*.cube").Any(), "The first editor did not generate a cache.");
            // Both directories were created inside this test's unique temporary fixture.
            Directory.Delete(fixture.Cache, recursive: true);
            string relocated = fixture.Presets + "-relocated";
            Directory.Move(fixture.Presets, relocated);
            using var reopened = new EditorViewModel(fixture.Root, relocated, fixture.Cache);
            reopened.AddFiles([fixture.Photo]); await ReadyAsync(reopened);
            True(expected.SequenceEqual(FirstBgra(reopened.Preview!)), "Reopening with a rebuilt cache changed the saved pixels.");
            Equal(65, reopened.LutStrength);
            True(reopened.HasLut && !reopened.HasUnsavedChanges && reopened.ExportCommand.CanExecute(null),
                "Restoring an unchanged preset should be clean and exportable.");
            True(Directory.EnumerateFiles(fixture.Cache, "*.cube").Any(), "Reopening did not rebuild the missing cache.");
            await SaveAsync(reopened, fixture.Photo);
            True(await RecipeStore.LoadAsync(fixture.Photo) == saved, "Installation/cache relocation changed persistent filter identity.");
        }
    }

    private static async Task LegacyPresetRecipesAsync()
    {
        var fixture = CreatePresetFixture("legacy", FilterPresetKind.RapidRawFilm);
        var catalog = new LutCatalogService(fixture.Presets, fixture.Cache);
        var preset = catalog.Scan().Single(p => p.Kind == FilterPresetKind.RapidRawFilm);
        string oldPath = catalog.GetLutPath(preset)!;
        var legacy = new EditRecipe(1, new() { Exposure = 0.25 }, oldPath, 0.4);
        // Write the old format directly; normal saves always write the current version.
        await File.WriteAllTextAsync(RecipeStore.GetPath(fixture.Photo), JsonSerializer.Serialize(new
        {
            legacy.Version, legacy.Settings, legacy.LutPath, legacy.LutStrength
        }));
        var upgraded = await RecipeStore.LoadAsync(fixture.Photo);
        True(upgraded?.Version == 2 && upgraded.LutPath == oldPath && upgraded.Settings == legacy.Settings,
            "Version 1 migration changed the editing parameters or lost the fallback path.");
        Directory.Delete(fixture.Cache, recursive: true);
        using (var editor = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache))
        {
            editor.AddFiles([fixture.Photo]); await ReadyAsync(editor);
            True(editor.HasLut && !editor.HasUnsavedChanges, "A legacy cached LUT did not migrate transparently.");
            await SaveAsync(editor, fixture.Photo);
            var migrated = await RecipeStore.LoadAsync(fixture.Photo);
            True(migrated?.LutPath is null && migrated?.LutSource?.Id == preset.Id && migrated.LutStrength == 0.4,
                "Saving a recovered version 1 recipe did not persist its stable identity.");
        }
        string importedPhoto = CreateJpeg("legacy-imported", 48, 32, 90, 80, 70);
        string imported = Path.Combine(fixture.Root, "imported.cube");
        await File.WriteAllTextAsync(imported, IdentityCube);
        var oldImported = new EditRecipe(1, new(), imported, 0.75);
        await File.WriteAllTextAsync(RecipeStore.GetPath(importedPhoto), JsonSerializer.Serialize(new
        {
            oldImported.Version, oldImported.Settings, oldImported.LutPath, oldImported.LutStrength
        }));
        using var reopened = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache);
        reopened.AddFiles([importedPhoto]); await ReadyAsync(reopened);
        True(reopened.HasLut && !reopened.HasUnsavedChanges, "A legacy imported cube path no longer loads.");
        await SaveAsync(reopened, importedPhoto);
        True((await RecipeStore.LoadAsync(importedPhoto)) is { Version: 2, LutSource: null, LutStrength: 0.75 } stored && stored.LutPath == imported,
            "The legacy imported cube path was not retained by migration.");
    }

    private static async Task UnavailablePresetSourcesAsync()
    {
        var fixture = CreatePresetFixture("unavailable", FilterPresetKind.RapidRawCommunity);
        EditRecipe saved;
        byte[] expected;
        using (var editor = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache))
        {
            editor.AddFiles([fixture.Photo]); await ReadyAsync(editor);
            await SelectFixturePresetAsync(editor, FilterPresetKind.RapidRawCommunity);
            expected = FirstBgra(editor.Preview!);
            await SaveAsync(editor, fixture.Photo);
            saved = (await RecipeStore.LoadAsync(fixture.Photo))!;
        }
        string original = saved.LutSource!.OriginalPath;
        string originalContents = await File.ReadAllTextAsync(original);
        File.Delete(original);
        Directory.Delete(fixture.Cache, recursive: true);
        using (var missing = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache))
        {
            missing.AddFiles([fixture.Photo]); await ReadyAsync(missing, allowError: true);
            True(missing.HasError && missing.HasLut && !missing.HasUnsavedChanges && !missing.ExportCommand.CanExecute(null),
                "A missing source should remain visible and keep the saved recipe intact.");
            await SaveAsync(missing, fixture.Photo);
            True(await RecipeStore.LoadAsync(fixture.Photo) == saved, "Saving after a missing source permanently removed the filter.");
        }
        await File.WriteAllTextAsync(original, originalContents.Replace("\"exposure\":1", "\"exposure\":2", StringComparison.Ordinal));
        using (var changed = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache))
        {
            changed.AddFiles([fixture.Photo]); await ReadyAsync(changed, allowError: true);
            True(changed.HasError && changed.Error.Contains("已改变", StringComparison.Ordinal) && changed.HasLut,
                "A changed source was silently substituted for the saved filter.");
            await SaveAsync(changed, fixture.Photo);
            True(await RecipeStore.LoadAsync(fixture.Photo) == saved, "Saving a changed-source fallback lost the expected source identity.");
        }
        await File.WriteAllTextAsync(original, originalContents);
        using var restored = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache);
        restored.AddFiles([fixture.Photo]); await ReadyAsync(restored);
        True(expected.SequenceEqual(FirstBgra(restored.Preview!)), "Restoring the original source did not recover the exact saved appearance.");
        var unknownAdapter = saved with { LutSource = saved.LutSource! with { AdapterVersion = "future-incompatible-adapter" } };
        await RecipeStore.SaveAsync(fixture.Photo, unknownAdapter);
        using var incompatible = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache);
        incompatible.AddFiles([fixture.Photo]); await ReadyAsync(incompatible, allowError: true);
        True(incompatible.HasError && !incompatible.ExportCommand.CanExecute(null), "An unsupported adapter silently changed saved colors.");
        await SaveAsync(incompatible, fixture.Photo);
        True(await RecipeStore.LoadAsync(fixture.Photo) == unknownAdapter, "An unavailable adapter reference was destroyed.");
        await ThrowsAsync(() => RecipeStore.SaveAsync(fixture.Photo, saved with { LutSource = saved.LutSource! with { SourceSha256 = "invalid" } }),
            "An invalid source checksum was accepted.");
    }

    private static async Task PresetHistoryAsync()
    {
        var fixture = CreatePresetFixture("preset-history", FilterPresetKind.RapidRawFilm);
        using (var first = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache))
        {
            first.AddFiles([fixture.Photo]); await ReadyAsync(first);
            await SelectFixturePresetAsync(first, FilterPresetKind.RapidRawFilm);
            // Discrete removal must create its own undo step even immediately after applying a preset.
            first.ClearLutCommand.Execute(null); first.UndoCommand.Execute(null);
            True(first.HasLut, "Clearing a freshly applied filter was coalesced with its application.");
            await SaveAsync(first, fixture.Photo);
        }
        using var editor = new EditorViewModel(fixture.Root, fixture.Presets, fixture.Cache);
        editor.AddFiles([fixture.Photo]); await ReadyAsync(editor);
        byte[] expected = FirstBgra(editor.Preview!);
        var oldPreview = editor.Preview;
        editor.ClearLutCommand.Execute(null);
        await UntilAsync(() => editor.Preview != oldPreview, "Clear saved preset");
        True(!editor.HasLut && editor.HasUnsavedChanges, "Clear did not remove both path and source reference.");
        var clearPreview = editor.Preview;
        editor.UndoCommand.Execute(null);
        await UntilAsync(() => editor.Preview != clearPreview, "Undo saved preset clear");
        True(editor.HasLut && !editor.HasUnsavedChanges && expected.SequenceEqual(FirstBgra(editor.Preview!)),
            "Undo did not recover the saved preset and pixels.");
        editor.RedoCommand.Execute(null);
        True(!editor.HasLut, "Redo did not clear the filter source.");
        await SaveAsync(editor, fixture.Photo);
        True((await RecipeStore.LoadAsync(fixture.Photo)) is { LutSource: null, LutPath: null }, "Explicit clear/save retained a hidden filter source.");
        editor.UndoCommand.Execute(null);
        editor.NaturalCommand.Execute(null);
        True(!editor.HasLut, "A built-in preset retained the previous catalog source.");
        editor.UndoCommand.Execute(null);
        True(editor.HasLut, "Undoing a built-in preset lost the catalog source.");
        editor.ResetCommand.Execute(null);
        True(!editor.HasLut, "Reset retained the catalog source.");
        editor.UndoCommand.Execute(null);
        True(editor.HasLut, "Undoing reset lost the catalog source.");
    }

    private static (string Root, string Presets, string Cache, string Photo) CreatePresetFixture(string name, FilterPresetKind kind)
    {
        string root = Path.Combine(TestDirectory, name);
        string presets = Path.Combine(root, "presets");
        string originals = Path.Combine(presets, "rapidraw", kind == FilterPresetKind.RapidRawFilm ? "film" : "community-local", "originals");
        Directory.CreateDirectory(originals);
        File.WriteAllText(Path.Combine(originals, kind == FilterPresetKind.RapidRawFilm ? "fixture.cube" : "fixture.rrpreset"),
            kind == FilterPresetKind.RapidRawFilm ? IdentityCube :
            "{\"creator\":\"Synthetic test fixture\",\"presets\":[{\"preset\":{\"name\":\"Synthetic community\",\"adjustments\":{\"exposure\":1,\"saturation\":-30}}}]}");
        return (root, presets, Path.Combine(root, "cache"), CreateJpeg(name, 48, 32, 70, 95, 120));
    }

    private static async Task SelectFixturePresetAsync(EditorViewModel editor, FilterPresetKind kind)
    {
        var previous = editor.Preview;
        editor.SelectedFilter = editor.Filters.Single(p => p.Kind == kind);
        await UntilAsync(() => editor.HasLut && editor.Preview != previous, "Apply synthetic preset");
        True(!editor.HasError, editor.Error);
    }
}
