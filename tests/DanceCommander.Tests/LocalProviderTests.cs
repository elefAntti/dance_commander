using DanceCommander.Core.Fs;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class LocalProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-tests-").FullName;
    private readonly LocalFileSystemProvider _provider = new();

    private FsUri Root => FsUri.Local(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<List<FsEntry>> ListAsync()
    {
        var entries = new List<FsEntry>();
        await foreach (var entry in _provider.ListAsync(Root, CancellationToken.None))
            entries.Add(entry);
        return entries;
    }

    /// <summary>
    /// What a OneDrive file looks like to this process, measured: placeholders are disguised, so
    /// online-only is 0x400020 (recall on data access, archive) and "always keep" adds 0x80000.
    /// A test can't make a real placeholder, so it feeds the attributes in.
    /// </summary>
    [Theory]
    [InlineData(0x400020, FsEntryAttributes.CloudOnly)]
    [InlineData(0x1020, FsEntryAttributes.CloudOnly)]
    [InlineData(0x80020, FsEntryAttributes.AlwaysKept)]
    [InlineData(0x180026, FsEntryAttributes.AlwaysKept | FsEntryAttributes.Hidden | FsEntryAttributes.System)]
    [InlineData(0x20, FsEntryAttributes.None)]
    public void CloudFilesSayWhereTheirContentIs(int attributes, FsEntryAttributes expected) =>
        LocalFileSystemProvider.ToAttributes((FileAttributes)attributes).ShouldBe(expected);

    [Fact]
    public void AJunctionIsALinkAndAReparsePointThatNamesNothingIsNot()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var link = Path.Combine(_root, "jump");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();

        FileIdentity.ReparseTagOf(link).ShouldBe(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT, a name surrogate
        LocalFileSystemProvider.LinkTarget(new DirectoryInfo(link)).ShouldNotBeNull();
        LocalFileSystemProvider.LinkTarget(new DirectoryInfo(target)).ShouldBeNull();
        FileIdentity.ReparseTagOf(target).ShouldBeNull();

        // A Store app's alias is a reparse point that names no file .NET can follow: not a link.
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        if (Directory.Exists(aliases) && Directory.EnumerateFiles(aliases, "*.exe").FirstOrDefault() is { } alias)
        {
            var info = new FileInfo(alias);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && FileIdentity.ReparseTagOf(alias) == 0x8000001Bu)
                LocalFileSystemProvider.LinkTarget(info).ShouldBeNull();
        }

        Directory.Delete(link);
    }

    [Fact]
    public async Task AJunctionWithATrailingDotIsAskedAboutItselfNotItsNeighbour()
    {
        // Asked by its plain path, jump. was answered for the folder jump beside it: not a link,
        // and so walked through as a folder (review, 2026-10-28).
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "jump"));
        // mklink won't take the name, so an ordinary junction is renamed to it.
        var plain = Path.Combine(_root, "jumping");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", ["/c", "mklink", "/J", plain, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        var link = @"\\?\" + Path.Combine(_root, "jump.");
        Directory.Move(@"\\?\" + plain, link);
        Directory.Exists(link).ShouldBeTrue();

        // Asked by its plain path, as a listed child is: the tag is the junction's, not jump's.
        FileIdentity.Extended(Path.Combine(_root, "jump.")).ShouldEndWith("jump.");
        FileIdentity.ReparseTagOf(Path.Combine(_root, "jump.")).ShouldBe(0xA0000003u);

        var rows = await ListAsync();
        rows.Single(e => e.Name == "jump.").LinkTarget.ShouldNotBeNull();
        rows.Single(e => e.Name == "jump").LinkTarget.ShouldBeNull();
        Directory.Delete(link);
    }

    [Fact]
    public async Task DeletingNotesDotLeavesTheFolderNotesAlone()
    {
        // WSL and robocopy make such names; without \\?\ Win32 read notes. as notes (kind 45).
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        File.WriteAllText(Path.Combine(_root, "notes", "keep.txt"), "mine");
        var dotted = @"\\?\" + Path.Combine(_root, "notes.");
        Directory.CreateDirectory(dotted);
        File.WriteAllText(dotted + @"\scratch.txt", "x");

        var entry = (await ListAsync()).Single(e => e.Name == "notes.");
        (await _provider.StatAsync(entry.Uri, CancellationToken.None)).ShouldNotBeNull().Name.ShouldBe("notes.");
        await _provider.DeleteAsync(entry.Uri, recursive: true, CancellationToken.None);

        Directory.Exists(dotted).ShouldBeFalse();
        File.ReadAllText(Path.Combine(_root, "notes", "keep.txt")).ShouldBe("mine");
    }

    [Fact]
    public async Task ListsFilesAndDirectoriesWithUsableUris()
    {
        File.WriteAllText(Path.Combine(_root, "notes.md"), "hello");
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var entries = await ListAsync();

        entries.Count.ShouldBe(2);

        var file = entries.Single(e => e.Name == "notes.md");
        file.IsDirectory.ShouldBeFalse();
        file.Size.ShouldBe(5);
        file.Extension.ShouldBe("md");
        file.Uri.ShouldBe(Root.Child("notes.md"));

        entries.Single(e => e.Name == "src").IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task ShowsHiddenEntriesRatherThanFilteringThemOut()
    {
        var path = Path.Combine(_root, "hidden.txt");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.Hidden);

        var entries = await ListAsync();

        entries.ShouldHaveSingleItem().IsHidden.ShouldBeTrue();
    }

    [Fact]
    public async Task CreatesRenamesAndDeletes()
    {
        var folder = Root.Child("group");
        await _provider.CreateDirectoryAsync(folder, CancellationToken.None);
        Directory.Exists(folder.ToLocalPath()).ShouldBeTrue();

        var renamed = Root.Child("grouped");
        await _provider.MoveAsync(folder, renamed, CancellationToken.None);
        Directory.Exists(renamed.ToLocalPath()).ShouldBeTrue();

        await _provider.DeleteAsync(renamed, recursive: true, CancellationToken.None);
        Directory.Exists(renamed.ToLocalPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task RoundTripsContentThroughStreams()
    {
        var file = Root.Child("payload.bin");
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await using (var write = await _provider.OpenWriteAsync(file, payload.Length, CancellationToken.None))
        {
            await write.WriteAsync(payload);
            await write.CommitAsync(replace: false, CancellationToken.None);
        }

        await using var read = await _provider.OpenReadAsync(file, CancellationToken.None);
        read.CanSeek.ShouldBeTrue(); // RandomAccessRead: archives can be browsed in place
        var buffer = new byte[payload.Length];
        await read.ReadExactlyAsync(buffer);
        buffer.ShouldBe(payload);
        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["payload.bin"]); // no .part left
    }

    [Fact]
    public async Task AWriteNotCommittedLeavesTheOldFileWholeAndNoPartBehind()
    {
        // §1.6: a copy cancelled part way used to leave the old file already truncated.
        var path = Path.Combine(_root, "report.txt");
        File.WriteAllText(path, "the old report");

        await using (var write = await _provider.OpenWriteAsync(Root.Child("report.txt"), 100, CancellationToken.None))
            await write.WriteAsync("half of the n"u8.ToArray());

        File.ReadAllText(path).ShouldBe("the old report");
        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["report.txt"]);
    }

    [Fact]
    public async Task ACommittedWriteIsTrimmedToWhatWasWritten()
    {
        // Pre-sized to the length promised; a file that came out shorter must not keep zeros.
        var file = Root.Child("short.bin");
        await using (var write = await _provider.OpenWriteAsync(file, 1000, CancellationToken.None))
        {
            await write.WriteAsync(new byte[] { 1, 2, 3 });
            await write.CommitAsync(replace: false, CancellationToken.None);
        }

        File.ReadAllBytes(file.ToLocalPath()).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task AFileThatAppearsWhileWritingIsNotReplacedUnlessReplacingWasChosen()
    {
        // Two jobs bringing the same name both found it free; the second committed over the first.
        var path = Path.Combine(_root, "x.txt");
        await using (var write = await _provider.OpenWriteAsync(Root.Child("x.txt"), 3, CancellationToken.None))
        {
            await write.WriteAsync("new"u8.ToArray());
            File.WriteAllText(path, "arrived meanwhile");

            await Should.ThrowAsync<IOException>(() => write.CommitAsync(replace: false, CancellationToken.None));
        }

        File.ReadAllText(path).ShouldBe("arrived meanwhile");
        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["x.txt"]);
    }

    [Fact]
    public async Task AWriteNeverOpensOverSomeoneElsesPartFile()
    {
        // A browser's unfinished download is name.part too.
        File.WriteAllText(Path.Combine(_root, "setup.exe.part"), "a download in progress");

        await using (var write = await _provider.OpenWriteAsync(Root.Child("setup.exe"), 3, CancellationToken.None))
        {
            await write.WriteAsync("new"u8.ToArray());
            await write.CommitAsync(replace: false, CancellationToken.None);
        }

        File.ReadAllText(Path.Combine(_root, "setup.exe.part")).ShouldBe("a download in progress");
        File.ReadAllText(Path.Combine(_root, "setup.exe")).ShouldBe("new");
    }

    [Fact]
    public async Task TwoNamesForOneFileAreTheSameFile()
    {
        var original = Path.Combine(_root, "a.txt");
        File.WriteAllText(original, "one file");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "one file");
        HardLink.Create(Path.Combine(_root, "linked.txt"), original);

        (await _provider.IsSameFileAsync(Root.Child("a.txt"), Root.Child("linked.txt"), CancellationToken.None)).ShouldBeTrue();
        (await _provider.IsSameFileAsync(Root.Child("a.txt"), Root.Child("b.txt"), CancellationToken.None)).ShouldBeFalse();
        (await _provider.IsSameFileAsync(Root.Child("a.txt"), Root.Child("nope.txt"), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task StatReturnsNullForSomethingThatIsNotThere()
    {
        (await _provider.StatAsync(Root.Child("nope.txt"), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ListingIsCancellable()
    {
        for (var i = 0; i < 50; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "x");

        using var cts = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _provider.ListAsync(Root, cts.Token))
                cts.Cancel();
        });
    }
}
