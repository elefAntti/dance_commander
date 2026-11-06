using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Transfer;

[Flags]
public enum DragModifiers
{
    None = 0,

    /// <summary>Forces a copy, as it does everywhere else in Windows.</summary>
    Control = 1,

    /// <summary>Forces a move.</summary>
    Shift = 2,
}

public enum DropAction
{
    /// <summary>Refuse the drop. The cursor says so before the user lets go.</summary>
    None,
    Copy,
    Move,

    /// <summary>Add the sources to a collection or the bookmarks: a reference each, nothing copied (§6.19).</summary>
    Collect,
}

/// <summary>
/// Decides what a drop would do, before it happens.
/// </summary>
/// <remarks>
/// Separated from the view so the rules can be stated and tested without a mouse: what a drag
/// means is a policy question, and the answer has to be known during DragOver — the cursor has to
/// promise the right thing while the button is still down, and then keep that promise.
///
/// The defaults follow Explorer, because muscle memory does: within one volume a drag moves, and
/// across volumes it copies. Anything leaving the machine copies regardless, since a move there
/// ends in deleting the original over a network that may drop halfway.
/// </remarks>
public static class DropPolicy
{
    /// <param name="preferCopy">
    /// The drag carries a place rather than a selection — the location icon in the address bar
    /// (§6.18). Picking up "where I am" and dropping it somewhere means "a copy of this, there",
    /// and a plain drag that quietly moved the folder you are standing in out from under you was
    /// the one surprise this default existed to prevent. Shift still moves.
    /// </param>
    public static DropAction Decide(
        IReadOnlyList<FsUri> sources, FsUri target, DragModifiers modifiers, bool targetIsWritable = true,
        bool preferCopy = false)
    {
        if (sources.Count == 0 || !targetIsWritable)
            return DropAction.None;

        foreach (var source in sources)
        {
            // Into itself, or into its own subfolder: the first is meaningless and the second is
            // a loop. Both are easy to do by accident with a mouse.
            if (source == target || Contains(source, target))
                return DropAction.None;

            // Already there. A copy onto its own parent would just make "file (2)", which is
            // never what a slipped drag meant.
            if (source.Parent == target && modifiers == DragModifiers.None)
                return DropAction.None;
        }

        if (modifiers.HasFlag(DragModifiers.Control))
            return DropAction.Copy;
        if (modifiers.HasFlag(DragModifiers.Shift))
            return DropAction.Move;

        if (preferCopy)
            return DropAction.Copy;

        return sources.All(source => SameVolume(source, target)) ? DropAction.Move : DropAction.Copy;
    }

    /// <summary>True when <paramref name="target"/> is inside <paramref name="source"/>.</summary>
    internal static bool Contains(FsUri source, FsUri target)
    {
        for (var parent = target.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent == source)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a move between these two would be a rename rather than a copy-and-delete. Drive
    /// letters for local paths, host and account for anything else; an archive is never the same
    /// volume as anything, because nothing can be moved into or out of one in place.
    /// </summary>
    internal static bool SameVolume(FsUri a, FsUri b)
    {
        if (a.IsArchiveEntry || b.IsArchiveEntry)
            return false;

        if (a.IsLocal != b.IsLocal)
            return false;

        if (!a.IsLocal)
        {
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Authority, b.Authority, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(VolumeOf(a), VolumeOf(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"C:" for a drive, "//server/share" for a UNC path, or empty when neither.</summary>
    private static string VolumeOf(FsUri uri)
    {
        var path = uri.Path;

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            // A UNC share is the volume: \\server\share. Two shares on one server are not one
            // volume, so both segments count.
            var segments = path[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 ? $"//{segments[0]}/{segments[1]}" : path;
        }

        return path.Length >= 2 && path[1] == ':' ? path[..2] : string.Empty;
    }
}
