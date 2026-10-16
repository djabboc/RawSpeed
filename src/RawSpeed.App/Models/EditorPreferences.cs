namespace RawSpeed.App.Models;

public enum EditorAction
{
    Open, Save, Export, Undo, Redo, ToggleBefore, CycleComparison, Composition,
    Fit, ActualSize, ZoomIn, ZoomOut, Previous, Next, Rating0, Rating1, Rating2, Rating3, Rating4, Rating5,
}

/// <summary>Persisted UI preferences. Clone before editing so cancelling a dialog has no side effects.</summary>
public sealed class EditorPreferences
{
    public int Version { get; set; } = 1;
    public Dictionary<EditorAction, string> Shortcuts { get; set; } = CreateDefaultShortcuts();
    public double LibraryWidth { get; set; } = 210;
    public double ToolsWidth { get; set; } = 360;
    public double HistogramHeight { get; set; } = 130;
    public double ThumbnailSize { get; set; } = 100;

    public EditorPreferences Clone() => new()
    {
        Version = Version, Shortcuts = new Dictionary<EditorAction, string>(Shortcuts),
        LibraryWidth = LibraryWidth, ToolsWidth = ToolsWidth,
        HistogramHeight = HistogramHeight, ThumbnailSize = ThumbnailSize,
    };

    public static Dictionary<EditorAction, string> CreateDefaultShortcuts() => new()
    {
        [EditorAction.Open] = "Ctrl+O", [EditorAction.Save] = "Ctrl+S", [EditorAction.Export] = "Ctrl+E",
        [EditorAction.Undo] = "Ctrl+Z", [EditorAction.Redo] = "Ctrl+Y;Ctrl+Shift+Z",
        [EditorAction.ToggleBefore] = "B", [EditorAction.CycleComparison] = "Y", [EditorAction.Composition] = "C",
        [EditorAction.Fit] = "F", [EditorAction.ActualSize] = "D",
        [EditorAction.ZoomIn] = "WheelUp;Add;Ctrl+OemPlus", [EditorAction.ZoomOut] = "WheelDown;Subtract;Ctrl+OemMinus",
        [EditorAction.Previous] = "Left", [EditorAction.Next] = "Right",
        [EditorAction.Rating0] = "D0;NumPad0", [EditorAction.Rating1] = "D1;NumPad1", [EditorAction.Rating2] = "D2;NumPad2",
        [EditorAction.Rating3] = "D3;NumPad3", [EditorAction.Rating4] = "D4;NumPad4", [EditorAction.Rating5] = "D5;NumPad5",
    };
}
