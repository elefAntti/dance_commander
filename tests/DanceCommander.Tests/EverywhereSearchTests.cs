using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Navigation;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The address bar's search: the whole computer, from where the pane stands outwards, likeliest
/// places first, and what is found in the order it is most likely wanted.
/// </summary>
public sealed class EverywhereSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-every-").FullName;
    private readonly IProviderRegistry _providers = new ProviderRegistry([new LocalFileSystemProvider()]);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;

    private string Touch(string relative)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, relative))!);
        File.WriteAllText(Path.Combine(_root, relative), "");
        return Path.Combine(_root, relative);
    }

    private FsUri At(string relative) => FsUri.Local(Path.Combine(_root, relative));

    private async Task<(List<FsEntry> Rows, string? Omission)> SearchAsync(
        string query, IReadOnlyList<SearchArea> areas, bool showHidden = false)
    {
        var source = new EverywhereListingSource(
            _providers, FsUri.View(FsUri.EverywhereScheme, areas[0].Root, query),
            new ListingFilter(showHidden), areas);
        var rows = new List<FsEntry>();
        await foreach (var row in source.ListAsync(CancellationToken.None))
            rows.Add(row);
        return (rows, source.Omission);
    }

    [Fact]
    public void TheAddressBarSearchesEverywhereFromALocalFolderAndOnlyHereFromAServer()
    {
        var work = FsUri.Local(@"C:\work");
        AddressSearch.Everywhere(work, " report ").ShouldBe(FsUri.View(FsUri.EverywhereScheme, work, "report"));

        // Searching again from the results starts from the same folder, not from the results.
        var results = AddressSearch.Everywhere(work, "report");
        AddressSearch.Everywhere(results, "plan").ShouldBe(FsUri.View(FsUri.EverywhereScheme, work, "plan"));

        var server = FsUri.Parse("sftp://build01/var/log");
        AddressSearch.Everywhere(server, "syslog").ShouldBe(FsUri.View("search", server, "syslog"));
    }

    [Fact]
    public void AnEverywhereSearchIsAnAddressLikeAnyOther()
    {
        var uri = FsUri.View(FsUri.EverywhereScheme, FsUri.Local(@"C:\work"), "report");

        FsUri.Parse(uri.ToString()).ShouldBe(uri);
        FsUri.Parse(uri.Display).ShouldBe(uri);
        uri.Parent.ShouldBe(FsUri.Local(@"C:\work")); // Up goes back to where it started
    }

    [Fact]
    public void EachPlaceIsSearchedOnceAtItsLikeliestAndOnlyIfItIsThere()
    {
        var documents = At("Documents");
        var areas = SearchAreas.Arrange(
            [
                new SearchArea("Here", documents, SearchAreas.Here),
                new SearchArea("Documents", documents, SearchAreas.Yours),
                new SearchArea("OneDrive", At("OneDrive"), SearchAreas.Yours),
                new SearchArea("Home", At(""), SearchAreas.Home),
            ],
            uri => uri != At("OneDrive"));

        areas.Select(a => a.Label).ShouldBe(["Here", "Home"]);
    }

    [Fact]
    public async Task PlacesAreWalkedInOrderAndNothingTwice()
    {
        Touch(@"home\notes-old.txt");
        Touch(@"home\Documents\notes.txt");
        Touch(@"home\Documents\project\notes-here.txt");

        var areas = new[]
        {
            new SearchArea("Here", At(@"home\Documents\project"), SearchAreas.Here),
            new SearchArea("Documents", At(@"home\Documents"), SearchAreas.Yours),
            new SearchArea("Home", At("home"), SearchAreas.Home),
        };

        var (rows, _) = await SearchAsync("notes", areas);

        rows.Select(r => r.Name).ShouldBe(["notes-here.txt", "notes.txt", "notes-old.txt"]);
        rows.Select(r => r.Extra![ColumnIds.Where]).ShouldBe(["Here", "Documents", "Home"]);
    }

    [Fact]
    public async Task AFolderThatIsAPlaceOfItsOwnIsStillFoundByName()
    {
        Dir(@"home\Documents");

        var areas = new[]
        {
            new SearchArea("Documents", At(@"home\Documents"), SearchAreas.Yours),
            new SearchArea("Home", At("home"), SearchAreas.Home),
        };

        var (rows, _) = await SearchAsync("documents", areas);

        rows.ShouldHaveSingleItem().Uri.ShouldBe(At(@"home\Documents"));
    }

    [Fact]
    public async Task ClutterIsNotWalkedAndSaysSoUntilHiddenThingsAreShown()
    {
        Touch(@"repo\report.md");
        Touch(@"repo\node_modules\report\index.js");
        Touch(@"repo\Temp\report.tmp");
        var areas = new[] { new SearchArea("Here", At("repo"), SearchAreas.Here) };

        var (rows, omission) = await SearchAsync("report", areas);
        rows.Select(r => r.Name).ShouldBe(["report.md"]);
        omission.ShouldBe("2 folders of caches and packages not searched");

        var (all, _) = await SearchAsync("report", areas, showHidden: true);
        all.Select(r => r.Name).ShouldBe(["report", "report.md", "report.tmp"], ignoreOrder: true);
    }

    [Fact]
    public async Task APlaceThatCannotBeReadDoesNotEndTheSearch()
    {
        Touch(@"home\plan.txt");
        var areas = new[]
        {
            new SearchArea("Here", At("gone"), SearchAreas.Here),
            new SearchArea("Home", At("home"), SearchAreas.Home),
        };

        var (rows, omission) = await SearchAsync("plan", areas);

        rows.ShouldHaveSingleItem().Name.ShouldBe("plan.txt");
        omission.ShouldBe("1 folder could not be read");
    }

    [Fact]
    public async Task WhatAPersonMadeSortsBeforeWhatAMachineMade()
    {
        var old = DateTime.Now.AddYears(-2);
        File.SetLastWriteTime(Touch(@"src\Report.dll"), DateTime.Now);
        File.SetLastWriteTime(Touch(@"src\bin\Debug\Report.pdf"), DateTime.Now);
        File.SetLastWriteTime(Touch(@"src\annual report.docx"), old);
        File.SetLastWriteTime(Touch(@"src\Report.docx"), old);
        File.SetLastWriteTime(Touch(@"src\deep\er\Report.xlsx"), old);
        var areas = new[] { new SearchArea("Here", At("src"), SearchAreas.Here) };

        var (rows, _) = await SearchAsync("report", areas);
        rows.Sort(new ListingOrder(ColumnIds.Where));

        // A good name match first, a shallow one before a deep one; then a weaker match; what a
        // build or a compiler left behind last, however well its name matches or however new.
        rows.Select(r => r.Name).ShouldBe(
            ["Report.docx", "Report.xlsx", "annual report.docx", "Report.dll", "Report.pdf"]);
    }

    [Fact]
    public void AColumnWithASortKeySortsByItAndShowsItsText()
    {
        FsEntry Row(string name, string where, string key) => new(
            At(name), name, false, 0, null,
            Extra: new Dictionary<string, string> { [ColumnIds.Where] = where, [ColumnIds.Where + ColumnIds.SortKeySuffix] = key });

        var rows = new List<FsEntry> { Row("b", "Windows", "6"), Row("a", "Here", "0"), Row("c", "Documents", "1") };
        rows.Sort(new ListingOrder(ColumnIds.Where));

        rows.Select(r => r.Extra![ColumnIds.Where]).ShouldBe(["Here", "Documents", "Windows"]);
    }
}
