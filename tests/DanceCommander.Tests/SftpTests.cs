using System.Security.Cryptography;
using System.Text;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.Sftp;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class SftpTargetTests
{
    [Theory]
    [InlineData("claude@192.168.56.102", "192.168.56.102", 22, "claude")]
    [InlineData("claude@host:2222", "host", 2222, "claude")]
    [InlineData("root@[2001:db8::1]:2222", "2001:db8::1", 2222, "root")]
    [InlineData("root@[2001:db8::1]", "2001:db8::1", 22, "root")]
    public void ParsesAnAuthority(string authority, string host, int port, string user)
    {
        var target = SftpTarget.Parse(authority);

        target.Host.ShouldBe(host);
        target.Port.ShouldBe(port);
        target.User.ShouldBe(user);
    }

    [Fact]
    public void AnAddressWithoutAUserMeansTheCurrentOne() =>
        SftpTarget.Parse("build-server").User.ShouldBe(Environment.UserName);

    [Theory]
    [InlineData("claude@host:not-a-port")]
    [InlineData("claude@host:0")]
    [InlineData("claude@host:70000")]
    [InlineData("")]
    [InlineData("claude@")]
    public void RefusesAnAddressItCannotUse(string authority) =>
        Should.Throw<FormatException>(() => SftpTarget.Parse(authority));

    [Fact]
    public void ReadsTheTargetStraightOffAUri()
    {
        var uri = FsUri.Parse("sftp://claude@192.168.56.102/var/log");

        var target = SftpTarget.From(uri);

        target.Host.ShouldBe("192.168.56.102");
        target.User.ShouldBe("claude");
        uri.Path.ShouldBe("/var/log");
    }

    [Fact]
    public void TheDefaultPortStaysImplicitButAnotherOneDoesNot()
    {
        new SftpTarget("host", 22, "me").Authority.ShouldBe("me@host");
        new SftpTarget("host", 2222, "me").Authority.ShouldBe("me@host:2222");
    }

    [Fact]
    public void UsesOpenSshsOwnHostNamingSoTheFileIsReadableElsewhere()
    {
        new SftpTarget("host", 22, "me").KnownHostsName.ShouldBe("host");
        new SftpTarget("host", 2222, "me").KnownHostsName.ShouldBe("[host]:2222");
    }

    [Fact]
    public void TheSameHostAsTwoUsersIsTwoTargets() =>
        new SftpTarget("host", 22, "alice").ShouldNotBe(new SftpTarget("host", 22, "bob"));

    [Fact]
    public void ACredentialKeyNamesTheAccountAndNothingSecret() =>
        new SftpTarget("host", 22, "claude").CredentialKey.ShouldBe("sftp:claude@host");
}

/// <summary>
/// Host key handling, tested hardest of anything in the provider: a mistake here is the
/// difference between an encrypted connection and an authenticated one.
/// </summary>
public sealed class KnownHostsStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-ssh-").FullName;
    private readonly KnownHostsStore _store;

    private static readonly SftpTarget Target = new("192.168.56.102", 22, "claude");
    private static readonly byte[] KeyA = Encoding.ASCII.GetBytes("ssh-ed25519 key material A");
    private static readonly byte[] KeyB = Encoding.ASCII.GetBytes("ssh-ed25519 key material B");

    public KnownHostsStoreTests() => _store = new KnownHostsStore(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string FileText => File.ReadAllText(Path.Combine(_root, "known_hosts"));

    [Fact]
    public void AHostWeHaveNeverSeenIsUnknown() =>
        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Unknown);

    [Fact]
    public void AHostIsTrustedOnceItsKeyIsRecorded()
    {
        _store.Trust(Target, "ssh-ed25519", KeyA);

        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Trusted);
    }

    [Fact]
    public void ADifferentKeyForAKnownHostIsAChangeNotAnUnknown()
    {
        _store.Trust(Target, "ssh-ed25519", KeyA);

        // The distinction is the whole point: unknown asks politely, changed refuses.
        _store.Check(Target, "ssh-ed25519", KeyB).ShouldBe(HostKeyVerdict.Changed);
    }

    [Fact]
    public void AcceptingAChangedKeyForOneHostOfASharedLineKeepsTheOthersKey()
    {
        // OpenSSH writes "name,address type key"; dropped whole, the other name lost its key.
        var other = new SftpTarget("build-server", 22, "claude");
        var shared = $"{other.KnownHostsName},{Target.KnownHostsName} ssh-ed25519 {Convert.ToBase64String(KeyA)} a comment";
        File.WriteAllText(Path.Combine(_root, "known_hosts"), "# mine\n" + shared + "\n");

        _store.Trust(Target, "ssh-ed25519", KeyB);

        _store.Check(other, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Trusted);
        _store.Check(Target, "ssh-ed25519", KeyB).ShouldBe(HostKeyVerdict.Trusted);
        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Changed);
        FileText.ShouldContain($"{other.KnownHostsName} ssh-ed25519 {Convert.ToBase64String(KeyA)} a comment");
        FileText.ShouldContain("# mine");
    }

    [Fact]
    public void AcceptingAChangedKeyReplacesTheOldOneRatherThanAddingToIt()
    {
        _store.Trust(Target, "ssh-ed25519", KeyA);
        _store.Trust(Target, "ssh-ed25519", KeyB);

        _store.Check(Target, "ssh-ed25519", KeyB).ShouldBe(HostKeyVerdict.Trusted);
        // The superseded key must not still be accepted.
        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Changed);
        FileText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);
    }

    [Fact]
    public void AKeyOfAnotherTypeFromAKnownHostIsAChangeNotAFirstConnection()
    {
        // The server side picks the key type, so anyone in between can pick one we have no
        // record of; "unknown" there was the polite question instead of the refusal.
        _store.Trust(Target, "ssh-ed25519", KeyA);

        _store.Check(Target, "ssh-rsa", KeyB).ShouldBe(HostKeyVerdict.Changed);
    }

    [Fact]
    public void AnUnreadableFileIsNoVerdictRatherThanUnknown()
    {
        // Read as no entries, a changed key got the first-use question instead of the refusal.
        _store.Trust(Target, "ssh-ed25519", KeyA);
        using var held = new FileStream(Path.Combine(_root, "known_hosts"), FileMode.Open, FileAccess.Read, FileShare.None);

        Should.Throw<IOException>(() => _store.Check(Target, "ssh-ed25519", KeyB));
    }

    [Fact]
    public void TheSameKeyUnderAnotherAlgorithmNameIsTrusted()
    {
        // One RSA key negotiates as ssh-rsa, rsa-sha2-256 or rsa-sha2-512.
        _store.Trust(Target, "rsa-sha2-512", KeyA);

        _store.Check(Target, "rsa-sha2-256", KeyA).ShouldBe(HostKeyVerdict.Trusted);
    }

    [Fact]
    public void AcceptingAKeyOfAnotherTypeReplacesTheHostsOldKey()
    {
        _store.Trust(Target, "ssh-ed25519", KeyA);
        _store.Trust(Target, "ssh-rsa", KeyB);

        _store.Check(Target, "ssh-rsa", KeyB).ShouldBe(HostKeyVerdict.Trusted);
        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Changed);
        FileText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);
    }

    [Fact]
    public void TrustingOneHostSaysNothingAboutAnother()
    {
        _store.Trust(Target, "ssh-ed25519", KeyA);

        _store.Check(Target with { Host = "elsewhere" }, "ssh-ed25519", KeyA)
            .ShouldBe(HostKeyVerdict.Unknown);
    }

    [Fact]
    public void TheSameHostOnAnotherPortIsAnotherHost()
    {
        _store.Trust(Target, "ssh-ed25519", KeyA);

        _store.Check(Target with { Port = 2222 }, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Unknown);
    }

    [Fact]
    public void TrustingTheSameHostAsAnotherUserNeedsNoSecondApproval()
    {
        // A host key belongs to the host, not to the account used on it.
        _store.Trust(Target, "ssh-ed25519", KeyA);

        _store.Check(Target with { User = "someone-else" }, "ssh-ed25519", KeyA)
            .ShouldBe(HostKeyVerdict.Trusted);
    }

    [Fact]
    public void WritesTheFormatOpenSshItselfUses()
    {
        _store.Trust(Target, "ssh-ed25519", KeyA);

        var parts = FileText.Trim().Split(' ');
        parts.Length.ShouldBe(3);
        parts[0].ShouldBe("192.168.56.102");
        parts[1].ShouldBe("ssh-ed25519");
        Convert.FromBase64String(parts[2]).ShouldBe(KeyA);
    }

    [Fact]
    public void TrustingAKeyKeepsEveryOtherLineAsItWas()
    {
        // Comments, hashed hosts and markers used to be dropped, and a marker line cut to three fields.
        var path = Path.Combine(_root, "known_hosts");
        string[] theirs =
        [
            "# added by hand",
            "|1|c2FsdA==|aGFzaA== ssh-ed25519 AAAA",
            "@cert-authority *.example.com ssh-rsa AAAAB3",
            $"other.host ssh-ed25519 {Convert.ToBase64String(KeyB)}",
        ];
        File.WriteAllLines(path, theirs);

        _store.Trust(Target, "ssh-ed25519", KeyA);

        var lines = File.ReadAllLines(path);
        lines[..4].ShouldBe(theirs);
        lines.Length.ShouldBe(5);
        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["known_hosts"]);
    }

    [Fact]
    public void AKnownHostsThatCannotBeReadIsNotRewritten()
    {
        // Rewriting from what could be read meant a locked file lost every other host.
        var path = Path.Combine(_root, "known_hosts");
        var line = $"other.host ssh-ed25519 {Convert.ToBase64String(KeyB)}";
        File.WriteAllLines(path, [line]);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Should.Throw<IOException>(() => _store.Trust(Target, "ssh-ed25519", KeyA));

        File.ReadAllLines(path).ShouldBe([line]);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var path = Path.Combine(_root, "known_hosts");
        File.WriteAllLines(path,
        [
            "# added by hand",
            "",
            $"192.168.56.102 ssh-ed25519 {Convert.ToBase64String(KeyA)}",
        ]);

        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Trusted);
    }

    [Fact]
    public void AHashedEntryIsSkippedRatherThanMisread()
    {
        // OpenSSH may hash hostnames. We cannot match those, and must not mistake one for a
        // mismatch either -- that would report a key change that did not happen.
        var path = Path.Combine(_root, "known_hosts");
        File.WriteAllLines(path, [$"|1|abc=|def= ssh-ed25519 {Convert.ToBase64String(KeyB)}"]);

        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Unknown);
    }

    [Fact]
    public void AnEntryListingSeveralHostNamesMatchesAnyOfThem()
    {
        var path = Path.Combine(_root, "known_hosts");
        File.WriteAllLines(path, [$"build,192.168.56.102 ssh-ed25519 {Convert.ToBase64String(KeyA)}"]);

        _store.Check(Target, "ssh-ed25519", KeyA).ShouldBe(HostKeyVerdict.Trusted);
    }

    [Fact]
    public void FingerprintsAreTheOnesSshPrints()
    {
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(KeyA)).TrimEnd('=');

        var fingerprint = KnownHostsStore.Fingerprint(KeyA);

        fingerprint.ShouldBe(expected);
        fingerprint.ShouldNotEndWith("="); // ssh prints them unpadded; ours must compare by eye
    }
}

/// <summary>
/// Library exceptions become sentences. Worth its own test because the translation is easy to
/// leave unreachable: it runs on a lazily-enumerated listing, where the error arrives long after
/// the call that appears to produce it.
/// </summary>
public sealed class SftpErrorTranslationTests
{
    private static readonly FsUri Where = FsUri.Parse("sftp://claude@192.168.56.102/var/log");

    [Fact]
    public void AMissingPathReadsLikeAMissingPath()
    {
        var translated = SftpFileSystemProvider.Translate(
            new Renci.SshNet.Common.SftpPathNotFoundException("No such file."), Where);

        translated.ShouldBeOfType<FileNotFoundException>();
        translated.Message.ShouldBe("sftp://claude@192.168.56.102/var/log is not there.");
    }

    [Fact]
    public void ARefusalNamesWhatWasRefused()
    {
        var translated = SftpFileSystemProvider.Translate(
            new Renci.SshNet.Common.SftpPermissionDeniedException("denied"), Where);

        translated.ShouldBeOfType<UnauthorizedAccessException>();
        translated.Message.ShouldContain("/var/log");
    }

    [Fact]
    public void ADroppedConnectionSaysSoInPlainWords()
    {
        var translated = SftpFileSystemProvider.Translate(
            new Renci.SshNet.Common.SshConnectionException("reset"), Where);

        translated.ShouldBeOfType<IOException>();
        translated.Message.ShouldBe("The connection to 192.168.56.102 was lost.");
    }

    [Fact]
    public void ASocketAbortedUnderARead_IsALostConnectionToo()
    {
        var aborted = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionAborted);

        SftpFileSystemProvider.Translate(aborted, Where).Message.ShouldBe("The connection to 192.168.56.102 was lost.");
    }

    [Fact]
    public void OurOwnSentenceAboutALostConnectionIsKept()
    {
        // A replace that couldn't put the old file back says where it is; "lost" alone hid that.
        var aborted = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionAborted);
        var aside = new IOException("build.tar could not be replaced, and the old one is now /x/build.tar.dc-old-1.", aborted);

        SftpFileSystemProvider.Translate(aside, Where).ShouldBeSameAs(aside);
    }

    [Fact]
    public void AnExceptionWeHaveNothingBetterToSayAboutIsLeftAlone()
    {
        var original = new InvalidOperationException("something else entirely");

        SftpFileSystemProvider.Translate(original, Where).ShouldBeSameAs(original);
    }

    [Fact]
    public void TheOriginalIsKeptAsTheInnerExceptionSoTheLogStillHasIt()
    {
        var original = new Renci.SshNet.Common.SftpPathNotFoundException("No such file.");

        SftpFileSystemProvider.Translate(original, Where).InnerException.ShouldBeSameAs(original);
    }

    private static readonly FsUri Upload = FsUri.Parse("sftp://claude@192.168.56.102/home/claude/build.tar");

    private static readonly Renci.SshNet.Common.SftpException Failure = new(Renci.SshNet.Sftp.StatusCode.Failure, "Failure");

    [Fact]
    public void AWriteToAReadOnlyFilesystemSaysSo()
    {
        var explained = SftpFileSystemProvider.WhyNotWritten(Upload, readOnly: true, available: 1UL << 30, 100, Failure);

        explained.Message.ShouldBe("192.168.56.102 has /home/claude on a read-only filesystem.");
        explained.InnerException.ShouldBeSameAs(Failure);
    }

    [Fact]
    public void AWriteBiggerThanTheFreeSpaceSaysHowShortItFell()
    {
        var explained = SftpFileSystemProvider.WhyNotWritten(
            Upload, readOnly: false, available: 512 * 1024, 3 * 1024 * 1024, Failure);

        explained.Message.ShouldBe($"192.168.56.102 is out of space: {0.5:0.#} MB free, 3 MB needed.");
    }

    [Fact]
    public void AFullDiskIsOutOfSpaceEvenWhenTheSizeIsNotKnown() =>
        SftpFileSystemProvider.WhyNotWritten(Upload, readOnly: false, available: 0, length: null, Failure)
            .Message.ShouldBe("192.168.56.102 is out of space.");

    [Theory]
    [InlineData(false, 1UL << 30)]
    [InlineData(false, null)]
    public void OtherwiseItAtLeastNamesTheFile(bool readOnly, ulong? available) =>
        SftpFileSystemProvider.WhyNotWritten(Upload, readOnly, available, 100, Failure)
            .Message.ShouldBe("The server could not write sftp://claude@192.168.56.102/home/claude/build.tar.");
}

/// <summary>
/// Quoting for the remote shell. The directory comes from a listing, not from the user, but it
/// still goes into a command line — and spaces and quotes are ordinary in Unix paths.
/// </summary>
public sealed class RemoteCommandQuotingTests
{
    [Theory]
    [InlineData("/home/claude", "'/home/claude'")]
    [InlineData("/srv/my logs", "'/srv/my logs'")]
    [InlineData("/tmp/$HOME", "'/tmp/$HOME'")]          // no expansion of the directory
    [InlineData("/tmp/a;rm -rf b", "'/tmp/a;rm -rf b'")] // a semicolon stays part of the name
    [InlineData("/tmp/it's", "'/tmp/it'\\''s'")]        // close, escaped quote, reopen
    [InlineData("/tmp/a\\", "'/tmp/a'\\\\''")]            // fish read '/tmp/a\' as still open
    public void QuotesADirectoryForAPosixShell(string path, string expected) =>
        SftpCommandService.Quote(path).ShouldBe(expected);

    [Theory]
    [InlineData("/tmp/a\\")]
    [InlineData("/tmp/it's\\'x")]
    [InlineData("/tmp/a\\\\b c")]
    public void ADirectoryReachesBashAsItIs(string path)
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash))
            return;
        // Handed over as CommandShells does, so Git Bash's runtime reads the line back exactly.
        var info = new System.Diagnostics.ProcessStartInfo(
            bash, "-c " + DanceCommander.Extensibility.ProcessArguments.ForMsys("printf '%s' " + SftpCommandService.Quote(path)))
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        output.ShouldBe(path);
    }

    [Fact]
    public void OnlyClaimsLocationsItCanActuallyRunOn()
    {
        var service = new SftpCommandService(null!, new DanceCommander.Core.Operations.ActivityLog());

        service.CanRun(FsUri.Parse("sftp://claude@host/var")).ShouldBeTrue();
        service.CanRun(FsUri.Local(@"C:\work")).ShouldBeFalse();
        service.CanRun(FsUri.Parse("dav://host/share")).ShouldBeFalse();
    }

    [Fact]
    public void AnExitStatusOfZeroIsSuccessAndAnythingElseIsNot()
    {
        new RemoteResult("ok", "", 0).Failed.ShouldBeFalse();
        new RemoteResult("", "no such file", 1).Failed.ShouldBeTrue();
        new RemoteResult("", "", null).Failed.ShouldBeTrue(); // killed by a signal
    }

    [Fact]
    public void BothStreamsAreShownWhenBothSaidSomething()
    {
        // A trailing newline on stdout should not become a blank line before stderr.
        new RemoteResult("out\n", "err", 1).Combined.ShouldBe("out\nerr");
        new RemoteResult("out", "", 0).Combined.ShouldBe("out");
        new RemoteResult("", "err", 1).Combined.ShouldBe("err");
    }
}

public sealed class PasswdTests
{
    [Fact]
    public void ReadsNamesByUserId()
    {
        var names = SftpConnectionPool.ParsePasswd(
            "root:x:0:0:root:/root:/bin/bash\n" +
            "claude:x:1001:1001:Claude,,,:/home/claude:/bin/bash\r\n" +
            "\n" +
            "# a comment line has no id\n" +
            "broken:x:notanumber:0::/:/bin/false\n" +
            "shadow:x:1001:1001::/:/bin/false\n");

        names.ShouldBe(new Dictionary<int, string> { [0] = "root", [1001] = "claude" }, ignoreOrder: true);
    }
}
