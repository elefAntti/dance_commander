using DanceCommander.Core.Columns;
using DanceCommander.Core.Watching;

namespace DanceCommander.Core.Fs;

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    Rename = 8,
    CreateDirectory = 16,

    /// <summary>Read streams are seekable, so archives can be browsed without downloading.</summary>
    RandomAccessRead = 32,

    /// <summary>Change notifications are available; <see cref="IFileSystemProvider.Watch"/> returns non-null.</summary>
    Watch = 64,

    /// <summary>Entries have a real path an external process can be handed (§6.6).</summary>
    ExecuteHost = 128,

    /// <summary>
    /// Every listing is a round trip to a server, slow enough that showing the last one while it is
    /// asked again is worth it (<see cref="Listing.ListingMemory"/>).
    /// </summary>
    Remote = 256,
}

/// <summary>
/// A provider whose locations differ in what their rows say — the application's own pages list
/// drives on one and a collection's files on another — so the columns are asked per location
/// rather than taken from <see cref="IFileSystemProvider.DefaultColumns"/>.
/// </summary>
public interface ILocationColumns
{
    ColumnSet ColumnsFor(FsUri location);
}

/// <summary>
/// A provider that has to open a connection before it can list anything, so the first listing on
/// a server spends its time connecting rather than reading. Asked so the pane can say which.
/// </summary>
public interface IConnectingProvider
{
    /// <summary>
    /// Who a listing of this location would have to connect to first, such as "user@host", or
    /// null when a connection is already open.
    /// </summary>
    string? ConnectingTo(FsUri location);

    /// <summary>
    /// Keeps the connection behind this location open until disposed, however long nothing is
    /// listed: a tab showing a server is still in use while someone reads it, and closing it as
    /// idle meant typing the password again on the next keypress.
    /// </summary>
    IDisposable KeepOpen(FsUri location);
}

/// <summary>
/// A provider whose files carry Unix permission bits, so a copy can hand them on and a file that
/// is meant to run can be made runnable (§6.8).
/// </summary>
/// <remarks>
/// Modes are the nine rwx bits as a number, the way chmod reads them in octal: 0755 is 493.
/// Setuid, setgid and sticky are left out on purpose; a copy is a new file, and those bits on a
/// file that has just changed are not the copier's to grant.
/// </remarks>
public interface IUnixModes
{
    /// <summary>The file's rwx bits, or null when they cannot be read.</summary>
    Task<int?> GetModeAsync(FsUri file, CancellationToken ct);

    Task SetModeAsync(FsUri file, int mode, CancellationToken ct);
}

/// <summary>
/// A provider that can tell when two addresses are one file: a <c>subst</c> drive, a junction or
/// <c>\\localhost\c$</c> give one file several names, and writing over "the other one" would
/// destroy the source (§1.6).
/// </summary>
public interface IFileIdentity
{
    /// <summary>True only when both exist and are the same file; false when unsure.</summary>
    Task<bool> IsSameFileAsync(FsUri a, FsUri b, CancellationToken ct);
}

/// <summary>
/// A provider that keeps a mark of the last change to a file that no one sets back: NTFS's change
/// journal moves on every write, and nothing that "preserves the date" (a VeraCrypt container
/// dismounted, a tool restoring times) restores it.
/// </summary>
public interface IChangeStamps
{
    /// <summary>The file's change stamp, or null when it can't be read.</summary>
    long? ChangeStamp(FsUri file);

    /// <summary>
    /// When the file was last written, renamed or made (NTFS's change time, or its creation time
    /// if later), by the clock of the machine that holds it, or null. Not something to trust
    /// against a program that sets it on purpose; enough to tell a file saved or pasted after a
    /// given moment.
    /// </summary>
    DateTimeOffset? ChangedAt(FsUri file);

    /// <summary>
    /// The change time alone, which the holder's clock sets on every write and rename and no copy
    /// brings along, or null where none is kept: what to read that clock by. <see cref="ChangedAt"/>
    /// won't do, as a copied file's modified time is its original's, by another clock.
    /// </summary>
    DateTimeOffset? ClockedAt(FsUri file);
}

/// <summary>
/// A provider that can move a folder link (a junction, a directory symbolic link) as itself,
/// leaving the folder it points to where it is.
/// </summary>
/// <remarks>
/// A move goes file by file, and walked through a link it moved the files of the folder the link
/// points to, outside everything selected (review, 2026-10-22). So a move hands each link here.
/// </remarks>
public interface IMovesLinks
{
    /// <summary>
    /// Moves the link at <paramref name="from"/> to <paramref name="to"/>, both on this provider,
    /// never over anything at <paramref name="to"/>. Throws, with the link where it was, when
    /// <paramref name="from"/> is no longer a link or the same kind can't be made at the target.
    /// </summary>
    Task MoveLinkAsync(FsUri from, FsUri to, CancellationToken ct);
}

/// <summary>
/// A provider that copies between two of its own files itself, keeping what a stream of bytes
/// can't carry: on Windows, the file's last-modified date, attributes and alternate streams (the
/// "downloaded from the internet" mark), which a copy through <see cref="IFileSystemProvider.OpenReadAsync"/>
/// and <see cref="IFileSystemProvider.OpenWriteAsync"/> lost. Permissions are the new folder's, as
/// in Explorer.
/// </summary>
public interface ICopiesWithin
{
    /// <summary>Whether this copy is one it does itself.</summary>
    bool CanCopyWithin(FsUri source, FsUri target);

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> as a commit does (§1.6): to a
    /// temporary name first, onto the real name only when whole, and over a file already there only
    /// when <paramref name="replace"/>; stopping when <paramref name="ct"/> is cancelled, with
    /// nothing left at the real name.
    /// </summary>
    /// <param name="durable">
    /// On the disk before it takes its name, whatever was there: for a move, whose source is
    /// deleted next, so a power cut can't leave a copy of zeros and no original.
    /// </param>
    /// <param name="moved">Told each count of bytes of the file's content, as they go.</param>
    /// <param name="mayReplace">
    /// Asked just before a file at the target is written over: false keeps it, and the copy fails.
    /// For what can change during a long copy, which the decision to replace never saw.
    /// </param>
    /// <returns>The size of what landed.</returns>
    Task<long> CopyWithinAsync(
        FsUri source, FsUri target, bool replace, bool durable, Action<long> moved, CancellationToken ct, Func<bool>? mayReplace = null);
}

/// <summary>
/// A provider whose files also have a page in a web interface: Nextcloud's Files app, for one.
/// </summary>
public interface IWebLinks
{
    /// <summary>
    /// Whether <paramref name="file"/> could have one, decided from the address alone, because
    /// it is asked each time a menu is built.
    /// </summary>
    bool MayHaveWebLink(FsUri file);

    /// <summary>
    /// The page that shows <paramref name="file"/> to someone who can already see it there.
    /// Null when the server has none to give.
    /// </summary>
    Task<Uri?> WebLinkAsync(FsUri file, CancellationToken ct);
}

/// <summary>
/// A provider whose server can search a tree itself: Nextcloud over WebDAV, where walking costs a
/// round trip per folder and the server answers the same question from its index in one.
/// </summary>
public interface ISearchingProvider
{
    /// <summary>
    /// What the server finds anywhere under <paramref name="root"/> for the words of a
    /// <see cref="Matching.NameQuery"/>, or null when it cannot search there and the caller walks
    /// instead. The server's idea of a match may be looser than ours, never stricter, so the
    /// caller filters the answer again.
    /// </summary>
    Task<IReadOnlyList<FsEntry>?> SearchAsync(FsUri root, IReadOnlyList<string> terms, CancellationToken ct);
}

/// <summary>
/// A write stream that lands under the file's real name only when committed (§1.6). Until then
/// the bytes go somewhere beside it, and disposing without committing throws them away and
/// leaves whatever had the name before exactly as it was.
/// </summary>
public interface ICommittableStream
{
    /// <param name="replace">
    /// Whether a file already at the name may be replaced. Only when the user said so: a file that
    /// appeared there while this one was being written (another job, another program) is not one
    /// anybody chose to replace, and committing over it regardless lost it.
    /// </param>
    Task CommitAsync(bool replace, CancellationToken ct);

    /// <summary>
    /// As above, and when <paramref name="durable"/>, on the disk itself before returning, not only
    /// in its cache: a move deletes its source next. A server's disk is the server's to flush, so
    /// a remote stream commits as it always does.
    /// </summary>
    Task CommitAsync(bool replace, bool durable, CancellationToken ct) => CommitAsync(replace, ct);
}

public static class WriteStreams
{
    /// <summary>
    /// Puts what was written under the real name. Every caller of
    /// <see cref="IFileSystemProvider.OpenWriteAsync"/> calls this once the last byte is written;
    /// a stream that has nothing to commit (a test's plain stream) is already where it belongs.
    /// </summary>
    public static Task CommitAsync(this Stream stream, bool replace, CancellationToken ct) =>
        stream is ICommittableStream committable ? committable.CommitAsync(replace, ct) : stream.FlushAsync(ct);

    /// <summary>As above, on the disk itself when <paramref name="durable"/> (a move's copy).</summary>
    public static Task CommitAsync(this Stream stream, bool replace, bool durable, CancellationToken ct) =>
        stream is ICommittableStream committable ? committable.CommitAsync(replace, durable, ct) : stream.FlushAsync(ct);

    /// <summary>What a commit throws when it finds a file at the name it was not told to replace.</summary>
    public static IOException Appeared(string name) =>
        new($"{name} appeared while it was being written, so it was left as it is and nothing was replaced.");
}

/// <summary>
/// A backend. Providers are resolved by scheme and know nothing about the UI; cross-provider
/// copy and move are the transfer service's job, not a provider's.
/// </summary>
public interface IFileSystemProvider
{
    string Scheme { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>Columns a listing from this provider offers, in addition to the standard set.</summary>
    ColumnSet DefaultColumns { get; }

    IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, CancellationToken ct);

    Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct);

    Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct);

    /// <summary>
    /// A stream for the file's new content. Nothing is at <paramref name="file"/> until the caller
    /// calls <see cref="WriteStreams.CommitAsync"/>: a write that is cancelled or fails part way
    /// must never leave a short file under the real name, or have already removed the old one.
    /// </summary>
    Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct);

    Task CreateDirectoryAsync(FsUri directory, CancellationToken ct);

    Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct);

    /// <summary>Move within this provider only. Cross-provider moves go through the transfer service.</summary>
    Task MoveAsync(FsUri from, FsUri to, CancellationToken ct);

    /// <summary>Null when the provider cannot report changes; the pane then falls back to manual refresh.</summary>
    IDirectoryWatcher? Watch(FsUri directory);
}
