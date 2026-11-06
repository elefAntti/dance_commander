using System.Windows.Input;
using DanceCommander.Core.Commands;

namespace DanceCommander.App.Input;

/// <summary>Translates WPF key events into the registry's platform-neutral strokes.</summary>
public static class KeyStrokeMapper
{
    public static KeyStroke? From(Key key, ModifierKeys modifiers)
    {
        var name = Name(key);
        return name is null ? null : new KeyStroke(name, From(modifiers));
    }

    public static KeyModifiers From(ModifierKeys modifiers)
    {
        var result = KeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= KeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= KeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= KeyModifiers.Alt;
        return result;
    }

    private static string? Name(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Escape => "Escape",
        Key.Space => "Space",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Home => "Home",
        Key.End => "End",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        >= Key.F1 and <= Key.F24 => key.ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => key.ToString()[1..],
        // Ctrl+. opens this folder's menu (§6.18): "." is the shell's name for the current folder.
        Key.OemPeriod or Key.Decimal => ".",
        // Ctrl+, opens the settings, as in VS Code and Windows Terminal.
        Key.OemComma => ",",
        _ => null,
    };
}
