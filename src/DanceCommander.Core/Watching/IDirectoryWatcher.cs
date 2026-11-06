using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Watching;

public enum DirectoryChangeKind
{
    Added,
    Updated,
    Removed,
    Renamed,
}

public sealed record DirectoryChange(DirectoryChangeKind Kind, string Name, string? OldName = null, FsEntry? Entry = null);

public sealed record DirectoryChangeBatch(IReadOnlyList<DirectoryChange> Changes);

/// <summary>
/// Change notifications for one directory, already coalesced by the implementation. Consumers
/// must handle <see cref="ResyncRequired"/>: under heavy churn the OS drops events, and the only
/// correct response is a full re-list.
/// </summary>
public interface IDirectoryWatcher : IDisposable
{
    event EventHandler<DirectoryChangeBatch>? Changed;

    event EventHandler? ResyncRequired;

    /// <summary>
    /// The watched folder itself went: renamed where it was (its new address), or deleted, sent to
    /// the Recycle Bin or moved away (null). A watch on the folder alone follows it silently, and
    /// a pane went on showing it as live and as it was (2026-10-14).
    /// </summary>
    event EventHandler<FsUri?>? Moved;

    void Start();
}
