using System.Text;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Core.Watching;
using DanceCommander.Providers.Local;
using DanceCommander.Providers.Sftp;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class ExecutableContentTests
{
    private static byte[] Elf(byte type, bool bigEndian = false)
    {
        var head = new byte[64];
        head[0] = 0x7F;
        head[1] = (byte)'E';
        head[2] = (byte)'L';
        head[3] = (byte)'F';
        head[5] = bigEndian ? (byte)2 : (byte)1;
        head[bigEndian ? 17 : 16] = type;
        return head;
    }

    [Theory]
    [InlineData(2, false, ExecutableKind.Binary)]   // an executable
    [InlineData(3, false, ExecutableKind.Binary)]   // a PIE executable or a shared library
    [InlineData(2, true, ExecutableKind.Binary)]    // big-endian, as on some routers
    [InlineData(1, false, ExecutableKind.None)]     // an object file
    [InlineData(4, false, ExecutableKind.None)]     // a core dump
    public void ReadsTheElfType(byte type, bool bigEndian, ExecutableKind expected) =>
        ExecutableContent.Detect(Elf(type, bigEndian)).ShouldBe(expected);

    [Theory]
    [InlineData("#!/bin/sh\necho hi\n", ExecutableKind.Script)]
    [InlineData("#!/usr/bin/env python3\r\nprint(1)\r\n", ExecutableKind.ScriptWithWindowsLineEndings)]
    [InlineData("#!/bin/sh", ExecutableKind.Script)]
    [InlineData("echo hi\n", ExecutableKind.None)]
    [InlineData("# a comment, not a shebang\n", ExecutableKind.None)]
    [InlineData("", ExecutableKind.None)]
    public void RecognisesScripts(string text, ExecutableKind expected) =>
        ExecutableContent.Detect(Encoding.ASCII.GetBytes(text)).ShouldBe(expected);

    [Fact]
    public void TooShortToBeAnElfHeaderIsNothing() =>
        ExecutableContent.Detect([0x7F, (byte)'E', (byte)'L', (byte)'F']).ShouldBe(ExecutableKind.None);

    [Theory]
    [InlineData("644", "755")]
    [InlineData("640", "750")]
    [InlineData("600", "700")]
    [InlineData("755", "755")]
    public void AddsExecuteWhereReadIsAllowed(string mode, string expected) =>
        ExecutableContent.WithExecute(Octal(mode)).ShouldBe(Octal(expected));

    [Theory]
    [InlineData("777", "644", "755")]  // the umask still applies
    [InlineData("750", "644", "750")]
    [InlineData("700", "600", "700")]
    [InlineData("755", "600", "700")]  // a private default keeps it private
    [InlineData("644", "644", "644")]
    public void InheritsASourceModeWithinTheServersDefault(string source, string fresh, string expected) =>
        ExecutableContent.Inherit(Octal(source), Octal(fresh)).ShouldBe(Octal(expected));

    [Theory]
    [InlineData("755", 755)]
    [InlineData("640", 640)]
    [InlineData("7", 7)]
    public void SshNetWantsTheDigitsInDecimal(string mode, short expected) =>
        SftpFileSystemProvider.Digits(Octal(mode)).ShouldBe(expected);

    internal static int Octal(string digits) => Convert.ToInt32(digits, 8);
}

/// <summary>
/// The local provider with Unix modes kept beside it: a stand-in for a server. Anything under
/// <c>windows</c> has no mode, which is what a source on a Windows disk looks like.
/// </summary>
internal sealed class ModalProvider(LocalFileSystemProvider inner, string windowsFolder) : IFileSystemProvider, IUnixModes
{
    /// <summary>What a new file on this "server" gets: 0644, as with umask 022.</summary>
    public static readonly int Fresh = ExecutableContentTests.Octal("644");

    public Dictionary<FsUri, int> Modes { get; } = [];

    public List<(FsUri File, int Mode)> Set { get; } = [];

    public Task<int?> GetModeAsync(FsUri file, CancellationToken ct) =>
        Task.FromResult<int?>(file.Path.Contains(windowsFolder) ? null : Modes.GetValueOrDefault(file, Fresh));

    public Task SetModeAsync(FsUri file, int mode, CancellationToken ct)
    {
        Modes[file] = mode;
        Set.Add((file, mode));
        return Task.CompletedTask;
    }

    public string Scheme => inner.Scheme;

    public ProviderCapabilities Capabilities => inner.Capabilities;

    public ColumnSet DefaultColumns => inner.DefaultColumns;

    public IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, CancellationToken ct) => inner.ListAsync(directory, ct);

    public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) => inner.StatAsync(path, ct);

    public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) => inner.OpenReadAsync(file, ct);

    public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => inner.OpenWriteAsync(file, length, ct);

    public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => inner.CreateDirectoryAsync(directory, ct);

    public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) => inner.DeleteAsync(path, recursive, ct);

    public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => inner.MoveAsync(from, to, ct);

    public IDirectoryWatcher? Watch(FsUri directory) => inner.Watch(directory);
}

public sealed class ExecutableModeTransferTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-modes-").FullName;
    private readonly ModalProvider _provider;
    private readonly ActivityLog _activity = new();
    private readonly TransferService _transfers;

    public ExecutableModeTransferTests()
    {
        _provider = new ModalProvider(new LocalFileSystemProvider(), "windows");
        _transfers = new TransferService(
            new ProviderRegistry([_provider]), new UndoStack(), _activity, new NoRecycleBin(), new JobService(),
            NullLogger<TransferService>.Instance);
        Directory.CreateDirectory(Path.Combine(_root, "server"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FsUri Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return FsUri.Local(path);
    }

    private FsUri Server => FsUri.Local(Path.Combine(_root, "server"));

    private async Task<ActivityEntry> CopyAsync(FsUri source, string? answer = null)
    {
        var job = _transfers.Enqueue([source], Server, TransferMode.Copy, new ScriptedDecisions(answer));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        job.State.ShouldBe(JobState.Completed);
        return _activity.Entries[0];
    }

    [Fact]
    public async Task AScriptFromWindowsBecomesRunnable()
    {
        var entry = await CopyAsync(Write("windows/deploy.sh", "#!/bin/sh\necho deployed\n"));

        _provider.Modes[Server.Child("deploy.sh")].ShouldBe(ExecutableContentTests.Octal("755"));
        entry.ShellEquivalent.ShouldContain("chmod 755");
    }

    [Fact]
    public async Task AnythingElseKeepsTheServersDefault()
    {
        var entry = await CopyAsync(Write("windows/notes.txt", "just text\n"));

        _provider.Set.ShouldBeEmpty();
        entry.ShellEquivalent.ShouldNotContain("chmod");
    }

    [Fact]
    public async Task ASourceWithAModeHandsItOn()
    {
        var source = Write("elsewhere/tool", "no header needed: the mode is known");
        _provider.Modes[source] = ExecutableContentTests.Octal("750");

        await CopyAsync(source);

        _provider.Modes[Server.Child("tool")].ShouldBe(ExecutableContentTests.Octal("750"));
    }

    [Fact]
    public async Task AKnownModeWinsOverTheContent()
    {
        var source = Write("elsewhere/lib.sh", "#!/bin/sh\n# sourced, not run\n");
        _provider.Modes[source] = ExecutableContentTests.Octal("644");

        await CopyAsync(source);

        _provider.Set.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReplacedFileIsLeftToTheProvider()
    {
        Write("server/deploy.sh", "#!/bin/sh\necho old\n");
        _provider.Modes[Server.Child("deploy.sh")] = ExecutableContentTests.Octal("700");

        await CopyAsync(Write("windows/deploy.sh", "#!/bin/sh\necho new\n"), answer: "overwrite");

        _provider.Set.ShouldBeEmpty();
        _provider.Modes[Server.Child("deploy.sh")].ShouldBe(ExecutableContentTests.Octal("700"));
    }

    [Fact]
    public async Task WindowsLineEndingsInAScriptAreMentioned()
    {
        var entry = await CopyAsync(Write("windows/run.sh", "#!/bin/sh\r\necho hi\r\n"));

        entry.Detail.ShouldNotBeNull();
        entry.Detail.ShouldContain("run.sh has Windows line endings");
    }
}
