using System.Text;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Providers.Local;
using DanceCommander.Providers.Local.Everything;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A search answered by an index (Everything) gives the walk's rows: the index only proposes, and
/// each proposal is judged by the walk's own rules.
/// </summary>
public sealed class IndexedSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-index-").FullName;
    private readonly IProviderRegistry _providers = new ProviderRegistry([new LocalFileSystemProvider()]);

    public IndexedSearchTests()
    {
        Touch("a.txt");
        Touch(@"docs\b.txt");
        Touch(@"docs\deep\c.txt");
        Touch(@".git\config.txt");
        Touch(@"hid\h.txt");
        File.SetAttributes(Path.Combine(_root, "hid"), FileAttributes.Directory | FileAttributes.Hidden);
        Touch(@"sys\s.txt");
        File.SetAttributes(Path.Combine(_root, "sys"), FileAttributes.Directory | FileAttributes.System);
        Touch(@"node_modules\pkg\n.txt");
        Touch("secret.txt");
        File.SetAttributes(Path.Combine(_root, "secret.txt"), FileAttributes.Hidden);

        // notes. beside notes: Win32 without \\?\ takes one for the other (AUDIT.md §2 kind 45).
        Touch(@"notes\e.txt");
        Directory.CreateDirectory(@"\\?\" + Path.Combine(_root, "notes."));
        File.WriteAllText(@"\\?\" + Path.Combine(_root, "notes.", "d.txt"), "");

        Junction.Create(Path.Combine(_root, "link"), Path.Combine(_root, "docs"));
    }

    public void Dispose()
    {
        Directory.Delete(Path.Combine(_root, "link")); // the junction alone, before what it leads to
        foreach (var folder in new[] { "hid", "sys" })
            File.SetAttributes(Path.Combine(_root, folder), FileAttributes.Directory);
        Directory.Delete(@"\\?\" + Path.Combine(_root, "notes."), recursive: true);
        Directory.Delete(_root, recursive: true);
    }

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private FsUri Root => FsUri.Local(_root);

    /// <summary>
    /// An index as loose as one can be: every path under the folder, whatever was asked, as Win32
    /// paths. What a reparse point leads to is listed only where it is, as Everything lists it.
    /// </summary>
    private sealed class WholeTree : ILocalSearchIndex
    {
        public Func<FsUri, IReadOnlyList<IndexedPath>?>? Answer { get; init; }

        public List<string> Extra { get; } = [];

        public string Name => "the test index";

        public Task<IReadOnlyList<IndexedPath>?> CandidatesAsync(
            FsUri root, IReadOnlyList<string> terms, int limit, CancellationToken ct)
        {
            if (Answer is not null)
                return Task.FromResult(Answer(root));

            var paths = Everything(root.ToLocalPath()).Concat(Extra.Select(path => new IndexedPath(path, false))).ToList();
            return Task.FromResult<IReadOnlyList<IndexedPath>?>(paths);
        }

        public static IEnumerable<IndexedPath> Everything(string folder)
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                var isFolder = info.Attributes.HasFlag(FileAttributes.Directory);
                yield return new IndexedPath(info.FullName.Replace(@"\\?\", ""), isFolder);
                if (isFolder && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Through \\?\, or notes. is read as notes, as Everything never would.
                    var exact = info.FullName.StartsWith(@"\\?\", StringComparison.Ordinal) ? info.FullName : @"\\?\" + info.FullName;
                    foreach (var below in Everything(exact))
                        yield return below;
                }
            }
        }
    }

    private static async Task<List<string>> RowsOf(IListingSource source)
    {
        var rows = new List<string>();
        await foreach (var row in source.ListAsync(CancellationToken.None))
        {
            var extra = row.Extra is null ? "" : string.Join(";", row.Extra.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
            // A folder's time as its parent's listing has it lags what the folder itself says: NTFS
            // updates the copy in the listing lazily, and the index's rows are stat-ed. The walk's
            // is the stale one, by milliseconds.
            var modified = row.IsDirectory ? "" : $"{row.Modified:O}";
            rows.Add($"{row.Uri}|{row.Name}|{row.IsDirectory}|{row.Size}|{modified}|{row.Attributes}|{row.LinkTarget}|{extra}");
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private IListingSource Search(string query, ILocalSearchIndex? index, bool showHidden = false) =>
        new SearchListingSource(_providers, FsUri.View("search", Root, query), new ListingFilter(showHidden), index);

    private IListingSource Everywhere(string query, ILocalSearchIndex? index, bool showHidden = false)
    {
        var areas = new[]
        {
            new SearchArea("Here", Root.Child("docs"), SearchAreas.Here),
            new SearchArea("Home", Root, SearchAreas.Home),
        };
        return new EverywhereListingSource(
            _providers, FsUri.View(FsUri.EverywhereScheme, Root.Child("docs"), query), new ListingFilter(showHidden), areas, index);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("txt", false)]
    [InlineData("*.txt", false)]
    [InlineData("e", false)]
    [InlineData("", true)]
    [InlineData("*.txt", true)]
    public async Task ASearchAnsweredByTheIndexListsWhatTheWalkLists(string query, bool showHidden)
    {
        var index = new WholeTree();

        var walked = await RowsOf(Search(query, null, showHidden));
        var answered = Search(query, index, showHidden);
        var rows = await RowsOf(answered);

        answered.AnsweredBy.ShouldBe("the test index");
        rows.ShouldBe(walked);
        walked.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("*.txt", false)]
    [InlineData("n", false)]
    [InlineData("*.txt", true)]
    public async Task AnEverywhereSearchAnsweredByTheIndexListsWhatTheWalkLists(string query, bool showHidden)
    {
        var walked = await RowsOf(Everywhere(query, null, showHidden));
        var answered = Everywhere(query, new WholeTree(), showHidden);
        var rows = await RowsOf(answered);

        answered.AnsweredBy.ShouldBe("the test index for 2 of 2 places");
        rows.ShouldBe(walked);
    }

    [Fact]
    public async Task TheFindsInADeclinedFolderAreCountedNotShown()
    {
        var source = Search("*.txt", new WholeTree());
        var rows = await RowsOf(source);

        rows.ShouldNotContain(row => row.Contains("h.txt") || row.Contains("s.txt") || row.Contains("config.txt"));
        source.Omission.ShouldBe("3 found in hidden folders not shown");

        var everywhere = Everywhere("*.txt", new WholeTree());
        await RowsOf(everywhere);
        everywhere.Omission.ShouldBe("3 found in hidden folders and 1 in caches and packages not shown");
    }

    [Fact]
    public async Task ASearchNoLongerGoesThroughALinkToAFolder()
    {
        // An index lists what is behind a link only where it really is, so the walk doesn't go
        // through one either, or the answer would depend on which gave it (2026-11-10).
        var rows = await RowsOf(Search("c", null));

        rows.ShouldContain(row => row.StartsWith(Root.Child("docs").Child("deep").Child("c.txt") + "|", StringComparison.Ordinal));
        rows.ShouldNotContain(row => row.Contains("/link/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatHasGoneSinceTheIndexSawItIsNoRow()
    {
        var index = new WholeTree();
        index.Extra.Add(Path.Combine(_root, "docs", "gone.txt"));
        index.Extra.Add(Path.Combine(_root, "gone", "deeper.txt"));

        var source = Search("", index);
        (await RowsOf(source)).ShouldBe(await RowsOf(Search("", null)));
        source.AnsweredBy.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(@"C:\elsewhere\a.txt")]
    [InlineData(@"{root}\docs\..\..\a.txt")]
    [InlineData(@"{root}\docs\\b.txt")]
    [InlineData(@"{root}\docs\b.txt:stream")]
    [InlineData(@"{root}x\a.txt")]
    public async Task AnAnswerWithAPathThatCannotBeTheFoldersIsNotUsed(string path)
    {
        var index = new WholeTree();
        index.Extra.Add(path.Replace("{root}", _root));

        var source = Search("", index);
        (await RowsOf(source)).ShouldBe(await RowsOf(Search("", null)));
        source.AnsweredBy.ShouldBeNull(); // the walk did it
    }

    [Fact]
    public async Task ASearchAskedToWalkLeavesTheIndexOut()
    {
        // Ctrl+R after the index found nothing: the disk itself, since the index may lag it. Not
        // everywhere: from the factory, which takes it to this machine's places: the whole disk
        // walked. It is handed the index by the same line.
        var factory = new ListingSourceFactory(_providers, index: new WholeTree());
        var uri = FsUri.View("search", Root.Child("docs"), "b");

        var asked = factory.Create(uri, new ListingFilter());
        var walked = factory.Create(uri, new ListingFilter(), walk: true);
        await RowsOf(asked);
        await RowsOf(walked);

        asked.AnsweredBy.ShouldNotBeNull();
        walked.AnsweredBy.ShouldBeNull();
    }

    [Fact]
    public async Task NoAnswerFromTheIndexIsTheWalk()
    {
        var source = Search("txt", new WholeTree { Answer = _ => null });

        (await RowsOf(source)).ShouldBe(await RowsOf(Search("txt", null)));
        source.AnsweredBy.ShouldBeNull();
    }

    [Fact]
    public async Task TheNameWithADotIsItselfNotItsNeighbour()
    {
        var rows = await RowsOf(Search("d", new WholeTree()));

        rows.ShouldContain(row => row.Contains("/notes./d.txt|", StringComparison.Ordinal));
        rows.ShouldNotContain(row => row.Contains("/notes/d.txt", StringComparison.Ordinal));
    }

    // ---- Everything itself ------------------------------------------------------------------

    private static byte[] Reply(uint total, uint flags, params (string Path, uint Flags)[] items)
    {
        var data = new List<byte>();
        void U32(uint value) => data.AddRange(BitConverter.GetBytes(value));
        U32(total);
        U32((uint)items.Length);
        U32(0);
        U32(flags);
        U32(1);

        var at = 20 + items.Length * 8;
        var texts = new List<byte>();
        foreach (var (path, itemFlags) in items)
        {
            U32(itemFlags);
            U32((uint)(at + texts.Count));
            texts.AddRange(BitConverter.GetBytes((uint)path.Length));
            // Char by char, so half a surrogate pair goes as it is, as Windows lets a name have one.
            foreach (var c in path + "\0")
                texts.AddRange(BitConverter.GetBytes(c));
        }

        data.AddRange(texts);
        return data.ToArray();
    }

    [Fact]
    public void EverythingsReplyIsReadAsItsPathsAndWhichAreFolders()
    {
        var reply = EverythingIpc.Parse(Reply(5, 0x4, (@"C:\work\a.txt", 0), (@"C:\work\src", 1)))!;

        reply.Total.ShouldBe(5u);
        reply.Paths.ShouldBe([new IndexedPath(@"C:\work\a.txt", false), new IndexedPath(@"C:\work\src", true)]);
    }

    [Fact]
    public void AReplyThatDoesNotAddUpIsNoReply()
    {
        var whole = Reply(1, 0x4, (@"C:\work\a.txt", 0));

        EverythingIpc.Parse(whole[..^6]).ShouldBeNull(); // cut short
        EverythingIpc.Parse(Reply(1, 0x4 | 0x10, (@"C:\work\a.txt", 0))).ShouldBeNull(); // laid out otherwise
        EverythingIpc.Parse(Reply(0, 0x4, (@"C:\work\a.txt", 0))).ShouldBeNull(); // more sent than it has
        EverythingIpc.Parse([1, 2, 3]).ShouldBeNull();
    }

    [Fact]
    public void ANameWithHalfASurrogatePairIsNoReplyRatherThanAnotherName() =>
        // Read loosely it is a\uFFFD.txt, a file that isn't there, and the real one goes missing.
        EverythingIpc.Parse(Reply(1, 0x4, ("C:\\work\\a\uD800.txt", 0))).ShouldBeNull();

    [Fact]
    public async Task AFolderReachedThroughAJunctionIsLeftToTheWalk()
    {
        // Everything has what is behind a link only where it really is: asked about the link's
        // side it finds nothing, and the folder would look empty.
        (await new EverythingIndex().CandidatesAsync(Root.Child("link"), ["b"], 100, CancellationToken.None))
            .ShouldBeNull();

        var walked = await RowsOf(new SearchListingSource(
            _providers, FsUri.View("search", Root.Child("link"), "b"), new ListingFilter(), new EverythingIndex()));
        walked.ShouldContain(row => row.Contains("/link/b.txt|", StringComparison.Ordinal));
    }

    private static readonly string[] Defaults =
    [
        "[Everything]",
        "exclude_list_enabled=1",
        "exclude_hidden_files_and_folders=0",
        "exclude_system_files_and_folders=0",
        "include_only_files=",
        "exclude_files=",
        "exclude_folders=",
        // As the file writes them, backslashes doubled.
        @"ntfs_volume_guids=""\\\\?\\Volume{0000000c-0000-0000-0000-000000000000}"",""\\\\?\\Volume{0000000d-0000-0000-0000-000000000000}"",""\\\\?\\Volume{0000000e-0000-0000-0000-000000000000}"",""\\\\?\\Volume{0000000f-0000-0000-0000-000000000000}""",
        "ntfs_volume_paths=\"C:\",\"D:\",\"E:\",\"F:\"",
        "ntfs_volume_includes=1,0,1,1",
        "ntfs_volume_monitors=1,1,1,0",
        "ntfs_volume_include_onlys=\"\",\"\",\"\",\"\"",
        "refs_volume_paths=",
        "refs_volume_includes=",
    ];

    [Fact]
    public void EverythingHasTheDrivesItIncludesAndWatchesKnownByTheirVolumes()
    {
        var volumes = EverythingSettings.Read(Defaults);

        // D: is not included, and F: not watched: its answer would be as old as its last rescan.
        volumes.Keys.Order().ShouldBe(["C:", "E:"]);
        volumes["C:"].ShouldBe("{0000000c-0000-0000-0000-000000000000}");
    }

    [Theory]
    [InlineData("exclude_folders=\"C:\\Users\\me\\private\"")]
    [InlineData("exclude_files=\"*.tmp\"")]
    [InlineData("include_only_files=\"*.docx\"")]
    [InlineData("exclude_hidden_files_and_folders=1")]
    [InlineData("exclude_system_files_and_folders=1")]
    [InlineData("ntfs_volume_include_onlys=\"C:\\work\",\"\",\"\"")]
    public void AnythingExcludedAnywhereMeansNoDriveIsWhole(string setting)
    {
        var key = setting[..setting.IndexOf('=')];
        var lines = Defaults.Where(line => !line.StartsWith(key + "=", StringComparison.Ordinal)).Append(setting);

        EverythingSettings.Read(lines).ShouldBeEmpty();
    }

    [Fact]
    public async Task AWordNoNameCanHoldIsAnsweredAtOnceAndOurOwnGlobsAreLeftToTheWalk()
    {
        var everything = new EverythingIndex();

        (await everything.CandidatesAsync(Root, ["a:b"], 100, CancellationToken.None)).ShouldBe([]);
        (await everything.CandidatesAsync(Root, ["src\\x"], 100, CancellationToken.None)).ShouldBe([]);
        (await everything.CandidatesAsync(Root, ["*.{cs,md}"], 100, CancellationToken.None)).ShouldBeNull();
        (await everything.CandidatesAsync(Root, ["[ab]*"], 100, CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>
    /// Against the Everything running on this machine, when there is one: a fresh folder in the
    /// temporary folder, found once Everything has seen it, and the walk's rows either way.
    /// Passes without asking anything where Everything isn't running, as on a build server.
    /// </summary>
    [Fact]
    public async Task TheRunningEverythingAnswersAsTheWalkDoes()
    {
        if (EverythingIpc.ProcessId() is null)
            return;

        var token = "dcidx" + Guid.NewGuid().ToString("N")[..8];
        Touch($@"docs\{token}-one.txt");
        Touch($@"docs\deep\{token}-two.txt");
        Touch($@"hid\{token}-three.txt");

        var everything = new EverythingIndex();
        IReadOnlyList<IndexedPath>? seen = null;
        for (var tries = 0; tries < 50 && (seen is null || seen.Count < 3); tries++)
        {
            await Task.Delay(100);
            seen = await everything.CandidatesAsync(Root, [token], 100, CancellationToken.None);
        }

        if (seen is null)
            return; // the temporary folder's drive isn't one it has whole

        seen.Count.ShouldBe(3);

        // Asked up to three times: run beside the other tests, which make and delete thousands of
        // files, Everything was once slower than the answer's timeout, and the walk answered
        // instead, as it should (2026-11-10).
        var answered = Search(token, everything);
        var rows = await RowsOf(answered);
        for (var tries = 1; tries < 3 && answered.AnsweredBy is null; tries++)
        {
            answered = Search(token, everything);
            rows = await RowsOf(answered);
        }

        answered.AnsweredBy.ShouldBe("Everything");
        rows.ShouldBe(await RowsOf(Search(token, null)));
        rows.Count.ShouldBe(2);
        answered.Omission.ShouldBe("1 found in a hidden folder not shown");
    }
}
