namespace DanceCommander.Core.Commands;

/// <summary>
/// What a key means in most other programs, so a key chosen for one of ours can be warned about
/// before it surprises (SETTINGS_PLAN.md §7). Ctrl+Shift+A taken for "add to collection" was the
/// case that started it: it is Select none nearly everywhere else.
/// </summary>
/// <remarks>
/// Explorer, browsers, editors and the commanders. Only keys whose meaning is widespread enough to
/// be in the fingers; each lists the commands of ours that are that meaning, which may have it
/// without a warning.
/// </remarks>
public static class KeyConventions
{
    private static readonly Dictionary<KeyStroke, (string Meaning, string[] Ours)> Table = new()
    {
        [KeyStroke.Parse("Ctrl+C")] = ("Copy", ["dc.clipboardCopy"]),
        [KeyStroke.Parse("Ctrl+X")] = ("Cut", ["dc.clipboardCut"]),
        [KeyStroke.Parse("Ctrl+V")] = ("Paste", ["dc.paste"]),
        [KeyStroke.Parse("Ctrl+Z")] = ("Undo", ["dc.undo"]),
        [KeyStroke.Parse("Ctrl+Y")] = ("Redo", []),
        [KeyStroke.Parse("Ctrl+A")] = ("Select all", ["dc.selectAll"]),
        [KeyStroke.Parse("Ctrl+Shift+A")] = ("Select none", ["dc.selectNone"]),
        [KeyStroke.Parse("Ctrl+S")] = ("Save", []),
        [KeyStroke.Parse("Ctrl+F")] = ("Find", ["dc.search"]),
        [KeyStroke.Parse("Ctrl+P")] = ("Print", []),
        [KeyStroke.Parse("Ctrl+N")] = ("New window", []),
        [KeyStroke.Parse("Ctrl+T")] = ("New tab", ["dc.newTab"]),
        [KeyStroke.Parse("Ctrl+W")] = ("Close tab", ["dc.closeTab"]),
        [KeyStroke.Parse("Ctrl+Shift+T")] = ("Reopen closed tab", []),
        [KeyStroke.Parse("Ctrl+Tab")] = ("Next tab", ["dc.cycleTab", "dc.nextTab"]),
        [KeyStroke.Parse("Ctrl+PageDown")] = ("Next tab", ["dc.nextTab", "dc.cycleTab"]),
        [KeyStroke.Parse("Ctrl+PageUp")] = ("Previous tab", ["dc.previousTab", "dc.cycleTabBack"]),
        [KeyStroke.Parse("Ctrl+Shift+Tab")] = ("Previous tab", ["dc.cycleTabBack", "dc.previousTab"]),
        [KeyStroke.Parse("Ctrl+L")] = ("Go to the address bar", ["dc.focusAddress"]),
        [KeyStroke.Parse("Ctrl+D")] = ("Bookmark", ["dc.bookmark"]),
        [KeyStroke.Parse("Ctrl+R")] = ("Refresh", ["dc.refresh"]),
        [KeyStroke.Parse("F5")] = ("Refresh in Explorer and browsers, Copy in commanders", ["dc.refresh", "dc.copy"]),
        [KeyStroke.Parse("F2")] = ("Rename", ["dc.rename", "dc.renameBookmark"]),
        [KeyStroke.Parse("F3")] = ("View in commanders, Find next elsewhere", ["dc.preview"]),
        [KeyStroke.Parse("F4")] = ("Edit in commanders", ["dc.edit"]),
        [KeyStroke.Parse("Shift+F4")] = ("New file in commanders", ["dc.newFile"]),
        [KeyStroke.Parse("F6")] = ("Move in commanders", ["dc.move", "dc.moveBookmarkToGroup"]),
        [KeyStroke.Parse("F7")] = ("New folder in commanders", ["dc.newFolder", "dc.newCollection", "dc.saveShelf"]),
        [KeyStroke.Parse("F8")] = ("Delete in commanders", ["dc.delete"]),
        [KeyStroke.Parse("Ctrl+Shift+N")] = ("New folder in Explorer", ["dc.newFolder"]),
        [KeyStroke.Parse("Delete")] = ("Delete", ["dc.delete", "dc.deleteCollection", "dc.removeFromCollection", "dc.removeFromShelf", "dc.forgetVisit", "dc.removeBookmark"]),
        [KeyStroke.Parse("Shift+Delete")] = ("Delete permanently", ["dc.deletePermanently"]),
        [KeyStroke.Parse("Enter")] = ("Open", ["dc.open"]),
        [KeyStroke.Parse("Escape")] = ("Cancel", ["dc.dismiss"]),
        [KeyStroke.Parse("Alt+Left")] = ("Back", ["dc.back"]),
        [KeyStroke.Parse("Alt+Right")] = ("Forward", ["dc.forward"]),
        [KeyStroke.Parse("Alt+Up")] = ("Up one level in Explorer", ["dc.up"]),
        [KeyStroke.Parse("Alt+Enter")] = ("Properties", []),
        [KeyStroke.Parse("Alt+F4")] = ("Close the window", []),
        [KeyStroke.Parse("Ctrl+Shift+P")] = ("Command palette", ["dc.palette"]),
        [KeyStroke.Parse("Ctrl+,")] = ("Settings", ["dc.settings"]),
        [KeyStroke.Parse("F1")] = ("Help", []),
        [KeyStroke.Parse("Ctrl+Q")] = ("Quit", []),
        [KeyStroke.Parse("Insert")] = ("Mark in commanders", ["dc.mark"]),
        [KeyStroke.Parse("Space")] = ("Mark in commanders", ["dc.markWithSpace"]),
    };

    /// <summary>
    /// What the key usually does, when that is not what this command does; null when the key is
    /// free of expectations or the command is the very thing it means.
    /// </summary>
    public static string? ConflictFor(KeyStroke stroke, string commandId) =>
        Table.TryGetValue(stroke, out var convention) && !convention.Ours.Contains(commandId, StringComparer.Ordinal)
            ? convention.Meaning
            : null;
}
