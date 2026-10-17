# Application integration checks

This dependency-free console runner uses a real STA thread and WPF dispatcher, without opening an application window or operating system dialogs. It creates tiny JPEG fixtures in a unique temporary directory and removes them after the run.

```powershell
dotnet run --project tests/RawSpeed.App.Tests -c Release
```

When a running application locks the normal Release output, use an isolated build configuration:

```powershell
dotnet run --project tests/RawSpeed.App.Tests -c AppTests -p:Optimize=true
```

Checks cover recipe roundtrips, atomic replacement, invalid/corrupt/oversized sidecars, rapid photo selection, per-photo edit sessions, slider history, undo/redo, dirty-state tracking, saving, crop/rotation/comparison/reset, latest-render behavior, missing-LUT recovery, recovery after a decoder failure, bounded thumbnail dimensions, and retrying the same photo after cancellation. The runner uses the public view-model API and actual JPEG decoding; it does not access private fields or replace application services with mocks.

This is model/application integration coverage. Actual window layout, pointer interaction, operating system dialogs and display scaling still require UI verification. Native RAW sample decoding and export are covered separately by the imaging probe.

`RecipePresetTests.cs` verifies version 1 migration, durable version 2 preset identity, cache deletion and installation relocation, unavailable source recovery, filter history and Cube BOM handling. `ImportTests.cs` verifies background enumeration of 1,024 generated files, UI work between batches, early selection, normalized-path deduplication, public collection changes, cancellation/retry, superseded work and disposal. These tests use generated local fixtures and do not require sample RAW files or downloaded community presets.
