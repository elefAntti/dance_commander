using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Navigation;

/// <summary>
/// What the window was showing when it closed, so the next start opens there (WORKSPACES_PLAN §4,
/// single-window step). In <c>%LOCALAPPDATA%</c> beside <c>window.json</c>: it describes this
/// machine's session, and a roaming copy would reopen one laptop's folders on another.
/// </summary>
/// <remarks>
/// Not saved: marks and the quick filter, which are about the moment — a filter you do not
/// remember setting would be silent filtering — and previews and output panels.
/// </remarks>
public sealed class SessionFile
{
    public bool DualPane { get; set; } = true;

    public bool RightActive { get; set; }

    public PaneSession Left { get; set; } = new();

    public PaneSession Right { get; set; } = new();
}

public sealed class PaneSession
{
    public List<TabSession> Tabs { get; set; } = [];

    /// <summary>Which of <see cref="Tabs"/> is in front.</summary>
    public int Active { get; set; }
}

/// <summary>One tab: its back/forward list, where in it the tab stands, and the row the cursor was on.</summary>
public sealed class TabSession
{
    /// <summary>Persisted URI strings, oldest first; the location is the one at <see cref="Position"/>.</summary>
    public List<string> History { get; set; } = [];

    public int Position { get; set; }

    public string? Cursor { get; set; }

    /// <summary>How far back and forward a tab remembers across a restart.</summary>
    public const int Reach = 20;

    /// <summary>A tab's history, trimmed to <see cref="Reach"/> either side of where it stands.</summary>
    public static TabSession Of(IReadOnlyList<FsUri> history, int position, string? cursor)
    {
        var first = Math.Max(0, position - Reach);
        var last = Math.Min(history.Count - 1, position + Reach);
        return new TabSession
        {
            History = history.Skip(first).Take(last - first + 1).Select(u => u.ToString()).ToList(),
            Position = position - first,
            Cursor = cursor,
        };
    }

    /// <summary>
    /// The history back as addresses, and the position within it — or null when there is nothing
    /// usable to go back to. Entries that no longer parse are dropped, the position moving with them.
    /// </summary>
    public (List<FsUri> History, int Position)? Read()
    {
        var parsed = new List<FsUri>();
        var position = -1;
        for (var i = 0; i < History.Count; i++)
        {
            if (!FsUri.TryParse(History[i], out var uri, out _))
                continue;
            if (i <= Position)
                position = parsed.Count;
            parsed.Add(uri);
        }

        return parsed.Count == 0 ? null : (parsed, Math.Max(position, 0));
    }
}

public static class SessionPlaces
{
    /// <summary>
    /// A local folder, or the nearest of its parents that still exists; anything else as it is.
    /// A place deleted since the last session opens its closest surviving parent rather than an
    /// error, or home — which would lose the one clue to where you were.
    /// </summary>
    /// <remarks>
    /// Never above the path's own drive or share. The parent of <c>\\server</c> is <c>\</c>, which
    /// Windows reads as the root of whatever drive the process is on: a share that did not answer
    /// came back as C:\, and was offered as the place to make its folders in (review, 2026-10-29).
    /// </remarks>
    public static FsUri NearestExisting(FsUri uri, Func<string, bool> directoryExists)
    {
        if (!uri.IsLocal || uri.IsView)
            return uri;

        var root = AsFolder(Path.GetPathRoot(uri.Path.Replace('/', '\\')) ?? "");
        for (var at = uri; at is not null; at = at.Parent)
        {
            if (!AsFolder(at.Path.Replace('/', '\\')).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                break;
            if (at.IsLocal && directoryExists(at.ToLocalPath()))
                return at;
        }

        return uri;
    }

    /// <summary>"C:" and "C:\" alike as "C:\", so a root compares with the paths under it.</summary>
    private static string AsFolder(string path) => path.TrimEnd('\\') + '\\';
}
