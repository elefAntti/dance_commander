using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using Microsoft.Extensions.Logging;

namespace DanceCommander.Core.Listing;

/// <summary>
/// Lists the server folder under the cursor into <see cref="ListingMemory"/> before it is opened,
/// so Enter finds it ready (PLAN.md §5, "Remote listings, remembered and fetched ahead").
/// </summary>
/// <remarks>
/// One fetch at a time: the cursor passes over far more folders than it opens, and each fetch is a
/// request to someone else's server. So a fetch starts only after the cursor has rested (the caller
/// says how long: nothing for a click, a moment for the keyboard), and the next one cancels it. A
/// navigation to the folder being fetched takes the fetch over (<see cref="Join"/>) rather than
/// asking the server a second time.
/// </remarks>
public sealed class ListingPrefetcher(
    IProviderRegistry providers, ListingMemory memory, Func<bool> enabled, ILogger<ListingPrefetcher> log)
{
    /// <summary>A listing this young is fresh enough that fetching it again would only cost the server.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private sealed class Fetch(FsUri folder)
    {
        public FsUri Folder { get; } = folder;

        public CancellationTokenSource Cancel { get; } = new();

        /// <summary>Ends the wait for the cursor to rest: a navigation wants it now.</summary>
        public TaskCompletionSource Now { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RememberedListing?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<RememberedListing?>(null);
    }

    private readonly Lock _gate = new();
    private Fetch? _current;

    /// <summary>
    /// The cursor is on <paramref name="folder"/>: list it once it has stayed there for
    /// <paramref name="rest"/>, unless it is not on a server, is fresh already, or the setting is off.
    /// </summary>
    public void Ahead(FsUri folder, TimeSpan rest)
    {
        if (!enabled() || !providers.TryResolve(folder, out var provider) || !ListingMemory.Covers(folder, provider))
        {
            Cancel();
            return;
        }

        // Only over a connection already open: a fetch must never be the thing that connects, or
        // asks for a password (NoOneAsks would decline it, but the attempt itself is a login).
        if (provider is IConnectingProvider connecting && connecting.ConnectingTo(folder) is not null)
        {
            Cancel();
            return;
        }

        // Fresh enough to show is all this decides: whatever it shows is listed again on arrival.
        // A stamp in the future (the clock set back) is no evidence of anything, so it is stale.
        if (memory.Recall(folder) is { } known && DateTime.UtcNow - known.ListedAt is var age
            && age >= TimeSpan.Zero && age < FreshFor)
        {
            Cancel();
            return;
        }

        Fetch fetch;
        lock (_gate)
        {
            if (_current?.Folder == folder)
                return;
            _current?.Cancel.Cancel();
            _current = fetch = new Fetch(folder);
        }

        // On a worker from the start: a provider's listing begins synchronously, and that part
        // would otherwise run on the UI thread under the cursor that just moved.
        fetch.Task = System.Threading.Tasks.Task.Run(() => RunAsync(fetch, provider, rest));
    }

    /// <summary>The cursor left the folders: whatever was about to be fetched is not wanted.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel.Cancel();
            _current = null;
        }
    }

    /// <summary>
    /// A navigation to <paramref name="folder"/> takes over the fetch running for it, or null when
    /// there is none. Taken over, it no longer waits for the cursor to rest, and only
    /// <paramref name="ct"/> (the navigation's own) can stop it: the cursor moving on as the pane
    /// leaves is not a reason to throw away the answer the pane is waiting for.
    /// </summary>
    public Task<RememberedListing?>? Join(FsUri folder, CancellationToken ct)
    {
        Fetch fetch;
        lock (_gate)
        {
            if (_current?.Folder != folder || _current.Cancel.IsCancellationRequested)
                return null;
            fetch = _current;
            _current = null;
        }

        fetch.Now.TrySetResult();
        ct.Register(fetch.Cancel.Cancel);
        return fetch.Task;
    }

    private async Task<RememberedListing?> RunAsync(Fetch fetch, IFileSystemProvider provider, TimeSpan rest)
    {
        var ct = fetch.Cancel.Token;
        try
        {
            // Out of the caller's scope: nobody is there to answer a question.
            DecisionScope.Current = NoOneAsks.Instance;

            if (rest > TimeSpan.Zero)
                await System.Threading.Tasks.Task.WhenAny(System.Threading.Tasks.Task.Delay(rest, ct), fetch.Now.Task).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Stamped as asked, not as answered (ListingMemory.Remember).
            var askedAt = DateTime.UtcNow;
            var entries = new List<FsEntry>();
            await foreach (var entry in provider.ListAsync(fetch.Folder, ct).ConfigureAwait(false))
                entries.Add(entry);

            var listing = new RememberedListing(entries, askedAt);
            memory.Remember(fetch.Folder, entries, askedAt);
            return listing;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Not the user's failure: they never asked. Opening the folder will say what is wrong.
            log.LogDebug(ex, "Fetching {Folder} ahead failed", fetch.Folder);
            return null;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, fetch))
                    _current = null;
            }
        }
    }
}
