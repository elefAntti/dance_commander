using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Providers.Local;

/// <summary>
/// A local write that goes to <c>name.part</c> and is renamed over the real name on commit
/// (§1.6), the way SFTP writes always have.
/// </summary>
/// <remarks>
/// Writing straight to the name, pre-sized to the full length, meant a cancelled copy left a file
/// of the right size with zeros after the cut-off, and with "Replace" the old file was already
/// gone before the first byte arrived. Now the old file stays whole until the new one is complete,
/// and the swap is one rename, which NTFS does atomically.
/// </remarks>
internal sealed class LocalPartStream : Stream, ICommittableStream
{
    private readonly FileStream _inner;
    private readonly string _part;
    private readonly string _target;
    private bool _finished;

    private LocalPartStream(FileStream inner, string part, string target)
    {
        _inner = inner;
        _part = part;
        _target = target;
    }

    public static LocalPartStream Open(string target, long? length)
    {
        var (part, stream) = CreatePart(target);
        try
        {
            if (length is > 0)
                stream.SetLength(length.Value); // pre-allocate to avoid fragmenting large copies
        }
        catch
        {
            stream.Dispose();
#pragma warning disable RS0030 // §1.6: the .part just made here with CreateNew, still empty.
            File.Delete(part);
#pragma warning restore RS0030
            throw;
        }

        return new LocalPartStream(stream, part, target);
    }

    /// <summary>
    /// <c>name.part</c>, or a numbered one when that name is taken. Never opened over an existing
    /// file: a <c>.part</c> beside the target may be someone else's, a browser's download in
    /// progress among them.
    /// </summary>
    private static (string Part, FileStream Stream) CreatePart(string target)
    {
        var names = PartNames(target).ToList();
        for (var n = 0; ; n++)
        {
            var part = names[n];
            try
            {
                return (part, new FileStream(
                    part, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 64 * 1024, FileOptions.Asynchronous));
            }
            catch (IOException) when (n < names.Count - 1 && File.Exists(part))
            {
            }
        }
    }

    public Task CommitAsync(bool replace, CancellationToken ct) => CommitAsync(replace, durable: false, ct);

    public async Task CommitAsync(bool replace, bool durable, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_finished, this);

        // Trimmed to what was written, since the file was pre-sized to what was expected.
        _inner.SetLength(_inner.Position);
        await _inner.FlushAsync(ct).ConfigureAwait(false);

        // On the disk before it takes the place of a file that was: otherwise a power cut just
        // after the rename could leave the name holding zeros where the old file had been. So too
        // when it is a move's, whose source is deleted next. Only then, since a new copy has
        // nothing to lose and a flush per file is slow.
        var exists = File.Exists(_target);
        if (exists && !replace)
            throw WriteStreams.Appeared(Path.GetFileName(_target));
        if (exists || durable)
            _inner.Flush(flushToDisk: true);
        await _inner.DisposeAsync().ConfigureAwait(false);

        SwapIn(_part, _target, replace);
        _finished = true;
    }

    /// <summary>
    /// A complete <c>.part</c> takes the target's name, in one rename. Shared with
    /// <see cref="LocalCopy"/>, which fills its <c>.part</c> by other means.
    /// </summary>
    /// <remarks>
    /// Windows refuses to rename over a file that is read-only, or that another program has open,
    /// and says "access denied" for both (the torture run, 2026-10-11); each is now said as itself.
    /// A file open elsewhere is "in use", so the transfer asks to Retry, which it couldn't while the
    /// error looked like a permission. A read-only file stays: a Replace here can't be undone, and
    /// one answer covers every clash after it, so clearing the mark (as a Delete does, which goes to
    /// the Recycle Bin) destroyed read-only files nobody had been asked about (review, 2026-10-11).
    /// </remarks>
    internal static void SwapIn(string part, string target, bool replace)
    {
        try
        {
#pragma warning disable RS0030 // §1.6: replaces the target in one rename, only with a complete file flushed to disk, and
            // only when the user said Replace; otherwise a file that appeared there stops the commit.
            File.Move(part, target, overwrite: replace);
#pragma warning restore RS0030
        }
        catch (IOException) when (!replace && File.Exists(target))
        {
            throw WriteStreams.Appeared(Path.GetFileName(target));
        }
        catch (UnauthorizedAccessException ex) when (replace && File.Exists(target))
        {
            if (File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                throw new IOException($"{Path.GetFileName(target)} is read-only, so it was not replaced. Clear its read-only mark to replace it.", ex);
            if (OpenElsewhere.Is(target))
                throw new InUseException([target], $"{Path.GetFileName(target)} is open in another program, so it cannot be replaced yet.");
            throw;
        }
    }

    /// <summary>
    /// The names a <c>.part</c> beside <paramref name="target"/> is tried under, in order: the
    /// target's name shortened where it is too long to take the suffix.
    /// </summary>
    internal static IEnumerable<string> PartNames(string target)
    {
        var folder = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileName(target);
        for (var n = 0; n <= 100; n++)
            yield return Path.Combine(folder, TemporaryName.Beside(name, n == 0 ? ".part" : $".{n}.part"));
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _inner.WriteAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _inner.WriteAsync(buffer, ct);

    /// <summary>Not committed means abandoned: the <c>.part</c> goes and the target is untouched.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_finished)
        {
            _finished = true;
            _inner.Dispose();
            try
            {
#pragma warning disable RS0030 // §1.6: our own .part (made with CreateNew), never committed; the target is untouched.
                File.Delete(_part);
#pragma warning restore RS0030
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Debris with an obvious name; the caller is already reporting what went wrong.
            }
        }

        base.Dispose(disposing);
    }
}
