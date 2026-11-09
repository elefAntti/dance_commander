using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.Archive;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Builds real archives on disk rather than checking in binaries, so the fixtures are readable
/// and a reviewer can see exactly what is being claimed.
/// </summary>
public abstract class ArchiveFixture : IDisposable
{
    protected readonly string Root = Directory.CreateTempSubdirectory("dc-arc-").FullName;

    public void Dispose() => Directory.Delete(Root, recursive: true);

    protected string At(string name) => Path.Combine(Root, name);

    /// <summary>A tar holding a small tree, written to <paramref name="path"/>.</summary>
    protected string WriteTar(string path)
    {
        var content = Path.Combine(Root, "staging");
        Directory.CreateDirectory(Path.Combine(content, "logs", "old"));
        File.WriteAllText(Path.Combine(content, "readme.txt"), "top level");
        File.WriteAllText(Path.Combine(content, "logs", "today.log"), "fresh");
        File.WriteAllText(Path.Combine(content, "logs", "old", "archive.log"), "stale");

        TarFile.CreateFromDirectory(content, path, includeBaseDirectory: false);
        Directory.Delete(content, recursive: true);
        return path;
    }

    /// <summary>The case that matters: a tarball whose name claims it is a plain gzip.</summary>
    protected string WriteMisnamedTarball(string name)
    {
        var tar = WriteTar(At("staging.tar"));
        var target = At(name);

        using (var input = File.OpenRead(tar))
        using (var output = File.Create(target))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            input.CopyTo(gzip);

        File.Delete(tar);
        return target;
    }

    protected string WritePlainGzip(string name, string text = "just one file, no tar in sight")
    {
        var target = At(name);
        using var output = File.Create(target);
        using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(text));
        return target;
    }

    protected string WriteZip(string name)
    {
        var target = At(name);
        using var file = File.Create(target);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        using (var writer = new StreamWriter(zip.CreateEntry("notes.md").Open()))
            writer.Write("# notes");
        using (var writer = new StreamWriter(zip.CreateEntry("src/app.cs").Open()))
            writer.Write("class App;");

        return target;
    }

    /// <summary>
    /// A gzip carrying the original filename in its header, which System.IO.Compression will not
    /// write — so the header is assembled by hand, which is also the only way to be sure the
    /// parser is reading the real layout rather than agreeing with itself.
    /// </summary>
    protected string WriteGzipNaming(string name, string originalName)
    {
        var target = At(name);
        var payload = Encoding.UTF8.GetBytes("contents");

        using var output = File.Create(target);
        output.Write([0x1F, 0x8B, 0x08, 0x08, 0, 0, 0, 0, 0, 0x03]); // FLG=FNAME
        output.Write(Encoding.ASCII.GetBytes(originalName));
        output.WriteByte(0);

        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(payload);

        output.Write(BitConverter.GetBytes(Crc32(payload)));
        output.Write(BitConverter.GetBytes((uint)payload.Length));
        return target;
    }

    /// <summary>
    /// A tar whose entries are named the way <c>tar -C dir .</c> names them: every path prefixed
    /// with "./". System.Formats.Tar does not write that prefix, which is precisely why it has to
    /// be constructed by hand -- fixtures that only exercise .NET's own writer miss it entirely.
    /// </summary>
    protected string WriteDotPrefixedTarball(string name)
    {
        var tar = At("dotted.tar");
        using (var file = File.Create(tar))
        using (var writer = new TarWriter(file, TarEntryFormat.Ustar))
        {
            void AddFile(string path, string text)
            {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, path);
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                writer.WriteEntry(entry);
            }

            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "./logs/"));
            AddFile("./readme.txt", "top level");
            AddFile("./logs/today.log", "fresh");
        }

        var target = At(name);
        using (var input = File.OpenRead(tar))
        using (var output = File.Create(target))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            input.CopyTo(gzip);

        File.Delete(tar);
        return target;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
        return ~crc;
    }
}

public sealed class ArchiveSnifferTests : ArchiveFixture
{
    [Fact]
    public async Task ReadsATarballThroughAMisleadingGzExtension()
    {
        // The whole reason detection is by content: these are everywhere.
        var path = WriteMisnamedTarball("logs.gz");

        var format = await ArchiveSniffer.DetectAsync(path);

        format.Kind.ShouldBe(ArchiveKind.GZip);
        format.ContainsTar.ShouldBeTrue();
        format.IsTree.ShouldBeTrue();
        format.Label.ShouldBe("tar.gz");
    }

    [Fact]
    public async Task APlainGzipIsOneFileNotATree()
    {
        var format = await ArchiveSniffer.DetectAsync(WritePlainGzip("notes.gz"));

        format.Kind.ShouldBe(ArchiveKind.GZip);
        format.ContainsTar.ShouldBeFalse();
        format.IsTree.ShouldBeFalse();
        format.Label.ShouldBe("gzip");
    }

    [Fact]
    public async Task RecognisesAZipWhateverItIsCalled()
    {
        var format = await ArchiveSniffer.DetectAsync(WriteZip("payload.dat"));

        format.Kind.ShouldBe(ArchiveKind.Zip);
        format.IsTree.ShouldBeTrue();
    }

    [Fact]
    public async Task RecognisesAnUncompressedTarWhichHasNoLeadingMagic()
    {
        var format = await ArchiveSniffer.DetectAsync(WriteTar(At("bundle.bin")));

        format.Kind.ShouldBe(ArchiveKind.Tar);
    }

    [Fact]
    public async Task OrdinaryFilesAreNotArchives()
    {
        var path = At("notes.txt");
        await File.WriteAllTextAsync(path, "nothing to see here");

        (await ArchiveSniffer.DetectAsync(path)).Kind.ShouldBe(ArchiveKind.None);
    }

    [Fact]
    public async Task AnEmptyFileIsNotAnArchive()
    {
        var path = At("empty.gz");
        await File.WriteAllBytesAsync(path, []);

        (await ArchiveSniffer.DetectAsync(path)).Kind.ShouldBe(ArchiveKind.None);
    }

    [Fact]
    public async Task ATruncatedArchiveDoesNotThrow()
    {
        var full = await File.ReadAllBytesAsync(WriteMisnamedTarball("cut.gz"));
        var path = At("truncated.gz");
        await File.WriteAllBytesAsync(path, full[..(full.Length / 3)]);

        // Whatever it decides, it must not blow up on the way to deciding.
        await Should.NotThrowAsync(() => ArchiveSniffer.DetectAsync(path));
    }

    [Fact]
    public async Task RecoversTheOriginalNameAGzipHeaderRecorded()
    {
        var path = WriteGzipNaming("mystery.gz", "the-real-name.tar");

        (await ArchiveSniffer.DetectAsync(path)).InnerName.ShouldBe("the-real-name.tar");
    }

    [Fact]
    public void ANameOnlyAppearsWhenTheHeaderFlagSaysSo()
    {
        // FLG = 0: no name follows, and the bytes after the header must not be read as one.
        byte[] header = [0x1F, 0x8B, 0x08, 0x00, 0, 0, 0, 0, 0, 0x03, (byte)'x', (byte)'y', 0];

        ArchiveSniffer.ReadGZipName(header).ShouldBeNull();
    }

    [Fact]
    public void AnEndOfArchiveBlockIsNotATarHeader() =>
        ArchiveSniffer.LooksLikeTar(new byte[512]).ShouldBeFalse();
}

public sealed class OpenArchiveTests : ArchiveFixture
{
    private static async Task<OpenArchive> Open(string path) =>
        await OpenArchive.OpenAsync(File.OpenRead(path), Path.GetFileName(path), CancellationToken.None);

    [Fact]
    public async Task BrowsesAMisnamedTarballAsADirectoryTree()
    {
        using var archive = await Open(WriteMisnamedTarball("logs.gz"));

        var root = archive.List(string.Empty).Select(n => n.Name).OrderBy(n => n).ToList();
        root.ShouldBe(["logs", "readme.txt"]);

        archive.List("logs").Select(n => n.Name).OrderBy(n => n).ShouldBe(["old", "today.log"]);
        archive.List("logs/old").Single().Name.ShouldBe("archive.log");
    }

    [Fact]
    public async Task SynthesisesDirectoriesThatHaveNoEntryOfTheirOwn()
    {
        // A zip written this way contains "src/app.cs" but no entry for "src".
        using var archive = await Open(WriteZip("bundle.zip"));

        archive.List(string.Empty).Select(n => n.Name).OrderBy(n => n).ShouldBe(["notes.md", "src"]);
        archive.IsDirectory("src").ShouldBeTrue();
        archive.List("src").Single().Name.ShouldBe("app.cs");
    }

    [Fact]
    public async Task APlainGzipHoldsExactlyOneFileNamedWithoutTheSuffix()
    {
        using var archive = await Open(WritePlainGzip("report.gz"));

        var only = archive.List(string.Empty).ShouldHaveSingleItem();
        only.Name.ShouldBe("report");
        only.IsDirectory.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadsAnEntryBackOutOfTheArchive()
    {
        using var archive = await Open(WriteMisnamedTarball("logs.gz"));

        using var reader = new StreamReader(archive.OpenRead("logs/today.log"));
        (await reader.ReadToEndAsync()).ShouldBe("fresh");
    }

    /// <summary>
    /// F5 on an entry while the preview reads another: both read through one source stream, and
    /// opening the second moved the first's position, so each got the other's bytes (kind 47).
    /// </summary>
    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    [InlineData("gz")]
    public async Task TwoEntriesReadAtOnceEachGetTheirOwnBytes(string kind)
    {
        var a = string.Concat(Enumerable.Repeat("first entry, line after line. ", 40_000));
        var b = string.Concat(Enumerable.Repeat("SECOND ENTRY, OTHER WORDS! ", 40_000));
        string path, nameA, nameB;
        switch (kind)
        {
            case "zip":
                path = At("two.zip");
                using (var file = File.Create(path))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("a.txt").Open()))
                        writer.Write(a);
                    using (var writer = new StreamWriter(zip.CreateEntry("b.txt").Open()))
                        writer.Write(b);
                }
                (nameA, nameB) = ("a.txt", "b.txt");
                break;
            case "tar.gz":
                var staging = Directory.CreateDirectory(At("two")).FullName;
                File.WriteAllText(Path.Combine(staging, "a.txt"), a);
                File.WriteAllText(Path.Combine(staging, "b.txt"), b);
                var tar = At("two.tar");
                TarFile.CreateFromDirectory(staging, tar, includeBaseDirectory: false);
                path = At("two.tar.gz");
                using (var input = File.OpenRead(tar))
                using (var output = File.Create(path))
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                    input.CopyTo(gzip);
                (nameA, nameB) = ("a.txt", "b.txt");
                break;
            default:
                // One entry, read twice at once: the same position shared all the same.
                path = WritePlainGzip("two.gz", a);
                (nameA, nameB, b) = ("two", "two", a);
                break;
        }

        using var archive = await Open(path);
        using var first = new StreamReader(archive.OpenRead(nameA));
        using var second = new StreamReader(archive.OpenRead(nameB));
        var buffer = new char[4096];
        var readA = new StringBuilder();
        var readB = new StringBuilder();
        int gotA, gotB;
        do
        {
            gotA = await first.ReadAsync(buffer);
            readA.Append(buffer, 0, gotA);
            gotB = await second.ReadAsync(buffer);
            readB.Append(buffer, 0, gotB);
        }
        while (gotA > 0 || gotB > 0);

        readA.ToString().ShouldBe(a);
        readB.ToString().ShouldBe(b);
    }

    [Fact]
    public async Task AServersArchiveThatArrivesShortIsNotOpened()
    {
        var tar = File.ReadAllBytes(WriteTar(At("whole.tar")));
        // A tar cut on a block edge reads as a smaller tar: nothing in it says what is missing.
        var cut = new NotSeekable(new MemoryStream(tar[..1024]));
        (await Should.ThrowAsync<IOException>(() =>
            OpenArchive.OpenAsync(cut, "whole.tar", CancellationToken.None, expectedLength: tar.Length))).Message.ShouldContain("whole.tar");

        using var whole = await OpenArchive.OpenAsync(
            new NotSeekable(new MemoryStream(tar)), "whole.tar", CancellationToken.None, expectedLength: tar.Length);
        whole.List(string.Empty).ShouldNotBeEmpty();
    }

    private sealed class NotSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ACutShortGzipReadToItsEndFails()
    {
        var whole = File.ReadAllBytes(WritePlainGzip("cut.gz", string.Concat(Enumerable.Repeat("some text that compresses ", 20_000))));
        File.WriteAllBytes(At("cut.gz"), whole[..(whole.Length / 2)]);
        using var archive = await Open(At("cut.gz"));

        await Should.ThrowAsync<Exception>(async () =>
        {
            await using var entry = archive.OpenRead("cut");
            await entry.CopyToAsync(Stream.Null);
        });
    }

    [Fact]
    public async Task AGzipPaddedWithZerosReadsWholeAndAJoinedOneFailsRatherThanGoShort()
    {
        // A tape pads with zeros. cat a.gz b.gz, bgzip and pigz write members one after another,
        // and SharpCompress read the first alone, quietly; it can't be vouched for whole, so it fails.
        var first = File.ReadAllBytes(WritePlainGzip("one.gz", "first part, "));
        var second = File.ReadAllBytes(WritePlainGzip("two.gz", "second part"));
        File.WriteAllBytes(At("joined.gz"), [.. first, .. second]);
        File.WriteAllBytes(At("padded.gz"), [.. first, .. new byte[512]]);

        using (var joined = await Open(At("joined.gz")))
        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await using var entry = joined.OpenRead("joined");
            await entry.CopyToAsync(Stream.Null);
        });

        using (var padded = await Open(At("padded.gz")))
        using (var reader = new StreamReader(padded.OpenRead("padded")))
            (await reader.ReadToEndAsync()).ShouldBe("first part, ");
    }

    [Fact]
    public async Task ReadingPastTheEndOfAnEntryStillSaysEnd()
    {
        using var archive = await Open(WritePlainGzip("again.gz", "text"));
        await using var entry = archive.OpenRead("again");
        await entry.CopyToAsync(Stream.Null);
        (await entry.ReadAsync(new byte[16])).ShouldBe(0);
    }

    [Fact]
    public async Task ReadingTheSameEntryTwiceWorks()
    {
        // Regression guard: the decompressors close what they wrap unless stopped, which would
        // make the second read fail on a stream the first read had quietly disposed.
        using var archive = await Open(WritePlainGzip("twice.gz", "same both times"));

        for (var i = 0; i < 2; i++)
        {
            using var reader = new StreamReader(archive.OpenRead("twice"));
            (await reader.ReadToEndAsync()).ShouldBe("same both times");
        }
    }

    [Fact]
    public async Task ReadsATarballWhoseEntriesAreAllPrefixedWithDotSlash()
    {
        // How "tar -C dir ." writes every entry, and how a great many real tarballs look.
        using var archive = await Open(WriteDotPrefixedTarball("dotted.gz"));

        archive.List(string.Empty).Select(n => n.Name).OrderBy(n => n).ShouldBe(["logs", "readme.txt"]);
        archive.List("logs").Single().Name.ShouldBe("today.log");
    }

    [Theory]
    [InlineData("./readme.txt", "readme.txt")]
    [InlineData("./logs/./today.log", "logs/today.log")]
    [InlineData("logs/", "logs")]
    [InlineData("/leading", "leading")]
    [InlineData(".", "")]
    [InlineData(@"a\b", "a/b")] // a zip may use backslashes
    public void NormalisesEntryPaths(string raw, string expected) =>
        OpenArchive.Normalize(raw).ShouldBe(expected);

    [Fact]
    public async Task AFileThatIsNotAnArchiveIsRefusedClearly()
    {
        var path = At("plain.txt");
        await File.WriteAllTextAsync(path, "hello");

        await Should.ThrowAsync<InvalidDataException>(() => Open(path));
    }

    [Theory]
    [InlineData("backup.tar.gz", "backup.tar")]
    [InlineData("notes.gz", "notes")]
    [InlineData("bundle.tgz", "bundle.tar")]
    [InlineData("data.xz", "data")]
    [InlineData("opaque", "opaque.out")]
    public void NamesTheContentsOfASingleCompressedFile(string container, string expected) =>
        OpenArchive.StripCompressionSuffix(container).ShouldBe(expected);

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("..")]
    public void PathsThatClimbAboveTheRootAreRejected(string path) =>
        OpenArchive.Escapes(path).ShouldBeTrue();

    [Theory]
    [InlineData("a/b.txt")]
    [InlineData("a/../b.txt")] // dips into a/ and back out: still inside
    [InlineData("./a/b.txt")]
    public void OrdinaryPathsAreAccepted(string path) =>
        OpenArchive.Escapes(path).ShouldBeFalse();

    [Fact]
    public async Task AnEntryWithDotDotInsideIsLeftOutRatherThanListedAsAFolderNamedDotDot()
    {
        // "a/../b" stays inside, but its folder "a/.." was listed as "..", which an address
        // resolves to the root: copying the archive then walked root, a, .., root, … forever.
        var target = At("dotdot.zip");
        using (var file = File.Create(target))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("a/../b.txt").Open()))
                writer.Write("tricky");
            using (var writer = new StreamWriter(zip.CreateEntry("ok.txt").Open()))
                writer.Write("fine");
        }

        using var archive = await Open(target);

        archive.List(string.Empty).Select(n => n.Name).ShouldBe(["ok.txt"]);
        archive.RejectedEntries.ShouldBe(1);
    }
}

/// <summary>
/// Through the provider seam, because the point of §4.3 is that an archive is addressed and
/// resolved exactly like any other location.
/// </summary>
public sealed class ArchiveProviderTests : ArchiveFixture
{
    private readonly ArchiveProvider _provider;
    private readonly FsUri _container;

    public ArchiveProviderTests()
    {
        // The registry contains the archive provider and the archive provider needs the registry,
        // so the link is closed after both exist — exactly what the Func indirection is for.
        IProviderRegistry? registry = null;
        var provider = new ArchiveProvider(() => registry!, new ArchiveCache(() => registry!));
        registry = new ProviderRegistry([new LocalFileSystemProvider(), provider]);

        _provider = provider;
        _container = FsUri.Local(WriteMisnamedTarball("logs.gz"));
    }

    private static async Task<List<FsEntry>> Collect(IAsyncEnumerable<FsEntry> entries)
    {
        var list = new List<FsEntry>();
        await foreach (var entry in entries)
            list.Add(entry);
        return list;
    }

    [Fact]
    public async Task ListsTheRootOfAnArchiveByUri()
    {
        var root = FsUri.ArchiveEntry(_container, "/");

        var entries = await Collect(_provider.ListAsync(root, CancellationToken.None));

        entries.Select(e => e.Name).OrderBy(n => n).ShouldBe(["logs", "readme.txt"]);
        entries.Single(e => e.Name == "logs").IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task EntriesAddressTheirOwnChildren()
    {
        var logs = FsUri.ArchiveEntry(_container, "logs");

        var entries = await Collect(_provider.ListAsync(logs, CancellationToken.None));

        entries.Select(e => e.Name).OrderBy(n => n).ShouldBe(["old", "today.log"]);
    }

    [Fact]
    public void LeavingAnArchiveLandsBesideTheFileThatHeldIt()
    {
        var root = FsUri.ArchiveEntry(_container, "/");

        root.Parent.ShouldBe(_container.Parent);
    }

    [Fact]
    public async Task ReportsWhatTheArchiveTurnedOutToBe()
    {
        var format = await _provider.DescribeAsync(
            FsUri.ArchiveEntry(_container, "/"), CancellationToken.None);

        format.Label.ShouldBe("tar.gz"); // despite the ".gz" name
    }

    [Fact]
    public async Task WritingIntoAnArchiveSaysSoRatherThanFailingObscurely()
    {
        var target = FsUri.ArchiveEntry(_container, "new.txt");

        var error = await Should.ThrowAsync<NotSupportedException>(
            () => _provider.OpenWriteAsync(target, null, CancellationToken.None));
        error.Message.ShouldContain("extract it first");
    }

    [Fact]
    public async Task AnEntryBeingReadOutlivesItsArchiveLeavingTheCache()
    {
        // The cache keeps four; opening more disposed the oldest under a stream still reading from
        // it (§4, audit round 3). Now it is retired, and goes when that stream is closed.
        var first = FsUri.Local(WriteZip("first.zip"));
        await using var reading = await _provider.OpenReadAsync(FsUri.ArchiveEntry(first, "notes.md"), CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            var other = FsUri.Local(WriteZip($"other{i}.zip"));
            (await _provider.StatAsync(FsUri.ArchiveEntry(other, "notes.md"), CancellationToken.None)).ShouldNotBeNull();
        }

        using var text = new StreamReader(reading);
        (await text.ReadToEndAsync()).ShouldBe("# notes");
    }
}

/// <summary>
/// The stream wrappers, tested directly because both exist to stop a failure that only shows up
/// on real-world files: fixtures written by .NET's own GZipStream dispose cleanly, while gzip
/// files produced by actual gzip make SharpCompress throw when a partial read is disposed.
/// </summary>
public sealed class ArchiveStreamWrapperTests
{
    private sealed class HostileStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            throw new ArgumentException("Destination is too short.");
        }
    }

    [Fact]
    public void DisposingAStreamThatThrowsIsNotTheCallersProblem()
    {
        var hostile = new HostileStream();

        Should.NotThrow(() => new QuietDisposeStream(hostile).Dispose());
        hostile.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public void AResultIsNotLostBecauseCleanupFailed()
    {
        // The shape of the bug this guards: read a value, then dispose, and keep the value.
        var hostile = new HostileStream();
        hostile.Write("payload"u8);
        hostile.Position = 0;

        string text;
        using (var stream = new QuietDisposeStream(hostile))
        {
            var buffer = new byte[7];
            stream.ReadExactly(buffer);
            text = System.Text.Encoding.UTF8.GetString(buffer);
        }

        text.ShouldBe("payload");
    }

    [Fact]
    public void ANonClosingWrapperLeavesItsInnerStreamUsable()
    {
        using var inner = new MemoryStream([1, 2, 3]);

        new NonClosingStream(inner).Dispose();

        inner.ReadByte().ShouldBe(1); // still open
    }
}

/// <summary>
/// Packing, tested by unpacking: a zip is only correct if this application can read back what it
/// just wrote, and the reader is the one already covered above.
/// </summary>
public sealed class ArchiveBuilderTests : ArchiveFixture
{
    private readonly IProviderRegistry _providers;
    private readonly ArchiveBuilder _builder;

    public ArchiveBuilderTests()
    {
        _providers = new ProviderRegistry([new LocalFileSystemProvider()]);
        _builder = new ArchiveBuilder(() => _providers);
    }

    private FsUri Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return FsUri.Local(path);
    }

    private async Task<OpenArchive> Pack(params FsUri[] sources)
    {
        var target = FsUri.Local(At("packed.zip"));
        await _builder.CreateAsync(sources, target, PackFormat.Zip, progress: null, CancellationToken.None);
        return await OpenArchive.OpenAsync(File.OpenRead(target.ToLocalPath()), "packed.zip", CancellationToken.None);
    }

    [Fact]
    public async Task PacksFilesAndReadsThemBackUnchanged()
    {
        var a = Write("notes.txt", "first");
        var b = Write("readme.md", "second");

        using var archive = await Pack(a, b);

        archive.List(string.Empty).Select(n => n.Name).OrderBy(n => n).ShouldBe(["notes.txt", "readme.md"]);
        using var reader = new StreamReader(archive.OpenRead("notes.txt"));
        (await reader.ReadToEndAsync()).ShouldBe("first");
    }

    [Fact]
    public async Task ASourceThatEndsEarlyIsNotPackedAsAShorterFile()
    {
        // A download cut short reads as an end of file; the zip took it for one, with a CRC of
        // its own short bytes.
        await using var cut = new LazySourceStream(
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[10])), length: 20, CancellationToken.None, name: "cut.bin");
        (await Should.ThrowAsync<IOException>(() => cut.CopyToAsync(Stream.Null))).Message.ShouldContain("cut.bin");

        // One whose size wasn't known is read to whatever end it has.
        await using var unknown = new LazySourceStream(
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[10])), length: 0, CancellationToken.None);
        await unknown.CopyToAsync(Stream.Null);
    }

    [Fact]
    public async Task ASourceWhoseConnectionIsLostIsNamed()
    {
        // A server gone mid-file said only the library's words, with no file in them.
        await using var lost = new LazySourceStream(
            _ => Task.FromResult<Stream>(new LosesConnection()), length: 20, CancellationToken.None, name: "big.bin");
        var error = await Should.ThrowAsync<IOException>(() => lost.CopyToAsync(Stream.Null));
        error.Message.ShouldContain("big.bin");
        error.Message.ShouldContain("connection was closed");
    }

    private sealed class LosesConnection : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new InvalidOperationException("The connection was closed.");
    }

    [Fact]
    public async Task PacksAFolderKeepingItsShape()
    {
        Write("tree/top.txt", "top");
        Write("tree/inner/deep.txt", "deep");

        using var archive = await Pack(FsUri.Local(Path.Combine(Root, "tree")));

        archive.List(string.Empty).Single().Name.ShouldBe("tree");
        archive.List("tree").Select(n => n.Name).OrderBy(n => n).ShouldBe(["inner", "top.txt"]);
        archive.List("tree/inner").Single().Name.ShouldBe("deep.txt");
    }

    [Fact]
    public async Task AnEmptyFolderIsLeftOutRatherThanTurnedIntoAFile()
    {
        Directory.CreateDirectory(Path.Combine(Root, "empty"));
        Write("empty/../kept.txt", "kept");

        using var archive = await Pack(
            FsUri.Local(Path.Combine(Root, "empty")), FsUri.Local(Path.Combine(Root, "kept.txt")));

        // The library's writer cannot record a directory entry -- it strips the trailing separator
        // that marks one -- so an empty folder would come back as a zero-byte file with its name.
        // Losing the folder is recoverable; a file standing where a directory was is not.
        archive.List(string.Empty).Select(n => n.Name).ShouldBe(["kept.txt"]);
    }

    [Fact]
    public async Task WhatWeWriteIsAZipByContentNotJustByName()
    {
        var target = FsUri.Local(At("named-anything.dat"));

        await _builder.CreateAsync([Write("one.txt", "x")], target, PackFormat.Zip, progress: null, CancellationToken.None);

        (await ArchiveSniffer.DetectAsync(target.ToLocalPath())).Kind.ShouldBe(ArchiveKind.Zip);
    }

    [Theory]
    [InlineData(PackFormat.SevenZip, "packed.7z", ArchiveKind.SevenZip)]
    [InlineData(PackFormat.TarGz, "packed.tar.gz", ArchiveKind.GZip)]
    public async Task SevenZipAndTarballsReadBackWithTheirShapeNamesAndTimes(PackFormat format, string name, ArchiveKind kind)
    {
        var when = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);
        var longName = new string('n', 140) + ".txt";
        var top = Write("tree/top.txt", "top");
        Write("tree/inner/päivä.txt", "deep");
        Write($"tree/{longName}", "long");
        File.SetLastWriteTimeUtc(top.ToLocalPath(), when);
        var target = FsUri.Local(At(name));

        await _builder.CreateAsync([FsUri.Local(At("tree"))], target, format, progress: null, CancellationToken.None);

        var detected = await ArchiveSniffer.DetectAsync(target.ToLocalPath());
        detected.Kind.ShouldBe(kind);
        detected.IsTree.ShouldBeTrue();

        using var archive = await OpenArchive.OpenAsync(File.OpenRead(target.ToLocalPath()), name, CancellationToken.None);
        archive.List(string.Empty).Single().Name.ShouldBe("tree");
        archive.List("tree").Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["inner", longName, "top.txt"]);
        archive.List("tree/inner").Single().Name.ShouldBe("päivä.txt");
        archive.List("tree").Single(n => n.Name == "top.txt").Modified!.Value.UtcDateTime.ShouldBe(when);
        using var reader = new StreamReader(archive.OpenRead("tree/inner/päivä.txt"));
        (await reader.ReadToEndAsync()).ShouldBe("deep");
    }

    [Fact]
    public async Task AFileThatGrowsWhileTarredIsNotPackedCutShort()
    {
        // A tar header gives the size first and the writer copies exactly that much, so the rest
        // of a file that grew was left out with nothing to say so.
        var growing = Write("growing.log", "start");
        var stale = new GrowsWhenOpened(growing);
        var registry = new ProviderRegistry([stale]);
        var builder = new ArchiveBuilder(() => registry);
        var target = FsUri.Local(At("grown.tar.gz"));

        var error = await Should.ThrowAsync<IOException>(() =>
            builder.CreateAsync([growing], target, PackFormat.TarGz, progress: null, CancellationToken.None));

        error.Message.ShouldContain("growing.log grew");
        File.Exists(target.ToLocalPath()).ShouldBeFalse();
    }

    /// <summary>Local files, except that one grows the moment it is opened for packing.</summary>
    private sealed class GrowsWhenOpened(FsUri growing) : IFileSystemProvider
    {
        private readonly LocalFileSystemProvider _local = new();

        public string Scheme => _local.Scheme;

        public ProviderCapabilities Capabilities => _local.Capabilities;

        public DanceCommander.Core.Columns.ColumnSet DefaultColumns => _local.DefaultColumns;

        public DanceCommander.Core.Watching.IDirectoryWatcher? Watch(FsUri directory) => null;

        public Task<FsEntry?> StatAsync(FsUri uri, CancellationToken ct) => _local.StatAsync(uri, ct);

        public IAsyncEnumerable<FsEntry> ListAsync(FsUri uri, CancellationToken ct) => _local.ListAsync(uri, ct);

        public Task<Stream> OpenReadAsync(FsUri uri, CancellationToken ct)
        {
            if (uri == growing)
                File.AppendAllText(uri.ToLocalPath(), " and more");
            return _local.OpenReadAsync(uri, ct);
        }

        public Task<Stream> OpenWriteAsync(FsUri uri, long? length, CancellationToken ct) => _local.OpenWriteAsync(uri, length, ct);

        public Task CreateDirectoryAsync(FsUri uri, CancellationToken ct) => _local.CreateDirectoryAsync(uri, ct);

        public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => _local.MoveAsync(from, to, ct);

        public Task DeleteAsync(FsUri uri, bool recursive, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(PackFormat.Zip, "same.zip")]
    [InlineData(PackFormat.SevenZip, "same.7z")]
    [InlineData(PackFormat.TarGz, "same.tar.gz")]
    public async Task TwoFilesOfOneNameAreNotPackedOneOverTheOther(PackFormat format, string name)
    {
        // Picked in a flat view: the tar and 7z writers kept both, and the archive showed one
        // (review, 2026-11-08).
        var a = Write("a/readme.txt", "four");
        var b = Write("b/README.txt", "six!!!");
        var target = FsUri.Local(At(name));

        var error = await Should.ThrowAsync<IOException>(() =>
            _builder.CreateAsync([a, b], target, format, progress: null, CancellationToken.None));

        error.Message.ShouldContain("readme.txt");
        File.Exists(target.ToLocalPath()).ShouldBeFalse();
    }

    [Theory]
    [InlineData("logs.tar.gz", PackFormat.TarGz)]
    [InlineData("logs.TGZ", PackFormat.TarGz)]
    [InlineData("logs.7z", PackFormat.SevenZip)]
    [InlineData("v1.2.zip", PackFormat.Zip)]
    [InlineData("v1.2", null)]
    [InlineData(".zip", null)]
    public void TheExtensionPicksTheFormat(string name, PackFormat? format) =>
        PackFormats.FromName(name).ShouldBe(format);

    [Fact]
    public async Task ProgressNamesEachFileAndEndsWhole()
    {
        var target = FsUri.Local(At("counted.zip"));
        var reports = new List<ArchiveProgress>();

        await _builder.CreateAsync(
            [Write("a.txt", new string('a', 300_000)), Write("b.txt", "b")], target, PackFormat.Zip,
            new Recording(reports), CancellationToken.None);

        reports.Select(r => r.Detail).ShouldContain("a.txt");
        reports.Select(r => r.Detail).ShouldContain("b.txt");
        reports.Last().Fraction.ShouldBe(1);
        reports.Last().Detail.ShouldBe("Writing counted.zip");
    }

    [Fact]
    public async Task WhatWasPackedIsSaidFileByFile()
    {
        var a = Write("folder/a.txt", "aaa");
        var b = Write("folder/deep/b.txt", "b");

        var packed = await _builder.CreateAsync(
            [FsUri.Local(At("folder"))], FsUri.Local(At("said.zip")), PackFormat.Zip, progress: null, CancellationToken.None);

        packed.Select(p => (p.Source, p.Size)).ShouldBe([(a, 3L), (b, 1L)], ignoreOrder: true);
        (await PackedFile.FirstChangedAsync(_providers, packed, CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("rewritten")]
    public async Task APackedFileNoLongerAsItWasIsFound(string change)
    {
        // What a compress's undo asks before it deletes a zip that no bin will keep: the zip may
        // now hold the only copy (review, 2026-10-22).
        var a = Write("folder/a.txt", "aaa");
        var packed = await _builder.CreateAsync(
            [FsUri.Local(At("folder"))], FsUri.Local(At("only.zip")), PackFormat.Zip, progress: null, CancellationToken.None);

        if (change == "deleted")
            File.Delete(a.ToLocalPath());
        else
            File.WriteAllText(a.ToLocalPath(), "changed");

        (await PackedFile.FirstChangedAsync(_providers, packed, CancellationToken.None))!.Source.ShouldBe(a);
    }

    [Fact]
    public async Task StoppedPartWayLeavesNoZip()
    {
        var target = FsUri.Local(At("stopped.zip"));
        using var cts = new CancellationTokenSource();

        // Stopped once the writer is inside the first file, the way Cancel in the strip lands.
        var progress = new Recording([], r =>
        {
            if (r.Detail == "big.txt")
                cts.Cancel();
        });

        await Should.ThrowAsync<OperationCanceledException>(() => _builder.CreateAsync(
            [Write("big.txt", new string('x', 2_000_000))], target, PackFormat.Zip, progress, cts.Token));

        File.Exists(target.ToLocalPath()).ShouldBeFalse();
    }

    private sealed class Recording(List<ArchiveProgress> into, Action<ArchiveProgress>? also = null) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value)
        {
            into.Add(value);
            also?.Invoke(value);
        }
    }
}
