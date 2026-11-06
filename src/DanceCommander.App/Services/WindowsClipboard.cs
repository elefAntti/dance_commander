using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DanceCommander.Core.Clipboard;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;

namespace DanceCommander.App.Services;

/// <summary>
/// The Windows clipboard, in the two directions a file manager needs it (§6.14).
/// </summary>
/// <remarks>
/// Every call here can fail for reasons that have nothing to do with this application: the
/// clipboard is a single system-wide resource, another process can hold it open, and the program
/// that put data on it may have exited without rendering what it promised. None of that is worth
/// a crash over a Ctrl+V, so every entry point returns whether it got the clipboard instead, and
/// a format that fails to render degrades to "not there".
/// </remarks>
public sealed class WindowsClipboard : IClipboardService
{
    /// <summary>
    /// Our own format, carrying URIs so a copy keeps full fidelity inside the application. The
    /// same format drag and drop uses, so the clipboard and a drag are interchangeable.
    /// </summary>
    private const string UriFormat = "DanceCommander.Uris";

    /// <summary>
    /// How Explorer marks a cut, and the only way another application can tell one from a copy:
    /// the file list itself is identical either way. A DWORD of DROPEFFECT_MOVE or _COPY.
    /// </summary>
    private const string DropEffectFormat = "Preferred DropEffect";

    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    /// <summary>
    /// The clipboard is a single system-wide resource and anything on the machine can hold it for
    /// the moment it takes a clipboard viewer to repaint, which surfaces as CLIPBRD_E_CANT_OPEN.
    /// It happens often enough to have been hit while testing this class, so a failed call is tried
    /// again until this much time has gone.
    /// </summary>
    /// <remarks>
    /// A time and not a count, because one call can itself take a second: WPF retries inside it
    /// before throwing. Ten attempts of that froze the window for ten seconds or more while an
    /// unstable Explorer held the clipboard (2026-10-20), on the UI thread, over one keystroke.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

    private const int RetryDelayMs = 50;

    /// <summary>What a copy says when it gave up, the same from every place that copies.</summary>
    public static readonly Notice CouldNotCopy = new(
        "Could not copy to the clipboard.", NoticeSeverity.Warning,
        "Another program is holding it. Try again in a moment.");

    /// <summary>
    /// Text for pasting elsewhere: a path, a name, a few lines of a preview, an error.
    /// </summary>
    /// <returns>False when another program kept the clipboard; say <see cref="CouldNotCopy"/>.</returns>
    public static bool PutText(string text) => Retry(() => Clipboard.SetText(text));

    public bool PutFiles(IReadOnlyList<FsUri> uris, ClipboardMode mode)
    {
        if (uris.Count == 0)
            return true;

        var data = new DataObject();
        data.SetData(UriFormat, uris.Select(uri => uri.ToString()).ToArray());

        // Everything outside this application speaks paths and nothing else. A remote or
        // in-archive entry has no path, so it goes on the clipboard for our own use only --
        // pasting it into Explorer would promise a file that is not there.
        var paths = new StringCollection();
        foreach (var uri in uris.Where(uri => uri.IsLocal))
            paths.Add(uri.ToLocalPath());

        if (paths.Count > 0)
            data.SetFileDropList(paths);

        // Whatever the items: written only beside local paths, a cut of server files read back
        // as a copy, and Ctrl+V left the originals where they were (driven, 2026-10-28).
        data.SetData(
            DropEffectFormat,
            new MemoryStream(BitConverter.GetBytes(mode == ClipboardMode.Move ? DropEffectMove : DropEffectCopy)));

        // Copy: true leaves the data on the clipboard after this process exits, which is what a
        // user who copied a file and then closed the window expects to still be able to paste.
        return Retry(() => Clipboard.SetDataObject(data, true));
    }

    public bool ForgetCut(IReadOnlyList<FsUri> uris) =>
        Retry(() =>
        {
            if (Clipboard.GetDataObject() is { } data
                && ReadFiles(data) is { Mode: ClipboardMode.Move } cut
                && cut.Uris.SequenceEqual(uris))
            {
                Clipboard.Clear();
            }
        });

    public bool TryRead(out ClipboardContent? content) => Retry(Read, out content);

    private static ClipboardContent? Read()
    {
        if (Clipboard.GetDataObject() is not { } data)
            return null;

        // Files first, always -- see the remark on ClipboardContent. This is also why the bitmap
        // below is only reached when nothing on the clipboard claims to be a file.
        if (ReadFiles(data) is { } files)
            return files;

        if (ReadImage() is { } image)
            return image;

        return ReadText();
    }

    /// <summary>
    /// Runs a clipboard call, treating a failure as "try again shortly" rather than as "there is
    /// nothing there". The distinction matters on the way in: a paste that gives up on the first
    /// contended moment tells the user their clipboard is empty when it is not, and they have no
    /// way to tell that apart from a clipboard that really is.
    /// </summary>
    /// <returns>
    /// False once <see cref="Patience"/> has run out: genuinely stuck, or held by something that
    /// is not letting go. The caller says so in the pane; an exception here took the window down.
    /// </returns>
    private static bool Retry<T>(Func<T> operation, out T? result)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                result = operation();
                return true;
            }
            catch (Exception) when (clock.Elapsed < Patience)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning("Gave up on the clipboard after {Elapsed} ms: {Error}", clock.ElapsedMilliseconds, ex.Message);
                result = default;
                return false;
            }
        }
    }

    private static bool Retry(Action operation) =>
        Retry<object?>(() =>
        {
            operation();
            return null;
        }, out _);

    // The guards below are a different thing from Retry, and the split is deliberate. Retry covers
    // opening the clipboard at all, which is the contended part and is worth waiting for. These
    // cover rendering one particular format, where the right answer is to fall through and try the
    // next one -- an application that offers a format and then fails to produce it should not take
    // the formats that did work down with it.

    private static ClipboardContent.Files? ReadFiles(IDataObject data)
    {
        var uris = OwnUris(data) ?? DroppedPaths(data);
        if (uris is null || uris.Count == 0)
            return null;

        return new ClipboardContent.Files(uris, ModeOf(data));
    }

    /// <summary>Our own format, which survives archives and servers as a path cannot.</summary>
    private static IReadOnlyList<FsUri>? OwnUris(IDataObject data)
    {
        try
        {
            if (data.GetData(UriFormat) is not string[] spelled)
                return null;

            return spelled
                .Select(text => FsUri.TryParse(text, out var uri, out _) ? uri : null)
                .OfType<FsUri>()
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<FsUri>? DroppedPaths(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] paths)
                return null;

            return paths.Select(FsUri.LocalExact).ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ClipboardMode ModeOf(IDataObject data)
    {
        try
        {
            // Absent means copy. Explorer omits it for a plain copy often enough that treating a
            // missing marker as anything else would turn ordinary copies into moves, and a move
            // that was meant to be a copy deletes the original.
            if (data.GetData(DropEffectFormat) is not MemoryStream stream)
                return ClipboardMode.Copy;

            var bytes = stream.ToArray();
            if (bytes.Length < sizeof(int))
                return ClipboardMode.Copy;

            return (BitConverter.ToInt32(bytes, 0) & DropEffectMove) != 0
                ? ClipboardMode.Move
                : ClipboardMode.Copy;
        }
        catch (Exception)
        {
            return ClipboardMode.Copy;
        }
    }

    /// <summary>
    /// A bitmap, encoded to PNG because that is the format that survives a screenshot: lossless,
    /// and the only one of the obvious candidates that keeps an alpha channel, which a snip of a
    /// rounded window corner has.
    /// </summary>
    private static ClipboardContent.Image? ReadImage()
    {
        // Guarded as the file formats are: a bitmap its program fails to produce falls through to
        // the text, rather than failing the paste as a held clipboard. Text is not guarded, so a
        // clipboard held in the meantime still fails there and Retry tries the whole read again.
        try
        {
            if (!Clipboard.ContainsImage())
                return null;

            if (Clipboard.GetImage() is not { } source)
                return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return new ClipboardContent.Image(buffer.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ClipboardContent.Text? ReadText()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        return string.IsNullOrEmpty(text) ? null : new ClipboardContent.Text(text);
    }
}
