using DanceCommander.Core.Fs;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A local copy by Windows' own CopyFileEx: what a copy of the bytes lost (the date, the
/// attributes, the "downloaded from the internet" mark) comes along, and the commit is the same.
/// </summary>
public sealed class LocalCopyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-localcopy-").FullName;
    private readonly LocalFileSystemProvider _provider = new();

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private string At(string name) => Path.Combine(_root, name);

    private Task<long> CopyAsync(string from, string to, bool replace = false, CancellationToken ct = default) =>
        _provider.CopyWithinAsync(FsUri.Local(At(from)), FsUri.Local(At(to)), replace, durable: false, _ => { }, ct);

    [Fact]
    public async Task TheDateTheAttributesAndTheDownloadMarkComeAlong()
    {
        File.WriteAllText(At("setup.exe"), "MZ not really");
        File.WriteAllText(At("setup.exe") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var then = new DateTime(2020, 5, 17, 9, 30, 0, DateTimeKind.Local);
        File.SetLastWriteTime(At("setup.exe"), then);
        File.SetAttributes(At("setup.exe"), FileAttributes.Hidden);

        var length = await CopyAsync("setup.exe", "copy.exe");

        length.ShouldBe(new FileInfo(At("setup.exe")).Length);
        File.ReadAllText(At("copy.exe")).ShouldBe("MZ not really");
        File.GetLastWriteTime(At("copy.exe")).ShouldBe(then);
        File.GetAttributes(At("copy.exe")).HasFlag(FileAttributes.Hidden).ShouldBeTrue();
        // The mark that makes Windows warn before running something downloaded.
        File.ReadAllText(At("copy.exe") + ":Zone.Identifier").ShouldContain("ZoneId=3");
        Directory.GetFiles(_root, "*.part").ShouldBeEmpty();
    }

    [Fact]
    public async Task AReplaceThatIsNoLongerWantedAtTheCommitKeepsTheFileThere()
    {
        // Asked again at the commit: another transfer may have landed its file at the name while
        // a long copy ran (review, 2026-10-23).
        File.WriteAllText(At("new.txt"), "new");
        File.WriteAllText(At("there.txt"), "landed meanwhile");

        await Should.ThrowAsync<IOException>(() => _provider.CopyWithinAsync(
            FsUri.Local(At("new.txt")), FsUri.Local(At("there.txt")), replace: true, durable: false, _ => { },
            CancellationToken.None, mayReplace: () => false));

        File.ReadAllText(At("there.txt")).ShouldBe("landed meanwhile");
        Directory.GetFiles(_root, "*.part").ShouldBeEmpty();
    }

    [Fact]
    public async Task ACancelLeavesNothingAtTheNameAndNoPart()
    {
        using (var big = new FileStream(At("big.bin"), FileMode.CreateNew))
            big.SetLength(200L * 1024 * 1024);

        using var cancel = new CancellationTokenSource();
        var copying = _provider.CopyWithinAsync(
            FsUri.Local(At("big.bin")), FsUri.Local(At("copy.bin")), replace: false, durable: false, _ => cancel.Cancel(), cancel.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => copying);
        File.Exists(At("copy.bin")).ShouldBeFalse();
        Directory.GetFiles(_root, "copy.bin*").ShouldBeEmpty();
    }

    [Fact]
    public async Task SomeoneElsesPartIsLeftAlone()
    {
        File.WriteAllText(At("a.txt"), "ours");
        File.WriteAllText(At("b.txt.part"), "a browser's download in progress");

        await CopyAsync("a.txt", "b.txt");

        File.ReadAllText(At("b.txt")).ShouldBe("ours");
        File.ReadAllText(At("b.txt.part")).ShouldBe("a browser's download in progress");
    }

    [Fact]
    public async Task AFileThatAppearedIsNotReplacedUnlessTheUserSaidSo()
    {
        File.WriteAllText(At("a.txt"), "new");
        File.WriteAllText(At("b.txt"), "already there");

        await Should.ThrowAsync<IOException>(() => CopyAsync("a.txt", "b.txt"));

        File.ReadAllText(At("b.txt")).ShouldBe("already there");
        Directory.GetFiles(_root, "*.part").ShouldBeEmpty();
    }

    [Fact]
    public async Task AReadOnlyFileWhoseDeleteFailsStaysReadOnly()
    {
        // Cleared so the delete can go ahead; open in another program, it didn't, and must not be
        // left unprotected.
        File.WriteAllText(At("held.txt"), "kept");
        File.SetAttributes(At("held.txt"), FileAttributes.ReadOnly);

        using (new FileStream(At("held.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Should.ThrowAsync<IOException>(() => _provider.DeleteAsync(FsUri.Local(At("held.txt")), recursive: false, CancellationToken.None));
        }

        File.GetAttributes(At("held.txt")).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
    }

    [Fact]
    public async Task AReadOnlyFileReplacesOneTheUserChoseToReplace()
    {
        File.WriteAllText(At("a.txt"), "new");
        File.SetAttributes(At("a.txt"), FileAttributes.ReadOnly);
        File.WriteAllText(At("b.txt"), "old");

        await CopyAsync("a.txt", "b.txt", replace: true);

        File.ReadAllText(At("b.txt")).ShouldBe("new");
        File.GetAttributes(At("b.txt")).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
    }

    [Fact]
    public async Task AReadOnlyFileIsNotReplacedAndSaysWhy()
    {
        // A local Replace can't be undone, and one answer covers every clash after it: clearing
        // the mark destroyed read-only files nobody was asked about (review, 2026-10-11).
        File.WriteAllText(At("a.txt"), "new");
        File.WriteAllText(At("b.txt"), "old, read-only");
        File.SetAttributes(At("b.txt"), FileAttributes.ReadOnly);

        var thrown = await Should.ThrowAsync<IOException>(() => CopyAsync("a.txt", "b.txt", replace: true));

        thrown.Message.ShouldContain("read-only");
        File.ReadAllText(At("b.txt")).ShouldBe("old, read-only");
        File.GetAttributes(At("b.txt")).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
        Directory.GetFiles(_root, "*.part").ShouldBeEmpty();
    }

    [Fact]
    public async Task AFileOpenElsewhereIsSaidToBeInUseAndKept()
    {
        // Renaming over a file another program has open is "access denied" to Windows, as for a
        // permission; said that way, the transfer never offered to Retry.
        File.WriteAllText(At("a.txt"), "new");
        File.WriteAllText(At("b.txt"), "old, open in an editor");

        using (new FileStream(At("b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var thrown = await Should.ThrowAsync<IOException>(() => CopyAsync("a.txt", "b.txt", replace: true));
            DanceCommander.Core.Operations.InUse.Is(thrown).ShouldBeTrue(thrown.Message);
        }

        File.ReadAllText(At("b.txt")).ShouldBe("old, open in an editor");
        Directory.GetFiles(_root, "*.part").ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLongestNameCopiesAndReplaces()
    {
        // 255 characters, the most a name may have: name.part was refused as too long.
        var name = new string('n', 251) + ".txt";
        File.WriteAllText(At("a.txt"), "new");
        File.WriteAllText(At("b.txt"), "the other new");
        File.WriteAllText(At(name), "old");

        await CopyAsync("a.txt", name, replace: true);
        File.ReadAllText(At(name)).ShouldBe("new");

        var fresh = Directory.CreateDirectory(At("fresh")).FullName;
        await _provider.CopyWithinAsync(FsUri.Local(At("b.txt")), FsUri.Local(Path.Combine(fresh, name)), replace: false, durable: false, _ => { }, CancellationToken.None);
        File.ReadAllText(Path.Combine(fresh, name)).ShouldBe("the other new");
        Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLongestNameIsWrittenAndCommitted()
    {
        var name = new string('w', 251) + ".txt";
        await using (var stream = await _provider.OpenWriteAsync(FsUri.Local(At(name)), 5, CancellationToken.None))
        {
            await stream.WriteAsync("hello"u8.ToArray());
            await ((ICommittableStream)stream).CommitAsync(replace: false, CancellationToken.None);
        }

        File.ReadAllText(At(name)).ShouldBe("hello");
    }
}

public sealed class TemporaryNameTests
{
    [Fact]
    public void AShortNameKeepsAllOfItself() =>
        TemporaryName.Beside("a.txt", ".part").ShouldBe("a.txt.part");

    [Theory]
    [InlineData(".part")]
    [InlineData(".dc-old-0123456789abcdef0123456789abcdef")]
    public void ALongNameIsShortenedAndTheSuffixKept(string suffix)
    {
        var made = TemporaryName.Beside(new string('n', 251) + ".txt", suffix);

        made.ShouldEndWith(suffix);
        System.Text.Encoding.UTF8.GetByteCount(made).ShouldBe(TemporaryName.MostBytes);
    }

    [Fact]
    public void ANameNotInAsciiFitsInBytesAndNeverLosesHalfACharacter()
    {
        // 200 characters of two bytes each: 400 bytes, past a server's 255 though not NTFS's.
        var made = TemporaryName.Beside(new string('ä', 200), ".part");

        System.Text.Encoding.UTF8.GetByteCount(made).ShouldBeLessThanOrEqualTo(TemporaryName.MostBytes);
        made.ShouldBe(new string('ä', 125) + ".part");
    }

    [Fact]
    public void ANameThatAlreadyEndsInTheSuffixIsNeverItsOwnTemporary()
    {
        // A browser's long download: shortened to fit, it came out as itself, and the copy was
        // written at the real name (review, 2026-10-11).
        var name = new string('d', 250) + ".part";

        var made = TemporaryName.Beside(name, ".part");

        made.ShouldNotBe(name);
        made.ShouldEndWith(".part");
        System.Text.Encoding.UTF8.GetByteCount(made).ShouldBeLessThanOrEqualTo(TemporaryName.MostBytes);
    }

    [Fact]
    public void OnlyTheLastPartOfAServersPathIsShortened() =>
        TemporaryName.BesideInPath("/home/me/" + new string('n', 255), ".part")
            .ShouldBe("/home/me/" + new string('n', 250) + ".part");
}
