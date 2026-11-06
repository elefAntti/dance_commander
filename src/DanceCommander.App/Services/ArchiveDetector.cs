using System.IO;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.Archive;
using Microsoft.Extensions.DependencyInjection;

namespace DanceCommander.App.Services;

/// <summary>
/// Answers "would pressing Enter on this file open a folder?" by reading the file rather than its
/// name (§6.10).
/// </summary>
/// <remarks>
/// Deliberately cheap and deliberately silent. It runs on every Enter, so it reads one block and
/// no more; and a file it cannot read is simply not an archive, because a failed guess here must
/// fall through to "open it the normal way" rather than produce an error about a question the
/// user never asked.
/// </remarks>
public static class ArchiveDetector
{
    /// <summary>Files this size or smaller cannot hold an archive we would want to open.</summary>
    private const long SmallestPlausibleArchive = 20;

    public static Task<bool> IsBrowsableAsync(FsUri file, IServiceProvider services, CancellationToken ct) =>
        IsBrowsableAsync(file, services.GetRequiredService<IProviderRegistry>(), ct);

    public static async Task<bool> IsBrowsableAsync(FsUri file, IProviderRegistry providers, CancellationToken ct)
    {
        try
        {
            if (!providers.TryResolve(file, out var provider))
                return false;

            var stat = await provider.StatAsync(file, ct).ConfigureAwait(false);
            if (stat is null || stat.Size < SmallestPlausibleArchive)
                return false;

            await using var stream = await provider.OpenReadAsync(file, ct).ConfigureAwait(false);
            if (!stream.CanSeek)
                return false; // Sniffing a remote file would mean downloading it; not on Enter.

            var format = await ArchiveSniffer.DetectAsync(stream, ct).ConfigureAwait(false);
            return format.Kind != ArchiveKind.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
