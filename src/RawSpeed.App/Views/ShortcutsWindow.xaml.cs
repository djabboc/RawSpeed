using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RawSpeed.App.Infrastructure;
using RawSpeed.App.Models;
using RawSpeed.App.Services;

namespace RawSpeed.App.Views;

public sealed class ShortcutBindingRow : ObservableObject
{
    private string _gesture;
    public ShortcutBindingRow(EditorAction action, string gesture) { Action = action; _gesture = gesture; }
    public EditorAction Action { get; }
    public string Label => ShortcutService.Label(Action);
    public string Gesture { get => _gesture; set => SetProperty(ref _gesture, value); }
}

public partial class ShortcutsWindow : Window
{
    private readonly EditorPreferences _original;
    public ShortcutsWindow(EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        InitializeComponent();
        _original = preferences.Clone();
        Preferences = preferences.Clone();
        Rows = new ObservableCollection<ShortcutBindingRow>(Enum.GetValues<EditorAction>()
            .Select(action => new ShortcutBindingRow(action, preferences.Shortcuts.GetValueOrDefault(action, ""))));
        foreach (var row in Rows) row.PropertyChanged += (_, _) => ValidateRows();
        BindingsGrid.ItemsSource = Rows;
        ValidateRows();
    }

    public EditorPreferences Preferences { get; private set; }
    public ObservableCollection<ShortcutBindingRow> Rows { get; }

    public bool TryCreatePreferences(out EditorPreferences preferences, out string error)
    {
        preferences = _original.Clone();
        preferences.Shortcuts = Rows.ToDictionary(row => row.Action, row => row.Gesture);
        if (!ShortcutService.TryValidate(preferences, out error)) return false;
        foreach (var action in preferences.Shortcuts.Keys.ToArray())
        {
            ShortcutService.TryNormalize(preferences.Shortcuts[action], out string normalized, out _);
            preferences.Shortcuts[action] = normalized;
        }
        return true;
    }

    private void ValidateRows()
    {
        SaveButton.IsEnabled = TryCreatePreferences(out _, out string error);
        ValidationError.Text = error;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = EditorPreferences.CreateDefaultShortcuts();
        foreach (var row in Rows) row.Gesture = defaults[row.Action];
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        BindingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        BindingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!TryCreatePreferences(out var preferences, out string error)) { ValidationError.Text = error; return; }
        Preferences = preferences;
        DialogResult = true;
    }
}
