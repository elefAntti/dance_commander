using System.Diagnostics;
using System.Threading.Channels;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;

namespace DanceCommander.Providers.Local;

/// <summary>
/// One directory, watched live. <see cref="FileSystemWatcher"/> on its own is not enough for a
/// file manager; this wrapper adds the three things that make it reliable (§6.1):
/// <list type="number">
///   <item>Overflow. The OS drops events under churn. On Error we discard pending deltas and ask
///   for a full re-list rather than showing a listing that is quietly wrong.</item>
///   <item>Storms. Raw events are coalesced per name over a short quiet window, so saving a file
///   produces one row update rather than four.</item>
///   <item>Partial writes. Created fires before the writer has finished, so sizes are read at
///   flush time and a locked file keeps its last known values instead of showing 0 bytes.</item>
/// </list>
/// </summary>
public sealed class LocalDirectoryWatcher : IDirectoryWatcher
{
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(400);

    private readonly FsUri _directory;
    private readonly Func<string, FsEntry?> _stat;
    private readonly Channel<RawChange> _raw = Channel.CreateUnbounded<RawChange>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _fsw;
    private FileSystemWatcher? _place;
    private Task? _pump;

    public LocalDirectoryWatcher(FsUri directory, Func<string, FsEntry?> stat)
    {
        _directory = directory;
        _stat = stat;
    }

    public event EventHandler<DirectoryChangeBatch>? Changed;

    public event EventHandler? ResyncRequired;

    public event EventHandler<FsUri?>? Moved;

    private readonly record struct RawChange(DirectoryChangeKind Kind, string Name, string? OldName);

    public void Start()
    {
        _fsw = new FileSystemWatcher(_directory.ToLocalPath())
        {
            // 64 KB is the practical maximum for a non-paged pool buffer; bursty builds still
            // overflow it, which is what ResyncRequired exists for.
            InternalBufferSize = 64 * 1024,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
        };

        _fsw.Created += (_, e) => Publish(DirectoryChangeKind.Added, e.Name);
        _fsw.Changed += (_, e) => Publish(DirectoryChangeKind.Updated, e.Name);
        _fsw.Deleted += (_, e) => Publish(DirectoryChangeKind.Removed, e.Name);
        _fsw.Renamed += (_, e) =>
        {
            // A rename is a removal and an addition; treating it as one event would leave the
            // old row on screen under its old name.
            Publish(DirectoryChangeKind.Removed, e.OldName);
            Publish(DirectoryChangeKind.Added, e.Name);
        };
        _fsw.Error += (_, _) =>
        {
            Drain();
            ResyncRequired?.Invoke(this, EventArgs.Empty);
        };

        _fsw.EnableRaisingEvents = true;
        _pump = Task.Run(() => PumpAsync(_cts.Token));
        WatchPlace();
    }

    /// <summary>
    /// The folder's own name, watched from its parent: a watch inside a folder follows it when it
    /// is renamed or sent to the Recycle Bin (both a rename, to Windows) and says nothing.
    /// </summary>
    private void WatchPlace()
    {
        if (_directory.Parent is not { IsLocal: true } parent || _directory.IsRoot)
            return;

        try
        {
            _place = new FileSystemWatcher(parent.ToLocalPath(), _directory.Name)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName,
            };
            _place.Deleted += (_, _) => Moved?.Invoke(this, null);
            _place.Renamed += (_, e) =>
            {
                if (!string.Equals(e.OldName, _directory.Name, StringComparison.OrdinalIgnoreCase))
                    return;
                // Renamed in place: the same folder, under its new name. Moved out of the parent,
                // it arrives here as a deletion instead.
                Moved?.Invoke(this, string.Equals(e.Name, e.OldName, StringComparison.OrdinalIgnoreCase)
                    ? _directory
                    : parent.Child(e.Name ?? string.Empty));
            };
            _place.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A parent we may not watch: the folder is still watched, only its going unnoticed.
            _place?.Dispose();
            _place = null;
        }
    }

    private void Publish(DirectoryChangeKind kind, string? name)
    {
        if (!string.IsNullOrEmpty(name))
            _raw.Writer.TryWrite(new RawChange(kind, name, null));
    }

    private void Drain()
    {
        while (_raw.Reader.TryRead(out _))
        {
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await _raw.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var pending = new Dictionary<string, DirectoryChangeKind>(StringComparer.OrdinalIgnoreCase);
                var started = Stopwatch.StartNew();

                // Absorb until the directory goes quiet, but never hold a batch for long: a folder
                // under continuous write should still update, just not once per event.
                while (true)
                {
                    var readAny = false;
                    while (_raw.Reader.TryRead(out var change))
                    {
                        Merge(pending, change);
                        readAny = true;
                    }

                    if (!readAny || started.Elapsed >= MaxBatchDelay)
                        break;

                    await Task.Delay(QuietWindow, ct).ConfigureAwait(false);
                }

                if (pending.Count == 0)
                    continue;

                try
                {
                    var batch = Materialise(pending);
                    if (batch.Count > 0)
                        Changed?.Invoke(this, new DirectoryChangeBatch(batch));
                }
                catch (Exception)
                {
                    // A failure here must not silently end live updating and leave the pane
                    // showing a listing that quietly stops matching the disk.
                    ResyncRequired?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Merge(Dictionary<string, DirectoryChangeKind> pending, RawChange change)
    {
        if (!pending.TryGetValue(change.Name, out var existing))
        {
            pending[change.Name] = change.Kind;
            return;
        }

        var merged = (existing, change.Kind) switch
        {
            // Created then deleted inside one window never existed as far as the user is concerned.
            (DirectoryChangeKind.Added, DirectoryChangeKind.Removed) => (DirectoryChangeKind?)null,
            (DirectoryChangeKind.Added, _) => DirectoryChangeKind.Added,
            (DirectoryChangeKind.Removed, DirectoryChangeKind.Added) => DirectoryChangeKind.Updated,
            (_, DirectoryChangeKind.Removed) => DirectoryChangeKind.Removed,
            _ => DirectoryChangeKind.Updated,
        };

        if (merged is null)
            pending.Remove(change.Name);
        else
            pending[change.Name] = merged.Value;
    }

    private List<DirectoryChange> Materialise(Dictionary<string, DirectoryChangeKind> pending)
    {
        var batch = new List<DirectoryChange>(pending.Count);

        foreach (var (name, kind) in pending)
        {
            if (kind == DirectoryChangeKind.Removed)
            {
                batch.Add(new DirectoryChange(DirectoryChangeKind.Removed, name));
                continue;
            }

            // Metadata is read now, not when the event fired, so a file that was still being
            // written has its final size by the time the row appears.
            var entry = _stat(name);
            if (entry is null)
            {
                // It went away between the event and the flush: report it as gone.
                batch.Add(new DirectoryChange(DirectoryChangeKind.Removed, name));
                continue;
            }

            batch.Add(new DirectoryChange(kind, name, null, entry));
        }

        return batch;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_fsw is not null)
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }
        if (_place is not null)
        {
            _place.EnableRaisingEvents = false;
            _place.Dispose();
        }
        _raw.Writer.TryComplete();
        try
        {
            _pump?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
        }
        _cts.Dispose();
    }
}
