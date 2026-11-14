using System.Collections.Concurrent;

namespace DanceCommander.Core.Fs;

/// <summary>A local copy made to be opened.</summary>
/// <param name="Unmarked">
/// It should carry the "from the internet" mark and the disk would not take it (a FAT RAM disk, a
/// redirected temp folder), so Windows would open it without Protected View or SmartScreen.
/// </param>
public sealed record OpenedCopy(string Path, bool Unmarked);

/// <summary>
/// Copies a file that has no path on this machine (on a server, inside an archive) to one that
/// does, so that Enter can hand it to the program Windows opens it with.
/// </summary>
/// <remarks>
/// <para>Each copy gets a folder of its own under <see cref="Root"/>, so it keeps its name (the
/// program shows it in its title bar) and two files of one name never meet. Nothing there is
/// ever written over: the bytes go to <c>name.part</c>, made new, and take the name only when
/// whole, so a download that stops leaves something that says it is incomplete.</para>
/// <para>The copy is read-only. Saving it would change only the copy, never the file it came
/// from, and an editor that says "read-only" on Save says so at the moment it matters; Save As
/// still puts the work somewhere real.</para>
/// <para>Nothing removes the copies yet. DESIGN.md §1.6 allows removing one nobody has changed,
/// since its original is still whole; one with the flag cleared and saved into is the user's
/// work and is never removed silently.</para>
/// </remarks>
public sealed class OpenedCopies(IProviderRegistry providers, string? root = null)
{
    private const int BufferSize = 81920;

    private readonly ConcurrentDictionary<FsUri, byte> _fetching = new();

    /// <summary>Where the copies go: <c>%TEMP%\DanceCommander\Opened</c>.</summary>
    public string Root { get; } = root ?? Path.Combine(Path.GetTempPath(), "DanceCommander", "Opened");

    /// <summary>
    /// Whether the file comes from a server rather than from inside an archive on this machine,
    /// which is what the pane calls it while it waits: downloading or extracting.
    /// </summary>
    public static bool IsDownload(FsUri file) => !Outermost(file).IsLocal;

    /// <summary>
    /// Copies <paramref name="file"/> to a new local path and returns it; null when a copy of the
    /// same file is already on its way, which will be opened when it arrives.
    /// </summary>
    /// <param name="moved">Told the bytes copied so far, as they go.</param>
    public async Task<OpenedCopy?> FetchAsync(FsEntry file, Action<long>? moved, CancellationToken ct)
    {
        if (!_fetching.TryAdd(file.Uri, 0))
            return null;
        try
        {
            return await CopyAsync(file, moved, ct).ConfigureAwait(false);
        }
        finally
        {
            _fetching.TryRemove(file.Uri, out _);
        }
    }

    private async Task<OpenedCopy> CopyAsync(FsEntry file, Action<long>? moved, CancellationToken ct)
    {
        var provider = providers.Resolve(file.Uri);
        var folder = NewFolder();
        var name = ForeignName.ToFileName(file.Name);
        var final = Path.Combine(folder, name);
        var part = Path.Combine(folder, TemporaryName.Beside(name, ".part"));

        long done = 0;
        await using (var source = await provider.OpenReadAsync(file.Uri, ct).ConfigureAwait(false))
        await using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                moved?.Invoke(done);
            }
        }

        // A stream can end early without saying so (a dropped connection, a damaged archive), and
        // a cut copy opened as whole makes the original look broken. As the transfer does, a short
        // count is checked against a fresh look: the file may only have shrunk since it was listed.
        if (!file.SizeUnknown && done < file.Size)
        {
            var now = await provider.StatAsync(file.Uri, ct).ConfigureAwait(false);
            if (now is null || done < now.Size)
                throw new IOException($"The copy ended after {done:N0} of {(now ?? file).Size:N0} bytes.");
        }

        // Marked while it is still the .part, so no moment has an unmarked download at the real
        // name; the stream goes with the rename. Explorer passes a zip's mark on to what it
        // extracts; a file from a server always gets one.
        var unmarked = false;
        if (IsDownload(file.Uri) || ZoneMark.IsFromInternet(Outermost(file.Uri).ToLocalPath()))
            unmarked = !ZoneMark.MarkFromInternet(part);

        // Two arguments: never over anything. The folder is new, so nothing should be there.
        File.Move(part, final);

        // Its own date, not today's, so a program that shows it (or a later copy) tells the truth.
        // A date Windows can't hold (before 1601, a server's "no date") leaves today's.
        if (file.Modified is { } modified && modified.UtcDateTime >= DateTime.FromFileTimeUtc(0))
            File.SetLastWriteTimeUtc(final, modified.UtcDateTime);
        File.SetAttributes(final, File.GetAttributes(final) | FileAttributes.ReadOnly);
        return new OpenedCopy(final, unmarked);
    }

    /// <summary>A folder under <see cref="Root"/> that did not exist before.</summary>
    private string NewFolder()
    {
        while (true)
        {
            var folder = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
            if (Directory.Exists(folder))
                continue;
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>The file an archive entry is in, through any number of archives; else the file itself.</summary>
    private static FsUri Outermost(FsUri file)
    {
        while (file is { IsArchiveEntry: true, Inner: { } container })
            file = container;
        return file;
    }
}
