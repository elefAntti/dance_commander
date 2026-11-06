using System.IO;
using DanceCommander.App.ViewModels;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Preview;
using DanceCommander.Providers.Archive;

namespace DanceCommander.App.Services;

public interface IPreviewLoader
{
    Task<PreviewViewModel> LoadAsync(FsEntry entry, CancellationToken ct);
}

/// <summary>
/// Reads enough of a file to show it (§6.11).
/// </summary>
/// <remarks>
/// Everything here is capped and cancellable, because a preview is raised by a keystroke on
/// whatever the cursor happens to be on. The file may be a gigabyte, may be on a server, may be
/// inside a compressed tarball — so it is read through the provider like anything else, never
/// more than the caps below, and the read is abandoned the moment the user moves on. Read-only
/// throughout: that is what makes previewing a remote or in-archive file no harder than a local
/// one, since nothing has to be written back.
/// </remarks>
public sealed class PreviewLoader(IProviderRegistry providers) : IPreviewLoader
{
    /// <summary>Enough text to judge a file by; far more than fits on a screen.</summary>
    private const int TextCap = 1 << 20;

    /// <summary>An image has to be read whole to decode, so the cap is what we will spend on one.</summary>
    private const long ImageCap = 32L << 20;

    /// <summary>Same reasoning as <see cref="ImageCap"/>, sized up for a format that runs bigger.</summary>
    private const long PdfCap = 64L << 20;

    /// <summary>Asked of the shell for the last-resort thumbnail: large enough to fill the pane.</summary>
    private const int ThumbnailPixels = 1024;

    /// <summary>How long a shell extractor gets before the preview gives up on it.</summary>
    private static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromSeconds(5);

    public async Task<PreviewViewModel> LoadAsync(FsEntry entry, CancellationToken ct)
    {
        try
        {
            var provider = providers.Resolve(entry.Uri);
            var size = entry.Size;

            // Reading a file that is only online downloads it, and a preview follows the cursor:
            // arrowing down a OneDrive folder would fetch every file passed on the way. Asked again
            // before declining, since the listing may predate the file being opened elsewhere.
            if (entry.Attributes.HasFlag(FsEntryAttributes.CloudOnly) &&
                (await provider.StatAsync(entry.Uri, ct).ConfigureAwait(false))?.Attributes.HasFlag(FsEntryAttributes.CloudOnly) != false)
            {
                return await LoadCloudOnlyAsync(entry, size, ct).ConfigureAwait(false);
            }

            // One read serves both questions: a header long enough to identify an image, and up to
            // the text cap for everything else.
            await using var stream = await provider.OpenReadAsync(entry.Uri, ct).ConfigureAwait(false);
            var head = await ReadAtMostAsync(stream, TextCap + 1, ct).ConfigureAwait(false);

            if (ImageSignature.Detect(head) is { } format)
                return await LoadImageAsync(entry, provider, format, size, head, ct).ConfigureAwait(false);

            if (PdfSignature.Detect(head))
                return await LoadPdfAsync(entry, provider, size, head, ct).ConfigureAwait(false);

            var truncated = head.Length > TextCap;
            var inspection = TextInspector.Inspect(
                truncated ? head.AsSpan(0, TextCap) : head, truncated);

            if (inspection.IsText)
                return new PreviewViewModel(entry.Name, DescribeText(size, inspection), inspection);

            var archive = await ArchiveSniffer.DetectAsync(new MemoryStream(head), ct).ConfigureAwait(false);

            // Not text and not an image to us, but very possibly one to Windows. Tried ahead of the
            // archive sentence because a .docx *is* a zip: "press Enter to browse inside it" is
            // true and far less use than the first page of the document, and Enter still works.
            if (await TryShellThumbnailAsync(entry, size, ct).ConfigureAwait(true) is { } thumbnail)
                return thumbnail;

            // Nothing renders it. Saying what it is beats showing nothing, and for an archive it is
            // also actionable. A document that happens to be a zip gets neither the archive
            // sentence nor its advice, since Enter opens those in their application instead.
            return new PreviewViewModel(
                entry.Name,
                PreviewKind.Unsupported,
                Format.Size(size),
                ZipDocuments.IsDocument(entry.Name)
                    ? "Windows has no preview picture for this document. Press Enter to open it."
                    : archive.Kind != ArchiveKind.None
                        ? $"This is a {archive.Label} archive. Press Enter to browse inside it."
                        : "This is not text or an image, so there is nothing useful to show.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreviewViewModel(entry.Name, PreviewKind.Failed, Format.Size(entry.Size), ex.Message);
        }
    }

    private static async Task<PreviewViewModel> LoadImageAsync(
        FsEntry entry,
        IFileSystemProvider provider,
        string format,
        long size,
        byte[] head,
        CancellationToken ct)
    {
        if (size > ImageCap)
        {
            // Too big for us to decode is not too big for the shell, which has either cached a
            // thumbnail already or knows how to read one out without loading the whole file.
            return await TryShellThumbnailAsync(entry, size, ct).ConfigureAwait(true)
                ?? new PreviewViewModel(
                    entry.Name, PreviewKind.Unsupported, $"{format} · {Format.Size(size)}",
                    "This image is too large to preview.");
        }

        // The header read above is usually the whole file; only go back for the rest when it is not.
        var bytes = head;
        if (head.Length > TextCap || size > head.Length)
        {
            await using var full = await provider.OpenReadAsync(entry.Uri, ct).ConfigureAwait(false);
            bytes = await ReadAtMostAsync(full, (int)Math.Min(size, ImageCap), ct).ConfigureAwait(false);
        }

        try
        {
            // Decoded off the UI thread and frozen, so the image can cross to it. The size given is
            // the turned one, so a portrait photo reads as portrait.
            var decoded = await Task.Run(() => ImagePreview.Decode(bytes), ct).ConfigureAwait(true);
            var image = decoded.Image;
            return new PreviewViewModel(
                entry.Name, $"{format} · {image.PixelWidth}×{image.PixelHeight} · {Format.Size(size)}", image);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            // A format WPF will not decode is often one the shell will -- and a variant it cannot
            // read either is a genuinely broken file, which is what the sentence then says.
            return await TryShellThumbnailAsync(entry, size, ct).ConfigureAwait(true)
                ?? new PreviewViewModel(
                    entry.Name, PreviewKind.Failed, $"{format} · {Format.Size(size)}",
                    "This image could not be decoded.");
        }
    }

    /// <summary>A file that is only online: its picture if OneDrive has one, never its bytes.</summary>
    /// <remarks>
    /// The shell's thumbnail is safe to ask for. OneDrive makes it on its side, and a cloud-only
    /// document, image, PDF and slide deck all came back with a picture while staying cloud-only.
    /// </remarks>
    private static async Task<PreviewViewModel> LoadCloudOnlyAsync(FsEntry entry, long size, CancellationToken ct) =>
        await TryShellThumbnailAsync(entry, size, ct).ConfigureAwait(true)
        ?? new PreviewViewModel(
            entry.Name, PreviewKind.Unsupported, $"Available when online · {Format.Size(size)}",
            "This file is only online, and previewing it would download it. Press Enter to open it.");

    /// <summary>
    /// Page one of a PDF, rendered by Windows' own PDF engine rather than the shell thumbnail path
    /// -- see <see cref="PdfPreview"/> for why. Unlike an image or a shell thumbnail, this has no
    /// local-only restriction: it works from the bytes already read off whichever provider owns
    /// the file.
    /// </summary>
    private static async Task<PreviewViewModel> LoadPdfAsync(
        FsEntry entry, IFileSystemProvider provider, long size, byte[] head, CancellationToken ct)
    {
        if (size > PdfCap)
        {
            return new PreviewViewModel(
                entry.Name, PreviewKind.Unsupported, $"PDF · {Format.Size(size)}",
                "This PDF is too large to preview.");
        }

        // The header read in LoadAsync is usually the whole file; only go back when it is not.
        var bytes = head;
        if (head.Length > TextCap || size > head.Length)
        {
            await using var full = await provider.OpenReadAsync(entry.Uri, ct).ConfigureAwait(false);
            bytes = await ReadAtMostAsync(full, (int)Math.Min(size, PdfCap), ct).ConfigureAwait(false);
        }

        var rendered = await PdfPreview.RenderFirstPageAsync(bytes, ct).ConfigureAwait(true);
        if (rendered is null)
        {
            return new PreviewViewModel(
                entry.Name, PreviewKind.Failed, $"PDF · {Format.Size(size)}",
                "This PDF could not be opened -- it may be encrypted or damaged.");
        }

        var pages = rendered.PageCount == 1 ? "1 page" : $"{rendered.PageCount} pages";
        var image = rendered.Image;
        return new PreviewViewModel(
            entry.Name,
            $"PDF · {pages} · {image.PixelWidth}×{image.PixelHeight} · {Format.Size(size)}",
            image);
    }

    /// <summary>The picture Explorer would show, when there is one and the file is local (§6.11).</summary>
    /// <remarks>
    /// The shell needs a path it can parse, so this is local-only: an entry inside an archive or on
    /// a server would have to be spilled to a temp file first, and a preview that silently downloads
    /// is not what F3 promises. Plain path, not <see cref="FsUri.ToLocalPath"/> — the shell rejects
    /// the "\\?\" prefix that one adds to long paths.
    ///
    /// The deadline is for wedged extractors rather than slow ones. It bounds how long a preview can
    /// sit blank; the abandoned thread finishes into nothing.
    /// </remarks>
    private static async Task<PreviewViewModel?> TryShellThumbnailAsync(FsEntry entry, long size, CancellationToken ct)
    {
        if (!entry.Uri.IsLocal)
            return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ThumbnailTimeout);

        try
        {
            var image = await ShellThumbnail
                .LoadAsync(entry.Uri.Path.Replace('/', '\\'), ThumbnailPixels, deadline.Token)
                .ConfigureAwait(true);

            return image is null
                ? null
                : new PreviewViewModel(
                    entry.Name,
                    $"Windows thumbnail · {image.PixelWidth}×{image.PixelHeight} · {Format.Size(size)}",
                    image);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // the extractor ran out of time; fall through to whatever we can say
        }
    }

    private static string DescribeText(long size, TextInspection inspection)
    {
        var parts = new List<string> { Format.Size(size), inspection.EncodingName, inspection.LineEndingLabel };

        if (inspection.LineEndingDetail is { } detail)
            parts.Add(detail);
        if (inspection.EndsWithoutNewline)
            parts.Add("no newline at end");
        if (inspection.Truncated)
            parts.Add("first 1 MB shown");

        return string.Join(" · ", parts);
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream stream, int limit, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(limit, 1 << 16)];
        using var collected = new MemoryStream();

        while (collected.Length < limit)
        {
            var wanted = (int)Math.Min(buffer.Length, limit - collected.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            collected.Write(buffer, 0, read);
        }

        return collected.ToArray();
    }
}
