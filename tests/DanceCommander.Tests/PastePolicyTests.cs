using DanceCommander.Core.Clipboard;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Transfer;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// What a paste means. The interesting cases are all the ones where it deliberately disagrees with
/// <see cref="DropPolicyTests"/>: a drag has to guess, a paste has already been told.
/// </summary>
public sealed class PastePolicyTests
{
    private static FsUri L(string path) => FsUri.Local(path);

    private static DropAction Decide(
        FsUri source, FsUri target, ClipboardMode mode = ClipboardMode.Copy, bool writable = true) =>
        PastePolicy.Decide([source], target, mode, writable);

    // ---- the clipboard's verb is the answer, not a starting point ------------------------

    [Fact]
    public void ACopiedFileIsCopied() =>
        Decide(L(@"C:\work\notes.txt"), L(@"C:\archive")).ShouldBe(DropAction.Copy);

    [Fact]
    public void ACutFileIsMoved() =>
        Decide(L(@"C:\work\notes.txt"), L(@"C:\archive"), ClipboardMode.Move).ShouldBe(DropAction.Move);

    [Fact]
    public void CopyingAcrossDrivesIsStillACopyAndNotADecisionAboutVolumes() =>
        // Where a drag would have had to work this out from the drive letters, a paste was told.
        Decide(L(@"C:\work\notes.txt"), L(@"D:\archive")).ShouldBe(DropAction.Copy);

    [Fact]
    public void CuttingWithinOneDriveIsAMoveAndCuttingAcrossDrivesIsToo()
    {
        Decide(L(@"C:\work\notes.txt"), L(@"C:\archive"), ClipboardMode.Move).ShouldBe(DropAction.Move);
        Decide(L(@"C:\work\notes.txt"), L(@"D:\archive"), ClipboardMode.Move).ShouldBe(DropAction.Move);
    }

    // ---- where it parts company with a drag ----------------------------------------------

    [Fact]
    public void PastingACopyIntoTheFolderItCameFromIsAllowed() =>
        // The ordinary way to duplicate a file, and the one case a drag refuses outright:
        // Explorer has answered this with "notes (2).txt" for thirty years.
        Decide(L(@"C:\work\notes.txt"), L(@"C:\work")).ShouldBe(DropAction.Copy);

    [Fact]
    public void PastingACutIntoTheFolderItCameFromDoesNothing() =>
        // Nothing to do: it is already there, and "moving" it would be an elaborate no-op that
        // still risked a collision prompt.
        Decide(L(@"C:\work\notes.txt"), L(@"C:\work"), ClipboardMode.Move).ShouldBe(DropAction.None);

    [Fact]
    public void AFolderCannotBeCutIntoItself() =>
        Decide(L(@"C:\work"), L(@"C:\work"), ClipboardMode.Move).ShouldBe(DropAction.None);

    [Fact]
    public void AFolderCannotBeCutIntoItsOwnSubfolder() =>
        Decide(L(@"C:\work"), L(@"C:\work\nested\deep"), ClipboardMode.Move).ShouldBe(DropAction.None);

    [Fact]
    public void OneImpossibleFileRefusesTheWholePaste() =>
        // Half a paste is worse than none: the user would have to work out which of the five
        // landed. The whole thing is declined and said so.
        PastePolicy.Decide(
            [L(@"C:\work\notes.txt"), L(@"C:\work")],
            L(@"C:\work\nested"),
            ClipboardMode.Move).ShouldBe(DropAction.None);

    // ---- what cannot be written to ---------------------------------------------------------

    [Fact]
    public void NothingPastesIntoSomewhereUnwritable() =>
        Decide(L(@"C:\work\notes.txt"), L(@"C:\archive"), writable: false).ShouldBe(DropAction.None);

    [Fact]
    public void AnEmptyClipboardPastesNothing() =>
        PastePolicy.Decide([], L(@"C:\archive"), ClipboardMode.Copy).ShouldBe(DropAction.None);

    // ---- remote and in-archive sources are ordinary here ------------------------------------

    [Fact]
    public void ARemoteFileCanBePastedLocally() =>
        // The clipboard carries URIs for our own use, so a copy from a server is a transfer like
        // any other rather than something the clipboard has to refuse.
        PastePolicy.Decide(
            [FsUri.Parse("sftp://host/srv/notes.txt")],
            L(@"C:\work"),
            ClipboardMode.Copy).ShouldBe(DropAction.Copy);

    [Fact]
    public void AFileInsideAnArchiveCanBePastedOut() =>
        PastePolicy.Decide(
            [FsUri.Parse(@"C:\dl\release.zip!/bin/tool.exe")],
            L(@"C:\work"),
            ClipboardMode.Copy).ShouldBe(DropAction.Copy);
}
