using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace DanceCommander.App.Services;

/// <summary>
/// Renders page one of a PDF, using the WinRT engine Windows has shipped since 8.1 for exactly
/// this (§6.11).
/// </summary>
/// <remarks>
/// This is not the shell thumbnail path in <see cref="ShellThumbnail"/>, and deliberately so: no
/// PDF thumbnail provider is registered on a stock Windows install (Explorer's PDF preview is the
/// reading-pane <c>IPreviewHandler</c>, declined for the same reason as a WebView -- see DESIGN.md §1.4),
/// so asking the shell for one reliably fails. <c>Windows.Data.Pdf</c> is a different mechanism
/// entirely: it decodes the file itself, which means it also has none of <see cref="ShellThumbnail"/>'s
/// local-only restriction. It is handed the bytes already read off whatever provider owns the
/// file, so it works for a PDF over SFTP or inside an archive exactly as well as a local one.
/// </remarks>
public static class PdfPreview
{
    public sealed record Rendered(BitmapSource Image, uint PageCount);

    /// <summary>
    /// Null when Windows' own PDF engine cannot open the file -- encrypted, corrupt, or otherwise
    /// broken beyond what any reader on the machine would recover either.
    /// </summary>
    public static async Task<Rendered?> RenderFirstPageAsync(byte[] bytes, CancellationToken ct)
    {
        // The stream has to outlive LoadFromStreamAsync: PdfDocument reads it lazily rather than
        // slurping the content up front, so a page rendered after this returns needs it to still
        // be there. Disposing it as soon as loading finished (an earlier version of this did) does
        // not throw -- it just renders every page blank, which is a far worse bug to chase later.
        using var source = new MemoryStream(bytes).AsRandomAccessStream();

        PdfDocument document;
        try
        {
            document = await PdfDocument.LoadFromStreamAsync(source).AsTask(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (document.PageCount == 0)
            return null;

        // Rendering and decoding happen inside one scope: BitmapCacheOption.OnLoad reads the
        // frame eagerly, but only once Create() is actually called, so the WinRT stream has to
        // still be open at that point -- disposing it any earlier (as a first version of this did)
        // fails with "Cannot access a closed Stream" once the frame is touched afterwards.
        using var page = document.GetPage(0);
        using var rendered = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(rendered).AsTask(ct).ConfigureAwait(true);
        rendered.Seek(0);

        // Decoded through WPF's own imaging stack rather than WinRT's, so a PDF page is just
        // another bitmap by the time it reaches PreviewViewModel -- the same path an image file
        // already takes, not a second one.
        var decoder = BitmapDecoder.Create(
            rendered.AsStream(), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();

        return new Rendered(frame, document.PageCount);
    }
}
