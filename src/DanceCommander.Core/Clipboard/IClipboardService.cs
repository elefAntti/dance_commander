using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Clipboard;

/// <summary>Which of the two things a clipboard full of files means.</summary>
public enum ClipboardMode
{
    Copy,
    Move,
}

/// <summary>
/// What a paste found on the clipboard, in the order a paste should prefer it.
/// </summary>
/// <remarks>
/// The order matters and is not arbitrary. Copying an image file in Explorer puts <em>both</em> a
/// file list and a bitmap on the clipboard, and a paste that reached for the bitmap first would
/// quietly re-encode a perfectly good JPEG as a new PNG with a made-up name, losing the original's
/// name, its metadata and its bytes. Files therefore always win; the bitmap is what is left when
/// there is no file behind it, which is the screenshot case.
/// </remarks>
public abstract record ClipboardContent
{
    private ClipboardContent()
    {
    }

    /// <param name="Mode">Copy or cut, as the source application marked it.</param>
    public sealed record Files(IReadOnlyList<FsUri> Uris, ClipboardMode Mode) : ClipboardContent;

    /// <param name="Png">
    /// Already encoded, because deciding on a format is the reading end's job: the clipboard hands
    /// out a device-dependent bitmap and something has to choose what a file of it looks like.
    /// </param>
    public sealed record Image(byte[] Png) : ClipboardContent;

    public sealed record Text(string Value) : ClipboardContent;
}

/// <summary>
/// The system clipboard, as much of it as a file manager cares about (§6.14).
/// </summary>
/// <remarks>
/// An interface in Core with the implementation in the App layer, for the same reason
/// <see cref="Operations.IRecycleBin"/> is: the clipboard is a Win32 surface with an apartment,
/// failure modes involving other processes, and an imaging stack behind it, and none of that
/// belongs in the half of the application that copies bytes between providers.
/// <para>
/// Each call says whether it got the clipboard at all. Another program can hold it open, and a
/// copy that quietly did nothing left the previous contents to be pasted in its place.
/// </para>
/// </remarks>
public interface IClipboardService
{
    /// <summary>
    /// Puts a selection on the clipboard for this application and for every other one.
    /// </summary>
    /// <returns>False when another program kept the clipboard; nothing was put on it.</returns>
    bool PutFiles(IReadOnlyList<FsUri> uris, ClipboardMode mode);

    /// <summary>
    /// What is on the clipboard now: <paramref name="content"/> is null when it holds nothing a
    /// file can be made of.
    /// </summary>
    /// <returns>
    /// False when the clipboard could not be opened, which is not the same as empty: whatever is
    /// on it is still there, and saying "nothing to paste" would be wrong.
    /// </returns>
    bool TryRead(out ClipboardContent? content);

    /// <summary>
    /// Takes a cut off the clipboard once it has been pasted, as Explorer does: left there, a
    /// second Ctrl+V moved the files again, from where they had just been put. Only that same
    /// cut is cleared; anything copied since stays.
    /// </summary>
    /// <returns>False when another program kept the clipboard, so the cut may still be on it.</returns>
    bool ForgetCut(IReadOnlyList<FsUri> uris);
}
