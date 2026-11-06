using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DanceCommander.Core.Listing;

public static class AsyncBatching
{
    /// <summary>
    /// Groups a stream of entries into batches so the UI is touched once per batch rather than
    /// once per item, which is what keeps a directory with hundreds of thousands of entries from
    /// freezing the shell (§5.5).
    /// </summary>
    /// <remarks>
    /// The delay bound is checked as items arrive, so a source that stalls mid-batch holds those
    /// items until the next one appears. That is fine for local enumeration; when slow remote
    /// listings start streaming progressively this needs a real timer racing MoveNextAsync.
    /// </remarks>
    public static async IAsyncEnumerable<IReadOnlyList<T>> InBatchesAsync<T>(
        this IAsyncEnumerable<T> source,
        int maxSize,
        TimeSpan maxDelay,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var batch = new List<T>(maxSize);
        var since = Stopwatch.StartNew();

        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            batch.Add(item);
            if (batch.Count < maxSize && since.Elapsed < maxDelay)
                continue;

            yield return batch;
            batch = new List<T>(maxSize);
            since.Restart();
        }

        if (batch.Count > 0)
            yield return batch;
    }
}
