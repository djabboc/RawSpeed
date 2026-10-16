using System.Windows.Input;
using RawSpeed.App.Models;

namespace RawSpeed.App.Services;

/// <summary>Validates and resolves keyboard / wheel gestures without executing application actions.</summary>
public sealed class ShortcutService
{
    private readonly Dictionary<Gesture, EditorAction> _bindings;
    private readonly record struct Gesture(Key Key, int Wheel, ModifierKeys Modifiers);

    public ShortcutService(EditorPreferences preferences)
    {
        if (!TryCompile(preferences, out _bindings, out string error)) throw new ArgumentException(error, nameof(preferences));
    }

    public EditorAction? MatchKey(Key key, ModifierKeys modifiers) =>
        _bindings.TryGetValue(new Gesture(key, 0, modifiers), out var action) ? action : null;

    public EditorAction? MatchWheel(int delta, ModifierKeys modifiers) =>
        delta != 0 && _bindings.TryGetValue(new Gesture(Key.None, Math.Sign(delta), modifiers), out var action) ? action : null;

    public static bool TryValidate(EditorPreferences preferences, out string error) => TryCompile(preferences, out _, out error);

    public static bool TryNormalize(string? text, out string normalized, out string error)
    {
        normalized = ""; error = "";
        if (text is null) { error = "快捷键不能为 null。"; return false; }
        if (text.Length > 512) { error = "快捷键文本过长。"; return false; }
        var result = new List<string>();
        var seen = new HashSet<Gesture>();
        foreach (string item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(item, out var gesture, out error)) return false;
            if (!seen.Add(gesture)) continue;
            result.Add(Format(gesture));
        }
        normalized = string.Join(';', result);
        return true;
    }

    public static string Label(EditorAction action) => action switch
    {
        EditorAction.Open => "打开照片", EditorAction.Save => "保存编辑配方", EditorAction.Export => "导出照片",
        EditorAction.Undo => "撤销", EditorAction.Redo => "重做", EditorAction.ToggleBefore => "原图对比",
        EditorAction.CycleComparison => "切换对比方式", EditorAction.Composition => "构图工具", EditorAction.Fit => "适合窗口",
        EditorAction.ActualSize => "100% 查看", EditorAction.ZoomIn => "放大", EditorAction.ZoomOut => "缩小",
        EditorAction.Previous => "上一张", EditorAction.Next => "下一张", EditorAction.Rating0 => "清除评分",
        EditorAction.Rating1 => "评分 1 星", EditorAction.Rating2 => "评分 2 星", EditorAction.Rating3 => "评分 3 星",
        EditorAction.Rating4 => "评分 4 星", EditorAction.Rating5 => "评分 5 星", _ => action.ToString(),
    };

    private static bool TryCompile(EditorPreferences? preferences, out Dictionary<Gesture, EditorAction> bindings, out string error)
    {
        bindings = []; error = "";
        if (preferences?.Shortcuts is null) { error = "快捷键设置缺失。"; return false; }
        foreach (var (action, text) in preferences.Shortcuts)
        {
            if (!Enum.IsDefined(action)) { error = "快捷键包含未知操作。"; return false; }
            if (!TryNormalize(text, out string normalized, out error)) { error = $"{Label(action)}：{error}"; return false; }
            foreach (string item in normalized.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                TryParse(item, out var gesture, out _);
                if (bindings.TryGetValue(gesture, out var previous))
                { error = $"{item} 同时用于“{Label(previous)}”和“{Label(action)}”，请修改其中一项。"; return false; }
                bindings.Add(gesture, action);
            }
        }
        return true;
    }

    private static bool TryParse(string text, out Gesture gesture, out string error)
    {
        gesture = default; error = "";
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        ModifierKeys modifiers = ModifierKeys.None;
        foreach (string part in parts[..^1])
        {
            ModifierKeys next = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModifierKeys.Control, "SHIFT" => ModifierKeys.Shift,
                "ALT" => ModifierKeys.Alt, "WIN" or "WINDOWS" => ModifierKeys.Windows, _ => ModifierKeys.None,
            };
            if (next == ModifierKeys.None || (modifiers & next) != 0)
            { error = $"无法识别组合键“{text}”。可使用 Ctrl、Shift、Alt、Win。"; return false; }
            modifiers |= next;
        }
        string keyText = parts[^1];
        if (keyText.Equals("WheelUp", StringComparison.OrdinalIgnoreCase) || keyText.Equals("WheelDown", StringComparison.OrdinalIgnoreCase))
        { gesture = new Gesture(Key.None, keyText.Equals("WheelUp", StringComparison.OrdinalIgnoreCase) ? 1 : -1, modifiers); return true; }
        if (keyText.Length == 1 && keyText[0] is >= '0' and <= '9') keyText = "D" + keyText;
        keyText = keyText.ToUpperInvariant() switch { "PLUS" => "OemPlus", "MINUS" => "OemMinus", _ => keyText };
        if (keyText.Length == 0 || char.IsDigit(keyText[0]) || !Enum.TryParse<Key>(keyText, true, out var key) || !Enum.IsDefined(key)
            || key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.Escape
                or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        { error = $"无法识别按键“{parts[^1]}”。Esc 保留用于退出当前工具。"; return false; }
        gesture = new Gesture(key, 0, modifiers);
        return true;
    }

    private static string Format(Gesture gesture)
    {
        string prefix = "";
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) prefix += "Ctrl+";
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) prefix += "Shift+";
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) prefix += "Alt+";
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) prefix += "Win+";
        return prefix + (gesture.Wheel == 0 ? gesture.Key.ToString() : gesture.Wheel > 0 ? "WheelUp" : "WheelDown");
    }
}
