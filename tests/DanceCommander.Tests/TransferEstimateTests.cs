using DanceCommander.Core.Transfer;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The time a transfer has left: bytes left ÷ speed + files left × cost per file, the two
/// measured apart so that nothing is counted twice.
/// </summary>
public sealed class TransferEstimateTests
{
    private const long MB = 1024 * 1024;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>One file: 0.2 s before its first piece, then a 1 MB piece every 0.1 s.</summary>
    private static double CopyFile(TransferEstimate estimate, double start, int pieces)
    {
        estimate.FileStarted(S(start));
        var now = start + 0.2;
        estimate.Moved(MB, S(now));
        for (var i = 1; i < pieces; i++)
        {
            now += 0.1;
            estimate.Moved(MB, S(now));
        }

        return now;
    }

    [Fact]
    public void LargePartsAreTheirBytesPlusTheirSmallCost()
    {
        // 100 MB parts at 10 MB/s, each taking 0.2 s to its first 1 MB piece and 9.9 s for the
        // other 99: five more are 5 × 10.1 = 50.5 s. Not the 100 s that counting each file's whole
        // time on top of its bytes would give, nor 51, which counts each first piece twice.
        var estimate = new TransferEstimate();
        var end = CopyFile(estimate, 0, 100);
        CopyFile(estimate, end, 100);

        estimate.Speed!.Value.ShouldBe(10.0 * MB, tolerance: 0.01 * MB);
        estimate.Left(500 * MB, 5)!.Value.TotalSeconds.ShouldBe(50.5, tolerance: 0.05);
    }

    [Fact]
    public void SmallFilesAreTheirCostPerFile()
    {
        // Each in a single piece: no moving time to measure a speed by, and the files are the time.
        var estimate = new TransferEstimate();
        for (var i = 0; i < 10; i++)
        {
            estimate.FileStarted(S(i * 0.05));
            estimate.Moved(10 * 1024, S(i * 0.05 + 0.04));
        }

        estimate.Finished(S(0.5));

        estimate.Speed.ShouldBeNull();
        estimate.Left(1 * MB, 100)!.Value.TotalSeconds.ShouldBe(5, tolerance: 0.01);
    }

    [Fact]
    public void NothingToGoOnIsNoEstimate() =>
        new TransferEstimate().Left(100 * MB, 3).ShouldBeNull();

    [Fact]
    public void AQuestionsWaitIsNotTheFilesCost()
    {
        var estimate = new TransferEstimate();
        var end = CopyFile(estimate, 0, 10);

        // The next file is asked about for a minute, then copied.
        estimate.FileStarted(S(end));
        estimate.Interrupted();
        estimate.Resumed(S(end + 60));
        estimate.Moved(MB, S(end + 60.2));
        estimate.Finished(S(end + 60.2));

        // Only the first file's 0.2 s counts, and the minute is not moving time either.
        estimate.Left(0, 10)!.Value.TotalSeconds.ShouldBe(2, tolerance: 0.05);
        estimate.Speed!.Value.ShouldBe(10.0 * MB, tolerance: 0.01 * MB);
    }

    [Fact]
    public void ACrawlDoesNotOverflow()
    {
        // A few bytes after a long stall: a speed under a byte a second, and a terabyte left.
        var estimate = new TransferEstimate();
        estimate.FileStarted(S(0));
        estimate.Moved(1, S(1));
        estimate.Moved(100, S(600));

        Should.NotThrow(() => estimate.Left(1024L * 1024 * MB, 1));
    }

    [Fact]
    public void OnlyTheFilesAreAStretchWhereStillnessMeansSomething()
    {
        var estimate = new TransferEstimate();
        estimate.InFiles.ShouldBeFalse(); // listing the sources

        estimate.FileStarted(S(0));
        estimate.InFiles.ShouldBeTrue();

        estimate.Finished(S(1));
        estimate.InFiles.ShouldBeFalse(); // tidying up after
    }

    [Fact]
    public void ProgressIsTheLastThingThatMoved()
    {
        var estimate = new TransferEstimate();
        var end = CopyFile(estimate, 0, 3);

        estimate.SinceProgress(S(end + 12)).ShouldBe(S(12));
    }

    [Theory]
    [InlineData(3, "5 s")]
    [InlineData(12, "10 s")]
    [InlineData(23, "20 s")]
    [InlineData(54, "50 s")]
    [InlineData(90, "2 min")]
    [InlineData(61 * 60, "1 h")]
    [InlineData(71 * 60, "1 h 10 min")]
    public void ATimeIsSaidRoughly(double seconds, string said) =>
        TransferJob.Rough(TimeSpan.FromSeconds(seconds)).ShouldBe(said);
}
