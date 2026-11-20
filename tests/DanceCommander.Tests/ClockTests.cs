using System.Globalization;
using DanceCommander.Core.Jobs;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class ClockTests
{
    private static readonly CultureInfo Finnish = CultureInfo.GetCultureInfo("fi-FI");

    [Fact]
    public void TodayIsTheTimeAlone()
    {
        var now = DateTimeOffset.Now;
        var at = now.AddMinutes(-1);

        Clock.Time(at, now, Finnish).ShouldBe(at.ToLocalTime().ToString("t", Finnish));
    }

    [Fact]
    public void AnotherDayHasItsDateToo()
    {
        var now = DateTimeOffset.Now;
        var at = now.AddDays(-1);

        Clock.Time(at, now, Finnish).ShouldBe(at.ToLocalTime().ToString("d.M.yyyy", Finnish) + " " + at.ToLocalTime().ToString("t", Finnish));
    }
}
