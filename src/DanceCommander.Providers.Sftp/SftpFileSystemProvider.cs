using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace DanceCommander.Providers.Sftp;

/// <summary>
/// A remote filesystem over SSH (§6.8). Nothing here knows about the UI, and nothing above here
/// knows this is remote: a pane lists an sftp URI the same way it lists a folder.
/// </summary>
public sealed class SftpFileSystemProvider(ISftpConnections connections)
    : IFileSystemProvider, IConnectingProvider, IUnixModes
{
    public string Scheme => "sftp";

    public string? ConnectingTo(FsUri location)
    {
        var target = SftpTarget.From(location);
        return connections.IsConnected(target) ? null : target.ToString();
    }

    public IDisposable KeepOpen(FsUri location) => connections.Hold(SftpTarget.From(location));

    /// <summary>
    /// Reads are seekable because SSH.NET's file stream supports it, which is what lets the
    /// archive provider browse a zip on a server without downloading the whole thing first.
    /// </summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Delete |
        ProviderCapabilities.Rename | ProviderCapabilities.CreateDirectory |
        ProviderCapabilities.RandomAccessRead | ProviderCapabilities.Remote;

    /// <summary>
    /// Owner and permissions earn their place here where they do not on Windows: on a remote
    /// host they are the difference between a file you can write and one you cannot.
    /// </summary>
    /// <remarks>
    /// The Windows attribute letters go, because they are the same four flags spelled worse and
    /// nothing on a remote host ever sets them: it was a column of blanks sitting next to the
    /// column that actually answers the question.
    /// </remarks>
    public ColumnSet DefaultColumns { get; } = ColumnSet.Standard.Without(ColumnIds.Attributes).With(
        new ColumnDefinition(ColumnIds.Permissions, "Perms", ColumnKind.Text, 92),
        new ColumnDefinition(ColumnIds.Owner, "Owner", ColumnKind.Text, 80));

    public async IAsyncEnumerable<FsEntry> ListAsync(
        FsUri directory, [EnumeratorCancellation] CancellationToken ct)
    {
        var target = SftpTarget.From(directory);
        var client = await connections.GetAsync(target, ct).ConfigureAwait(false);
        var owners = await connections.GetUserNamesAsync(target, ct).ConfigureAwait(false);

        // The enumerator has to be driven by hand. ListDirectoryAsync only builds the sequence --
        // every error the server can report arrives later, while it is being read -- and C# will
        // not allow a yield inside a try that has a catch, so wrapping the foreach is not an
        // option. Getting this wrong means the translation below silently never runs.
        var enumerator = client.ListDirectoryAsync(directory.Path, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                ISftpFile file;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    file = enumerator.Current;
                }
                catch (Exception ex)
                {
                    throw Translate(ex, directory);
                }

                // The server lists these; they are navigation, not content, and the pane adds its own.
                if (file.Name is "." or "..")
                    continue;

                yield return ToEntry(file, directory, owners);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct)
    {
        var target = SftpTarget.From(path);
        var client = await connections.GetAsync(target, ct).ConfigureAwait(false);

        try
        {
            var file = await client.GetAsync(path.Path, ct).ConfigureAwait(false);
            var owners = await connections.GetUserNamesAsync(target, ct).ConfigureAwait(false);
            return ToEntry(file, path.Parent ?? path, owners);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public async Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct)
    {
        var client = await connections.GetAsync(SftpTarget.From(file), ct).ConfigureAwait(false);
        var hold = connections.Hold(SftpTarget.From(file));
        try
        {
            var stream = await client.OpenAsync(file.Path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
            return new HeldStream(stream, hold, file);
        }
        catch (Exception ex)
        {
            hold.Dispose();
            throw Translate(ex, file);
        }
    }

    /// <summary>
    /// Writes go to a sibling <c>.part</c> file and are renamed on commit, so an interrupted
    /// transfer never leaves something that looks like a finished file (§6.8), and a file being
    /// replaced stays whole until its replacement is (§1.6).
    /// </summary>
    public async Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct)
    {
        var client = await connections.GetAsync(SftpTarget.From(file), ct).ConfigureAwait(false);
        // One session for the whole upload, which therefore never looked busy to the idle reaper.
        var hold = connections.Hold(SftpTarget.From(file));

        try
        {
            var (partial, stream) = await CreatePartAsync(client, file.Path, ct).ConfigureAwait(false);

            return new PartStream(stream, hold, explain: ex => Explain(client, file, length, ex), commit: replace =>
            {
                if (!client.Exists(file.Path))
                {
                    client.RenameFile(partial, file.Path);
                    return;
                }

                if (!replace)
                    throw WriteStreams.Appeared(file.Name);

                // SFTP rename does not overwrite, and deleting the old file first lost it whenever
                // the rename then failed. So it steps aside, the new one takes the name, and only
                // then does the old one go; a failure in between puts it back. Its mode is handed
                // on first, while it is still there to read.
                KeepMode(client, file.Path, partial);
                var aside = TemporaryName.BesideInPath(file.Path, $".dc-old-{Guid.NewGuid():N}");
                client.RenameFile(file.Path, aside);
                try
                {
                    client.RenameFile(partial, file.Path);
                }
                catch (Exception ex)
                {
                    // Said where the old file is when it can't go back: a lost connection used to
                    // leave it under the .dc-old name with an error that only said "lost".
                    if (!TryRename(client, aside, file.Path))
                        throw new IOException($"{file.Name} could not be replaced, and the old one is now {aside}.", ex);
                    throw;
                }

                try
                {
#pragma warning disable RS0030 // §1.6: the file the user chose to replace, set aside, and only once its replacement is in place.
                    client.DeleteFile(aside);
#pragma warning restore RS0030
                }
                catch (Exception ex) when (ex is SshException or SocketException)
                {
                    // The new file is in place; the old one is left under a name that says what it is.
                }
            },
            abandon: () =>
            {
                if (client.Exists(partial))
                {
#pragma warning disable RS0030 // §1.6: our own unfinished .part, at a name checked free before writing; never the real name.
                    client.DeleteFile(partial);
#pragma warning restore RS0030
                }
            });
        }
        catch (Exception ex)
        {
            hold.Dispose();
            throw Explain(client, file, length, ex);
        }
    }

    /// <summary>
    /// <c>name.part</c>, or a numbered one when that name is taken: never opened over an existing
    /// file, which may be someone else's unfinished upload.
    /// </summary>
    private static async Task<(string Partial, Stream Stream)> CreatePartAsync(
        SftpClient client, string path, CancellationToken ct)
    {
        for (var n = 0; ; n++)
        {
            var partial = TemporaryName.BesideInPath(path, n == 0 ? ".part" : $".{n}.part");
            try
            {
                return (partial, await client
                    .OpenAsync(partial, FileMode.CreateNew, FileAccess.Write, ct)
                    .ConfigureAwait(false));
            }
            catch (SshException) when (n < 100 && client.Exists(partial))
            {
            }
        }
    }

    /// <returns>False when it could not be renamed; nothing is lost, it is still under its old name.</returns>
    private static bool TryRename(SftpClient client, string from, string to)
    {
        try
        {
            client.RenameFile(from, to);
            return true;
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts a reason on a write the server refused without giving one.
    /// </summary>
    /// <remarks>
    /// OpenSSH answers with the bare SSH_FX_FAILURE for anything it has no code for, which on a
    /// write is mostly a full disk or a filesystem mounted read-only. A VM that has been through a
    /// sleep of the host tends to remount its disk read-only, and "Failure" gave no hint of it.
    /// statvfs@openssh.com says which. It is asked only once the write has failed, so a working
    /// upload pays nothing; a server without the extension gets the plain sentence.
    /// </remarks>
    private static Exception Explain(SftpClient client, FsUri file, long? length, Exception ex)
    {
        if (ex is not SftpException { StatusCode: StatusCode.Failure })
            return Translate(ex, file);

        try
        {
            var status = client.GetStatus((file.Parent ?? file).Path);
            return WhyNotWritten(file, status.IsReadOnly, status.AvailableBlocks * status.BlockSize, length, ex);
        }
        catch (Exception)
        {
            return WhyNotWritten(file, readOnly: false, available: null, length, ex);
        }
    }

    internal static IOException WhyNotWritten(
        FsUri file, bool readOnly, ulong? available, long? length, Exception ex)
    {
        var target = SftpTarget.From(file);
        var directory = (file.Parent ?? file).Path;

        if (readOnly)
            return new IOException($"{target.Host} has {directory} on a read-only filesystem.", ex);

        if (available is { } free && (free == 0 || length is { } needed && free < (ulong)needed))
            return new IOException(
                length is { } size
                    ? $"{target.Host} is out of space: {Megabytes(free)} free, {Megabytes((ulong)size)} needed."
                    : $"{target.Host} is out of space.",
                ex);

        return new IOException($"The server could not write {file.Display}.", ex);
    }

    private static string Megabytes(ulong bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";

    /// <summary>
    /// Gives a replacement the permissions of the file it replaces.
    /// </summary>
    /// <remarks>
    /// Windows has no execute bit to send, so anything written from here arrives with the server's
    /// default mode, and uploading a new build of a binary over the old one left it unrunnable
    /// until someone typed chmod. The file being replaced already says what it should be. Only the
    /// nine rwx bits: setuid and setgid on a file that has just changed are not ours to carry over.
    /// A server that refuses the change still gets the upload; the mode is a courtesy, the bytes
    /// are the job.
    /// </remarks>
    private static void KeepMode(SftpClient client, string from, string to)
    {
        try
        {
            var old = client.GetAttributes(from);
            if (old.IsRegularFile)
                client.ChangePermissions(to, Digits(ModeOf(old)));
        }
        catch (Exception ex) when (ex is SshException or SocketException)
        {
        }
    }

    public async Task<int?> GetModeAsync(FsUri file, CancellationToken ct)
    {
        var client = await connections.GetAsync(SftpTarget.From(file), ct).ConfigureAwait(false);
        try
        {
            var attributes = await client.GetAttributesAsync(file.Path, ct).ConfigureAwait(false);
            return attributes.IsRegularFile ? ModeOf(attributes) : null;
        }
        catch (Exception ex)
        {
            throw Translate(ex, file);
        }
    }

    public async Task SetModeAsync(FsUri file, int mode, CancellationToken ct)
    {
        var client = await connections.GetAsync(SftpTarget.From(file), ct).ConfigureAwait(false);
        try
        {
            client.ChangePermissions(file.Path, Digits(mode));
        }
        catch (Exception ex)
        {
            throw Translate(ex, file);
        }
    }

    /// <summary>The nine rwx bits as a number, 0755 being 493.</summary>
    private static int ModeOf(SftpFileAttributes a)
    {
        static int Bits(bool r, bool w, bool x) => (r ? 4 : 0) | (w ? 2 : 0) | (x ? 1 : 0);
        return (Bits(a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute) << 6) |
               (Bits(a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute) << 3) |
               Bits(a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute);
    }

    /// <summary>
    /// A mode the way SSH.NET's ChangePermissions wants it: the octal digits written out as a
    /// decimal number, so 0755 is passed as 755. It throws for anything else.
    /// </summary>
    internal static short Digits(int mode) =>
        (short)(((mode >> 6) & 7) * 100 + ((mode >> 3) & 7) * 10 + (mode & 7));

    public async Task CreateDirectoryAsync(FsUri directory, CancellationToken ct)
    {
        var client = await connections.GetAsync(SftpTarget.From(directory), ct).ConfigureAwait(false);
        try
        {
            client.CreateDirectory(directory.Path);
        }
        catch (Exception ex)
        {
            throw Translate(ex, directory);
        }
    }

    public async Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct)
    {
        var client = await connections.GetAsync(SftpTarget.From(path), ct).ConfigureAwait(false);

        try
        {
            // §1.6: the provider's delete itself. Every caller of IFileSystemProvider.DeleteAsync is
            // banned and carries its own reason; this is where those reasons are carried out.
            var file = await client.GetAsync(path.Path, ct).ConfigureAwait(false);
#pragma warning disable RS0030 // §1.6: the provider's delete itself (above).
            if (file.IsDirectory && recursive)
                await DeleteTreeAsync(client, path.Path, ct).ConfigureAwait(false);
            else
                client.Delete(path.Path);
#pragma warning restore RS0030
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    private static async Task DeleteTreeAsync(SftpClient client, string path, CancellationToken ct)
    {
        await foreach (var child in client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (child.Name is "." or "..")
                continue;

            if (child.IsDirectory && !child.IsSymbolicLink)
                await DeleteTreeAsync(client, child.FullName, ct).ConfigureAwait(false);
#pragma warning disable RS0030 // §1.6: within the provider's own recursive delete (above); a link is removed, never followed.
            else
                client.Delete(child.FullName);
        }

        client.DeleteDirectory(path);
#pragma warning restore RS0030
    }

    public async Task MoveAsync(FsUri from, FsUri to, CancellationToken ct)
    {
        var target = SftpTarget.From(from);
        if (target != SftpTarget.From(to))
            throw new NotSupportedException("Moving between two servers goes through the transfer service.");

        var client = await connections.GetAsync(target, ct).ConfigureAwait(false);
        try
        {
            await client.RenameFileAsync(from.Path, to.Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, from);
        }
    }

    /// <summary>SFTP has no change notification, so the pane falls back to manual refresh (§6.5).</summary>
    public IDirectoryWatcher? Watch(FsUri directory) => null;

    private static FsEntry ToEntry(ISftpFile file, FsUri directory, IReadOnlyDictionary<int, string> owners)
    {
        var attributes = FsEntryAttributes.None;
        if (file.IsSymbolicLink)
            attributes |= FsEntryAttributes.Symlink;
        if (file.Name.StartsWith('.'))
            attributes |= FsEntryAttributes.Hidden;

        return new FsEntry(
            directory.Child(file.Name),
            file.Name,
            file.IsDirectory,
            file.IsRegularFile ? file.Length : 0,
            new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)),
            attributes,
            LinkTarget: null,
            Extra: new Dictionary<string, string>
            {
                [ColumnIds.Permissions] = Permissions(file),
                [ColumnIds.Owner] = owners.TryGetValue(file.UserId, out var owner) ? owner : file.UserId.ToString(),
            });
    }

    /// <summary>The familiar <c>drwxr-xr-x</c>, because that is what the server means.</summary>
    private static string Permissions(ISftpFile file)
    {
        Span<char> bits =
        [
            file.IsSymbolicLink ? 'l' : file.IsDirectory ? 'd' : '-',
            file.OwnerCanRead ? 'r' : '-',
            file.OwnerCanWrite ? 'w' : '-',
            file.OwnerCanExecute ? 'x' : '-',
            file.GroupCanRead ? 'r' : '-',
            file.GroupCanWrite ? 'w' : '-',
            file.GroupCanExecute ? 'x' : '-',
            file.OthersCanRead ? 'r' : '-',
            file.OthersCanWrite ? 'w' : '-',
            file.OthersCanExecute ? 'x' : '-',
        ];
        return new string(bits);
    }

    /// <summary>
    /// Turns library exceptions into sentences. "SftpPermissionDeniedException" is not something
    /// to put in front of someone who just wanted to open a folder (§1.3).
    /// </summary>
    internal static Exception Translate(Exception ex, FsUri where) => ex switch
    {
        SftpPathNotFoundException => new FileNotFoundException($"{where.Display} is not there.", ex),
        SftpPermissionDeniedException =>
            new UnauthorizedAccessException($"The server refused access to {where.Display}.", ex),
        SshAuthenticationException =>
            new UnauthorizedAccessException($"{SftpTarget.From(where)} did not accept those credentials.", ex),
        // The library hands on the socket's own error too when the server goes mid-read, "An
        // established connection was aborted by the software in your host machine." (the torture
        // run, 2026-10-14), so that is a lost connection as well.
        SshConnectionException or SocketException =>
            new IOException($"The connection to {SftpTarget.From(where).Host} was lost.", ex),
        SshOperationTimeoutException =>
            new TimeoutException($"{SftpTarget.From(where).Host} did not answer in time.", ex),
        _ => ex,
    };
}

/// <summary>
/// Writes to a temporary name and renames on commit; removes it otherwise. Anything that
/// inspects the target directory mid-transfer sees a <c>.part</c> file or nothing at all, never a
/// truncated file wearing the real name.
/// </summary>
internal sealed class PartStream(
    Stream inner, IDisposable hold, Func<Exception, Exception> explain, Action<bool> commit, Action abandon)
    : Stream, ICommittableStream
{
    private bool _finished;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => inner.SetLength(value);

    // A disk fills up part way through a file, not at the open, so the writes get the same
    // explanation the open does.
    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (Exception ex) when (Explained(ex) is { } explained)
        {
            throw explained;
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch (Exception ex) when (Explained(ex) is { } explained)
        {
            throw explained;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Explained(ex) is { } explained)
        {
            throw explained;
        }
    }

    /// <summary>The better exception, or null to let the original go on with its stack trace.</summary>
    private Exception? Explained(Exception ex)
    {
        var explained = explain(ex);
        return ReferenceEquals(explained, ex) ? null : explained;
    }

    public Task CommitAsync(bool replace, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;
        try
        {
            inner.Dispose();
            commit(replace);
            hold.Dispose();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            // A failed commit must not leave the partial file behind pretending to be a transfer
            // in progress. The original exception still travels; this is only cleanup.
            Abandon();

            if (Explained(ex) is { } explained)
                throw explained;
            throw;
        }
    }

    /// <summary>
    /// Not committed means abandoned. Closing used to commit, so a cancelled upload, closed on
    /// its way out by <c>await using</c>, put its first half over the real file.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _finished)
            return;

        _finished = true;
        try
        {
            inner.Dispose();
        }
        catch
        {
            // The stream is being thrown away; a failure to close it changes nothing.
        }

        Abandon();
    }

    private void Abandon()
    {
        using var released = hold;
        try
        {
            abandon();
        }
        catch
        {
            // Nothing further to try, and the caller is already handling a failure.
        }
    }
}

/// <summary>
/// A server file being read, holding its connection open until closed (see
/// <see cref="ISftpConnections.Hold"/>). Everything else passes straight through, seeking
/// included, which is what lets an archive on a server be browsed in place. A read that fails is
/// said in words, as a write's failure is: a download cut off said "Client not connected."
/// (the torture run, 2026-10-11).
/// </summary>
internal sealed class HeldStream(Stream inner, IDisposable hold, FsUri file) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (Exception ex) when (ex is SshException or SocketException)
        {
            throw SftpFileSystemProvider.Translate(ex, file);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or SocketException)
        {
            throw SftpFileSystemProvider.Translate(ex, file);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The hold goes even when closing fails (the connection already lost): kept, it held
            // the connection open for the rest of the session.
            try
            {
                inner.Dispose();
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
            {
                // A file only read loses nothing by not being closed on a server that is gone. Let
                // through, "Client not connected." took the place of the read's own failure as the
                // stream was disposed on the way out (the torture run, 2026-10-11).
            }
            finally
            {
                hold.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
