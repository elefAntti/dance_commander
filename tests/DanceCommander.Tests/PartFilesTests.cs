using System.Security.Cryptography;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Split into parts and Join parts on real files. What these guard is a set that joins into a
/// file that looks whole and isn't: a part cut short, or one damaged on the way.
/// </summary>
public sealed class PartFilesTests : IDisposable
{
    private const int Part = 1024 * 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("dc-parts-").FullName;
    private readonly IProviderRegistry _providers = new ProviderRegistry([new LocalFileSystemProvider()]);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string At(string name) => Path.Combine(_root, name);

    private FsUri Folder => FsUri.Local(_root);

    /// <summary>Two and a half parts of bytes that differ from part to part.</summary>
    private byte[] WriteSource(string name = "big.iso")
    {
        var bytes = new byte[Part * 5 / 2];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(At(name), bytes);
        return bytes;
    }

    [Theory]
    [InlineData("100", 100L * 1024 * 1024)]
    [InlineData("100 MB", 100L * 1024 * 1024)]
    [InlineData("1,5 GB", 1536L * 1024 * 1024)]
    [InlineData("1.5g", 1536L * 1024 * 1024)]
    [InlineData("500k", 500L * 1024)]
    [InlineData(" 2 MiB ", 2L * 1024 * 1024)]
    public void ASizeIsReadAsTyped(string text, long expected) => PartFiles.ParseSize(text).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("big")]
    [InlineData("0")]
    [InlineData("-5 MB")]
    [InlineData("10 parsecs")]
    public void ANonsenseSizeIsNone(string text) => PartFiles.ParseSize(text).ShouldBeNull();

    [Fact]
    public async Task ASplitJoinsBackIntoTheSameBytes()
    {
        var bytes = WriteSource();

        var made = await PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None);

        made.Select(u => u.Name).ShouldBe(["big.iso.001", "big.iso.002", "big.iso.003", "big.iso.sha256"]);
        new FileInfo(At("big.iso.003")).Length.ShouldBe(Part / 2);

        // The line sha256sum -c reads, with the hash of the whole file.
        File.ReadAllText(At("big.iso.sha256"))
            .ShouldBe($"{Convert.ToHexStringLower(SHA256.HashData(bytes))} *big.iso\n");

        var joined = await PartFiles.JoinAsync(_providers, FsUri.Local(At("big.iso.001")), FsUri.Local(At("again.iso")), CancellationToken.None);

        joined.ShouldBe((3, true));
        File.ReadAllBytes(At("again.iso")).ShouldBe(bytes);
    }

    [Fact]
    public async Task APartCutShortIsRefusedAndNothingIsJoined()
    {
        WriteSource();
        await PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None);
        using (var part = File.OpenWrite(At("big.iso.002")))
            part.SetLength(Part - 10);

        var thrown = await Should.ThrowAsync<IOException>(() =>
            PartFiles.JoinAsync(_providers, FsUri.Local(At("big.iso.001")), FsUri.Local(At("again.iso")), CancellationToken.None));

        thrown.Message.ShouldContain("big.iso.002");
        File.Exists(At("again.iso")).ShouldBeFalse();
    }

    [Fact]
    public async Task ADamagedPartFailsTheChecksumAndNothingIsKept()
    {
        WriteSource();
        await PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None);

        // The right size, one byte wrong: only the checksum can tell.
        var damaged = File.ReadAllBytes(At("big.iso.002"));
        damaged[1234] ^= 0xFF;
        File.WriteAllBytes(At("big.iso.002"), damaged);

        var thrown = await Should.ThrowAsync<IOException>(() =>
            PartFiles.JoinAsync(_providers, FsUri.Local(At("big.iso.001")), FsUri.Local(At("again.iso")), CancellationToken.None));

        thrown.Message.ShouldContain("does not match");
        File.Exists(At("again.iso")).ShouldBeFalse();
        Directory.GetFiles(_root, "again.iso*").ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutAChecksumTheJoinSaysItWasNotChecked()
    {
        var bytes = WriteSource();
        await PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None);
        File.Delete(At("big.iso.sha256"));

        var joined = await PartFiles.JoinAsync(_providers, FsUri.Local(At("big.iso.001")), FsUri.Local(At("again.iso")), CancellationToken.None);

        joined.ShouldBe((3, false));
        File.ReadAllBytes(At("again.iso")).ShouldBe(bytes);
    }

    [Fact]
    public async Task AnEarlierSetIsNeverWrittenOver()
    {
        WriteSource();
        File.WriteAllText(At("big.iso.002"), "someone else's");

        await Should.ThrowAsync<IOException>(() =>
            PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None));

        File.ReadAllText(At("big.iso.002")).ShouldBe("someone else's");
        File.Exists(At("big.iso.001")).ShouldBeFalse();
    }

    [Fact]
    public async Task AMissingTailWithoutAChecksumIsNotCalledWhole()
    {
        // Killed after the second part: both are full-size, and nothing says there were three.
        WriteSource();
        await PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None);
        File.Delete(At("big.iso.003"));
        File.Delete(At("big.iso.sha256"));

        var thrown = await Should.ThrowAsync<IOException>(() =>
            PartFiles.JoinAsync(_providers, FsUri.Local(At("big.iso.001")), FsUri.Local(At("again.iso")), CancellationToken.None));

        thrown.Message.ShouldContain("no telling");
        File.Exists(At("again.iso")).ShouldBeFalse();
    }

    [Fact]
    public async Task AGapInTheNumberingIsAMissingPart()
    {
        WriteSource();
        await PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None);
        File.Delete(At("big.iso.002"));

        var thrown = await Should.ThrowAsync<IOException>(() =>
            PartFiles.JoinAsync(_providers, FsUri.Local(At("big.iso.001")), FsUri.Local(At("again.iso")), CancellationToken.None));

        thrown.Message.ShouldContain("big.iso.003 is there but big.iso.002 is not");
    }

    [Fact]
    public async Task AnEmptyChecksumFileIsNotTakenAsNone()
    {
        WriteSource();
        await PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None);
        File.WriteAllText(At("big.iso.sha256"), string.Empty);

        await Should.ThrowAsync<IOException>(() =>
            PartFiles.JoinAsync(_providers, FsUri.Local(At("big.iso.001")), FsUri.Local(At("again.iso")), CancellationToken.None));

        File.Exists(At("again.iso")).ShouldBeFalse();
    }

    [Fact]
    public async Task ALongerSetFromBeforeIsNotContinued()
    {
        // A split into three beside a .004 left from an earlier, finer one would join it as a tail.
        WriteSource();
        File.WriteAllText(At("big.iso.004"), "earlier");

        await Should.ThrowAsync<IOException>(() =>
            PartFiles.SplitAsync(_providers, FsUri.Local(At("big.iso")), Folder, Part, CancellationToken.None));

        File.Exists(At("big.iso.001")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("big.iso.001", true)]
    [InlineData("big.iso.002", false)]
    [InlineData(".001", false)]
    [InlineData("notes.txt", false)]
    public void OnlyAFirstPartStartsAJoin(string name, bool expected) => PartFiles.IsFirstPart(name).ShouldBe(expected);
}
