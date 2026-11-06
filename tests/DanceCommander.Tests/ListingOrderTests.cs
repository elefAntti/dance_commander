using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using Shouldly;

namespace DanceCommander.Tests;

public class ListingOrderTests
{
    private static readonly FsUri Root = FsUri.Local(@"C:\work");

    private static FsEntry Entry(string name, bool directory = false, long size = 0, string? modified = null) =>
        new(Root.Child(name), name, directory, size,
            modified is null ? null : DateTimeOffset.Parse(modified));

    private static List<string> Sorted(ListingOrder order, params FsEntry[] entries)
    {
        var list = entries.ToList();
        list.Sort(order);
        return list.Select(e => e.Name).ToList();
    }

    [Fact]
    public void ByTypeFoldersAreATypeOfTheirOwnAndComeFirst()
    {
        var entries = new[]
        {
            Entry("notes.txt"),
            Entry("Makefile"),
            Entry("v1.2", directory: true),
            Entry("app.cs"),
            Entry("assets", directory: true),
        };

        // Folders first and by name (the dot in "v1.2" is not a type), then files with no
        // extension, then by extension.
        Sorted(new ListingOrder(ColumnIds.Extension), entries)
            .ShouldBe(["assets", "v1.2", "Makefile", "app.cs", "notes.txt"]);

        // Reversed, the types reverse; folders stay a separate, leading group, and names within a
        // type still read A-Z — the direction is the column's, not the tie-break's.
        Sorted(new ListingOrder(ColumnIds.Extension, Descending: true), entries)
            .ShouldBe(["assets", "v1.2", "notes.txt", "app.cs", "Makefile"]);
    }

    [Fact]
    public void SortsFoldersTogetherWithFilesByDefault()
    {
        var order = new ListingOrder();

        Sorted(order,
            Entry("zebra", directory: true),
            Entry("apple.txt"),
            Entry("mango", directory: true),
            Entry("banana.txt"))
            .ShouldBe(["apple.txt", "banana.txt", "mango", "zebra"]);
    }

    [Fact]
    public void PinsFoldersOnTopWhenAskedTo()
    {
        var order = new ListingOrder(DirectoriesFirst: true);

        Sorted(order,
            Entry("zebra", directory: true),
            Entry("apple.txt"),
            Entry("mango", directory: true))
            .ShouldBe(["mango", "zebra", "apple.txt"]);
    }

    [Fact]
    public void KeepsFoldersOnTopEvenWhenSortingDescending()
    {
        var order = new ListingOrder(Descending: true, DirectoriesFirst: true);

        Sorted(order, Entry("apple.txt"), Entry("mango", directory: true))
            .ShouldBe(["mango", "apple.txt"]);
    }

    [Fact]
    public void ComparesNumbersInNamesAsNumbers()
    {
        Sorted(new ListingOrder(),
            Entry("file10.log"), Entry("file9.log"), Entry("file1.log"), Entry("file2.log"))
            .ShouldBe(["file1.log", "file2.log", "file9.log", "file10.log"]);
    }

    [Fact]
    public void GivesZeroPaddedNamesADeterministicOrderRatherThanCallingThemEqual()
    {
        // Numerically identical, so the only sensible rule is fewer leading zeros first.
        Sorted(new ListingOrder(), Entry("file09.log"), Entry("file9.log"))
            .ShouldBe(["file9.log", "file09.log"]);
    }

    [Fact]
    public void SortsSizesAndDatesNumericallyNotAsText()
    {
        Sorted(new ListingOrder(ColumnIds.Size),
            Entry("big", size: 1000), Entry("small", size: 9), Entry("mid", size: 100))
            .ShouldBe(["small", "mid", "big"]);

        Sorted(new ListingOrder(ColumnIds.Modified),
            Entry("newest", modified: "2026-09-16T10:00:00Z"),
            Entry("oldest", modified: "2024-01-02T10:00:00Z"))
            .ShouldBe(["oldest", "newest"]);
    }

    [Fact]
    public void FallsBackToNameWhenTheSortedColumnTies()
    {
        Sorted(new ListingOrder(ColumnIds.Size),
            Entry("beta.txt", size: 10), Entry("alpha.txt", size: 10))
            .ShouldBe(["alpha.txt", "beta.txt"]);
    }

    [Fact]
    public void TogglingTheSameColumnFlipsDirection()
    {
        var order = new ListingOrder();

        order = order.ToggledBy(ColumnIds.Name);
        order.Descending.ShouldBeTrue();

        order = order.ToggledBy(ColumnIds.Name);
        order.Descending.ShouldBeFalse();
    }

    [Fact]
    public void ANewColumnStartsAtItsInterestingEnd()
    {
        // Nobody clicks Modified to find their oldest file, or Size to find their smallest.
        var order = new ListingOrder();

        order.ToggledBy(ColumnIds.Modified).Descending.ShouldBeTrue();
        order.ToggledBy(ColumnIds.Size).Descending.ShouldBeTrue();

        // A name column is for A-Z, which is the whole reason this is not a blanket rule. Coming
        // from another column, so that this tests arriving at Name rather than flipping it: a new
        // ListingOrder is already sorted by name.
        var elsewhere = order.ToggledBy(ColumnIds.Size);
        elsewhere.ToggledBy(ColumnIds.Name).Descending.ShouldBeFalse();
        elsewhere.ToggledBy(ColumnIds.Extension).Descending.ShouldBeFalse();
    }

    [Fact]
    public void TreatsDotfilesAndAttributeHiddenEntriesAlike()
    {
        var filter = new ListingFilter();

        filter.Matches(Entry("notes.md")).ShouldBeTrue();
        filter.Matches(Entry(".gitignore")).ShouldBeFalse();
        filter.Matches(Entry(".config", directory: true)).ShouldBeFalse();
        filter.Matches(Entry("thumbs.db") with { Attributes = FsEntryAttributes.Hidden }).ShouldBeFalse();
        filter.Matches(Entry("pagefile") with { Attributes = FsEntryAttributes.System }).ShouldBeFalse();

        filter.Toggled().Matches(Entry(".gitignore")).ShouldBeTrue();
    }
}
