using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Operations;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>What the window keeps across a restart, and what it does with a place that has gone.</summary>
public sealed class SessionTests
{
    private static FsUri Place(int n) => FsUri.Local($@"C:\work\p{n}");

    [Fact]
    public void ATabKeepsItsHistoryAroundWhereItStands()
    {
        var history = Enumerable.Range(0, 70).Select(Place).ToList();

        var kept = TabSession.Of(history, position: 40, cursor: "readme.md");

        // Twenty back and twenty forward: enough to retrace a day, not a year of it.
        kept.History.Count.ShouldBe(TabSession.Reach * 2 + 1);
        kept.Position.ShouldBe(TabSession.Reach);
        kept.Cursor.ShouldBe("readme.md");

        var (back, position) = kept.Read().ShouldNotBeNull();
        back[position].ShouldBe(Place(40));
        back[0].ShouldBe(Place(20));
        back[^1].ShouldBe(Place(60));
    }

    [Fact]
    public void AnEntryThatNoLongerReadsIsDroppedAndThePositionFollows()
    {
        var saved = new TabSession
        {
            History = [Place(1).ToString(), "   ", Place(2).ToString(), Place(3).ToString()],
            Position = 2,
        };

        var (history, position) = saved.Read().ShouldNotBeNull();

        history.ShouldBe([Place(1), Place(2), Place(3)]);
        history[position].ShouldBe(Place(2));
    }

    [Fact]
    public void ATabWithNothingReadableIsNotPutBack() =>
        new TabSession { History = [] }.Read().ShouldBeNull();

    [Fact]
    public void AGoneFolderOpensItsNearestSurvivingParent()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\", @"C:\work" };

        SessionPlaces.NearestExisting(FsUri.Local(@"C:\work\old-branch\src"), existing.Contains)
            .ShouldBe(FsUri.Local(@"C:\work"));
        SessionPlaces.NearestExisting(FsUri.Local(@"C:\work"), existing.Contains)
            .ShouldBe(FsUri.Local(@"C:\work"));
    }

    [Fact]
    public void AShareNeverLeadsToTheCurrentDrive()
    {
        // "\" exists wherever the process runs, and is not where \\server\share was.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\", @"C:\" };
        var gone = FsUri.Local(@"\\server\share\fonts");

        SessionPlaces.NearestExisting(gone, existing.Contains).ShouldBe(gone);
    }

    [Fact]
    public void AShareStillOpensItsNearestSurvivingFolder()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\\server\share" };

        SessionPlaces.NearestExisting(FsUri.Local(@"\\server\share\a\b"), existing.Contains)
            .ShouldBe(FsUri.Local(@"\\server\share"));
    }

    [Fact]
    public void ADriveRootIsStillANearestFolder()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\" };

        SessionPlaces.NearestExisting(FsUri.Local(@"C:\nope\fonts"), existing.Contains)
            .ShouldBe(FsUri.Local(@"C:\"));
    }

    [Fact]
    public void OnlyLocalFoldersAreCheckedForBeingGone()
    {
        // A server or a search is asked when it is shown, not guessed at from here.
        var remote = FsUri.Remote("sftp", "build01", "/var/log");
        SessionPlaces.NearestExisting(remote, _ => false).ShouldBe(remote);
    }

    [Fact]
    public void ARestoredHistoryGoesBackAndForwardFromWhereItStood()
    {
        var history = new NavigationHistory();
        history.Restore([Place(1), Place(2), Place(3)], position: 1);

        history.Current.ShouldBe(Place(2));
        history.Back().ShouldBe(Place(1));
        history.Forward().ShouldBe(Place(2));
        history.Forward().ShouldBe(Place(3));

        // Visiting where it already stands is not a new step.
        history.Visit(Place(3));
        history.Entries.Count.ShouldBe(3);
    }
}

public sealed class ArrivalGlowTests
{
    [Fact]
    public void ItComesQuicklyHoldsAMomentAndGoesSlowly()
    {
        ArrivalGlow.At(TimeSpan.Zero).ShouldBe(0);
        ArrivalGlow.At(ArrivalGlow.Rise / 2).ShouldBeGreaterThan(0.5); // easing out: most of the way at once
        ArrivalGlow.At(ArrivalGlow.Rise).ShouldBe(1);
        ArrivalGlow.At(ArrivalGlow.Hold - TimeSpan.FromMilliseconds(1)).ShouldBe(1);
        ArrivalGlow.At(ArrivalGlow.Total).ShouldBe(0);
    }

    [Fact]
    public void ItNeverBrightensAgainOnceFading()
    {
        var previous = 1.0;
        for (var t = ArrivalGlow.Hold; t < ArrivalGlow.Total; t += TimeSpan.FromMilliseconds(16))
        {
            var level = ArrivalGlow.At(t);
            level.ShouldBeLessThanOrEqualTo(previous);
            previous = level;
        }
    }
}
