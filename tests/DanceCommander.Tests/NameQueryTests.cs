using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Matching;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// What a typed query means. One rule shared by the search location and the quick filter, so that
/// what you learn typing in a pane is what you know when you search from one.
/// </summary>
public sealed class NameQueryTests
{
    private static bool Matches(string query, string name) =>
        NameQuery.Compile(query).Matches(new FsEntry(FsUri.Local(@"C:\x\" + name), name, false, 0, null));

    [Fact]
    public void ABareWordMatchesAnywhereInTheName()
    {
        // What people actually type, and what they expect it to do.
        Matches("view", "FileRowViewModel.cs").ShouldBeTrue();
        Matches("row", "FileRowViewModel.cs").ShouldBeTrue();
        Matches("zzz", "FileRowViewModel.cs").ShouldBeFalse();
    }

    [Fact]
    public void ItIgnoresCase() =>
        Matches("VIEWMODEL", "FileRowViewModel.cs").ShouldBeTrue();

    [Fact]
    public void AWildcardMakesItAGlob()
    {
        // Anchored, because that is what a glob means everywhere else on a computer.
        Matches("*.cs", "App.cs").ShouldBeTrue();
        Matches("*.cs", "App.csproj").ShouldBeFalse();
        Matches("App.*", "App.cs").ShouldBeTrue();
    }

    [Fact]
    public void AQuestionMarkIsOneCharacter()
    {
        Matches("App.??", "App.cs").ShouldBeTrue();
        Matches("App.??", "App.json").ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyQueryMatchesEverything()
    {
        Matches("", "anything.txt").ShouldBeTrue();
        Matches("   ", "anything.txt").ShouldBeTrue();
    }

    [Fact]
    public void TheTwoReadingsAreDistinguishedByWildcards()
    {
        NameQuery.IsGlob("report").ShouldBeFalse();
        NameQuery.IsGlob("*.md").ShouldBeTrue();
        NameQuery.IsGlob("log?.txt").ShouldBeTrue();
    }

    [Fact]
    public void ABareWordBecomesASurroundedGlob() =>
        // The mechanism behind "contains", stated so the two readings stay one implementation.
        NameQuery.ToGlob("view").ShouldBe("*view*");

    [Fact]
    public void AGlobIsLeftAlone() =>
        NameQuery.ToGlob("*.cs").ShouldBe("*.cs");

    [Fact]
    public void SpacesInAQueryAreKept() =>
        // A filter typed past the first word is still matching one name.
        Matches("my file", "my file.txt").ShouldBeTrue();

    [Fact]
    public void SeveralWordsMustAllMatchInAnyOrder()
    {
        Matches("mon d", "disk monitor").ShouldBeTrue();
        Matches("mon d", "CPU monitor").ShouldBeFalse();
        Matches("mon d", "pokemon").ShouldBeFalse();
        Matches("monitor disk", "disk monitor").ShouldBeTrue();
    }

    [Fact]
    public void EachWordIsReadByTheSameRule()
    {
        // A wildcard makes that word a glob; the others still only have to be in the name.
        Matches("view *.cs", "FileRowViewModel.cs").ShouldBeTrue();
        Matches("view *.cs", "FileRowViewModel.xaml").ShouldBeFalse();
        Matches("*.cs row", "App.cs").ShouldBeFalse();
    }

    private static string[] Ranked(string query, params string[] names)
    {
        var matcher = NameQuery.Compile(query);
        // A stable sort, so equal ranks keep the order given, as the listing's own order would.
        return names.OrderBy(matcher.Rank).ToArray();
    }

    [Fact]
    public void ANameThatStartsWithTheQueryLeads() =>
        // What type-ahead would have found.
        Ranked("rea", "already.txt", "README.md").ShouldBe(["README.md", "already.txt"]);

    [Fact]
    public void AWordThatStartsAWordOfTheNameBeatsOneInsideAWord()
    {
        Ranked("mon", "pokemon", "CPU monitor", "disk monitor")
            .ShouldBe(["CPU monitor", "disk monitor", "pokemon"]);
        // Camel case and digits are word boundaries too.
        Ranked("model", "remodelled.txt", "FileRowViewModel.cs")
            .ShouldBe(["FileRowViewModel.cs", "remodelled.txt"]);
    }

    [Fact]
    public void WordsInTheOrderTypedBeatTheSameWordsOutOfOrder() =>
        Ranked("mon d", "disk monitor", "CPU monitor dump")
            .ShouldBe(["CPU monitor dump", "disk monitor"]);

    [Fact]
    public void TheFirstWordStartingTheNameOutweighsTheRest() =>
        Ranked("mon d", "disk monitor", "monitored")
            .ShouldBe(["monitored", "disk monitor"]);

    [Theory]
    [InlineData("rea")]
    [InlineData("mon")]
    [InlineData("mon d")]
    [InlineData("2")]
    [InlineData("*.md")]
    [InlineData("view *.cs")]
    public void ArrangingTheSortedListingIsTheSameAsSortingTheMatches(string query)
    {
        // The quick filter sorts the listing once and arranges it per key; what it shows has to be
        // what sorting the matches by rank and then by the listing's order showed before.
        var order = new ListingOrder();
        var entries = new[]
        {
            "README.md", "already.txt", "readme2.md", "Bread.md", "CPU monitor", "disk monitor",
            "pokemon", "monitored", "CPU monitor dump", "FileRowViewModel.cs", "view.cs", "file10.md",
            "file2.md", "x2y.cs", "Reader", "notes.md",
        }.Select(n => new FsEntry(FsUri.Local(@"C:\x\" + n), n, false, 0, null)).ToList();
        var matcher = NameQuery.Compile(query);

        var sorted = entries.ToList();
        sorted.Sort(order);
        var arranged = matcher.Arrange(sorted, e => e).Select(e => e.Name);

        var before = entries.Where(matcher.Matches).ToList();
        before.Sort((a, b) => matcher.Rank(a.Name) != matcher.Rank(b.Name)
            ? matcher.Rank(a.Name).CompareTo(matcher.Rank(b.Name))
            : order.Compare(a, b));

        arranged.ShouldNotBeEmpty();
        arranged.ShouldBe(before.Select(e => e.Name));
    }

    [Fact]
    public void AGlobIsNotRanked()
    {
        // A wildcard said where the text goes; ranking on top of it would second-guess it.
        var matcher = NameQuery.Compile("rea*");
        matcher.Rank("README.md").ShouldBe(matcher.Rank("already.txt"));
    }
}
