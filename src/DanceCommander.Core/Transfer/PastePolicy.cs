using DanceCommander.Core.Clipboard;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Transfer;

/// <summary>
/// Decides what pasting a clipboard full of files would do.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="DropPolicy"/>, though the two answer a similar-looking question.
/// A drag is a gesture whose meaning has to be guessed from where it started and where it is
/// hovering, and guessing wrong with the button still down is cheap to correct. A paste was
/// already decided, twice: once when the user chose copy or cut, and once when they chose where
/// to stand. So this does not re-derive the verb from the volumes involved -- it takes the one the
/// clipboard carries and only refuses what cannot be done at all.
///
/// The visible difference is pasting into the folder you copied from. A drag there is a slip and
/// is refused; a paste there is the ordinary way to duplicate a file, and Explorer has made
/// "name (2)" the expected answer for thirty years.
/// </remarks>
public static class PastePolicy
{
    public static DropAction Decide(
        IReadOnlyList<FsUri> sources, FsUri target, ClipboardMode mode, bool targetIsWritable = true)
    {
        if (sources.Count == 0 || !targetIsWritable)
            return DropAction.None;

        if (mode == ClipboardMode.Copy)
            return DropAction.Copy;

        foreach (var source in sources)
        {
            // A move onto where it already is has nothing to do, and a move into your own subtree
            // is a loop. Both are refused outright rather than partly done: a paste of five files
            // where one of them is the folder you are standing in should not half-happen.
            if (source == target || source.Parent == target || DropPolicy.Contains(source, target))
                return DropAction.None;
        }

        return DropAction.Move;
    }
}
