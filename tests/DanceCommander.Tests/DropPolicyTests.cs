using DanceCommander.Core.Fs;
using DanceCommander.Core.Transfer;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// What a drag means, decided without a mouse. The cursor has to promise the right thing while
/// the button is still down, so these rules run during DragOver and again on Drop.
/// </summary>
public sealed class DropPolicyTests
{
    private static FsUri L(string path) => FsUri.Local(path);

    private static DropAction Decide(
        FsUri source, FsUri target, DragModifiers modifiers = DragModifiers.None) =>
        DropPolicy.Decide([source], target, modifiers);

    // ---- the Explorer defaults, because muscle memory follows them ----------------------

    [Fact]
    public void WithinOneDriveADragMoves() =>
        Decide(L(@"C:\work\notes.txt"), L(@"C:\archive")).ShouldBe(DropAction.Move);

    [Fact]
    public void BetweenDrivesADragCopies() =>
        Decide(L(@"C:\work\notes.txt"), L(@"D:\archive")).ShouldBe(DropAction.Copy);

    [Fact]
    public void ControlForcesACopyWithinOneDrive() =>
        Decide(L(@"C:\work\notes.txt"), L(@"C:\archive"), DragModifiers.Control).ShouldBe(DropAction.Copy);

    [Fact]
    public void ShiftForcesAMoveBetweenDrives() =>
        Decide(L(@"C:\work\notes.txt"), L(@"D:\archive"), DragModifiers.Shift).ShouldBe(DropAction.Move);

    // ---- the refusals, which matter more than the defaults -------------------------------

    [Fact]
    public void AFolderCannotBeDroppedIntoItself() =>
        Decide(L(@"C:\work"), L(@"C:\work")).ShouldBe(DropAction.None);

    [Fact]
    public void AFolderCannotBeDroppedIntoItsOwnSubfolder() =>
        // Easy to do by accident, and a loop if allowed.
        Decide(L(@"C:\work"), L(@"C:\work\nested\deep")).ShouldBe(DropAction.None);

    [Fact]
    public void ASubfolderCanStillBeDroppedIntoSomewhereElseEntirely() =>
        Decide(L(@"C:\work\nested"), L(@"C:\archive")).ShouldBe(DropAction.Move);

    [Fact]
    public void DroppingWhereTheFilesAlreadyAreDoesNothing() =>
        // A slipped drag should not quietly produce "notes (2).txt".
        Decide(L(@"C:\work\notes.txt"), L(@"C:\work")).ShouldBe(DropAction.None);

    [Fact]
    public void ButAnExplicitCopyOntoTheirOwnFolderIsAllowed() =>
        // Ctrl says the user meant it; duplicating in place is a real thing people do.
        Decide(L(@"C:\work\notes.txt"), L(@"C:\work"), DragModifiers.Control).ShouldBe(DropAction.Copy);

    // ---- dragging the location icon (§6.18) ----------------------------------------------------

    [Fact]
    public void DraggingWhereYouAreCopiesEvenOnTheSameDrive() =>
        // A plain drag of the folder you are standing in used to move it out from under the pane.
        DropPolicy.Decide([L(@"C:\work\app")], L(@"C:\archive"), DragModifiers.None, preferCopy: true)
            .ShouldBe(DropAction.Copy);

    [Fact]
    public void ShiftStillMovesWhereYouAre() =>
        DropPolicy.Decide([L(@"C:\work\app")], L(@"C:\archive"), DragModifiers.Shift, preferCopy: true)
            .ShouldBe(DropAction.Move);

    [Fact]
    public void WhereYouAreIsStillRefusedIntoItself() =>
        DropPolicy.Decide([L(@"C:\work\app")], L(@"C:\work\app\src"), DragModifiers.None, preferCopy: true)
            .ShouldBe(DropAction.None);

    [Fact]
    public void NothingSelectedIsNotADrop() =>
        DropPolicy.Decide([], L(@"C:\archive"), DragModifiers.None).ShouldBe(DropAction.None);

    [Fact]
    public void AReadOnlyTargetRefusesEverything() =>
        DropPolicy.Decide([L(@"C:\work\a.txt")], L(@"C:\ro"), DragModifiers.None, targetIsWritable: false)
            .ShouldBe(DropAction.None);

    // ---- leaving the machine --------------------------------------------------------------

    [Fact]
    public void AnythingLeavingTheMachineCopies() =>
        // A move would end in deleting the original across a link that may drop halfway.
        Decide(L(@"C:\work\notes.txt"), FsUri.Parse("sftp://claude@host/var/log")).ShouldBe(DropAction.Copy);

    [Fact]
    public void ComingBackFromAServerAlsoCopies() =>
        Decide(FsUri.Parse("sftp://claude@host/var/log/app.log"), L(@"C:\work")).ShouldBe(DropAction.Copy);

    [Fact]
    public void WithinOneServerAccountADragStillMoves() =>
        Decide(
            FsUri.Parse("sftp://claude@host/var/log/app.log"),
            FsUri.Parse("sftp://claude@host/var/archive")).ShouldBe(DropAction.Move);

    [Fact]
    public void BetweenTwoAccountsOnTheSameHostItCopies() =>
        // Different credentials, different reach: it is not a rename on the far side.
        Decide(
            FsUri.Parse("sftp://alice@host/home/alice/a.txt"),
            FsUri.Parse("sftp://bob@host/home/bob")).ShouldBe(DropAction.Copy);

    [Fact]
    public void DraggingOutOfAnArchiveCopies()
    {
        var inside = FsUri.ArchiveEntry(L(@"C:\dl\bundle.zip"), "docs/readme.md");

        Decide(inside, L(@"C:\work")).ShouldBe(DropAction.Copy);
    }

    [Fact]
    public void MovingInsideOneArchiveIsStillACopyBecauseNothingCanBeMovedInPlace()
    {
        var container = L(@"C:\dl\bundle.zip");

        Decide(FsUri.ArchiveEntry(container, "a/x.txt"), FsUri.ArchiveEntry(container, "b"))
            .ShouldBe(DropAction.Copy);
    }

    // ---- several sources at once -----------------------------------------------------------

    [Fact]
    public void ASelectionSpanningTwoDrivesCopiesRatherThanMovingSome()
    {
        // Half a move and half a copy would be the worst of both, so the safe action wins.
        var sources = new[] { L(@"C:\work\a.txt"), L(@"D:\other\b.txt") };

        DropPolicy.Decide(sources, L(@"C:\archive"), DragModifiers.None).ShouldBe(DropAction.Copy);
    }

    [Fact]
    public void OneImpossibleSourceRefusesTheWholeDrop()
    {
        // Dropping a folder onto itself does not become acceptable by selecting a file too.
        var sources = new[] { L(@"C:\work\a.txt"), L(@"C:\archive") };

        DropPolicy.Decide(sources, L(@"C:\archive"), DragModifiers.None).ShouldBe(DropAction.None);
    }

    // ---- volumes ----------------------------------------------------------------------------

    [Fact]
    public void TwoSharesOnOneServerAreNotOneVolume() =>
        DropPolicy.SameVolume(L(@"\\nas\media\a.mkv"), L(@"\\nas\backups")).ShouldBeFalse();

    [Fact]
    public void OneShareIsOneVolume() =>
        DropPolicy.SameVolume(L(@"\\nas\media\a.mkv"), L(@"\\nas\media\archive")).ShouldBeTrue();
}
