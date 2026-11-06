using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The script provider against a real script (§6.16). A .cmd stands in for a GitLab one: two
/// levels of rows, a read, a secret, and a level that fails in its own words.
/// </summary>
public sealed class ScriptProviderTests : IDisposable
{
    private const string Script = """
        @echo off
        if "%~1"=="list" goto list
        if "%~1"=="read" goto read
        echo unknown verb 1>&2
        exit /b 2
        :list
        if "%~2"=="" (
          echo d;Pipeline 12345;;12345
          echo f;Readme;5;readme
          exit /b 0
        )
        if "%~2"=="12345" (
          echo d;build job;;Build
          echo d;build other;;build
          echo f;bad row;1;has space
          exit /b 0
        )
        if "%~2"=="12345/Build" (
          echo f;out.txt;6;out.txt
          exit /b 0
        )
        echo pipeline %~2 was deleted 1>&2
        exit /b 3
        :read
        if "%~2"=="12345/Build/out.txt" (
          echo %DC_SECRET_API_TOKEN%
          exit /b 0
        )
        echo cannot read %~2 1>&2
        exit /b 4
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("dc-script-").FullName;
    private readonly CommandListingCatalog _catalog = new();
    private readonly ScriptProvider _provider;

    public ScriptProviderTests()
    {
        var script = Path.Combine(_root, "gitlab.cmd");
        File.WriteAllText(script, Script.Replace("\n", "\r\n"));

        _catalog.Register(new CommandListing(
            "gitlab", "Pipelines", script, [],
            @"^(?<kind>[df]);(?<name>[^;]+);(?<size>\d*);(?<crumb>[^;\r\n]*)\r?$",
            IsScript: true,
            Secrets: ["api-token"]));

        _provider = new ScriptProvider(_catalog, new FixedSecret("s3cret"));
    }

    public void Dispose()
    {
        _provider.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedSecret(string value) : IScriptSecrets
    {
        public Task<string?> GetAsync(string name, string listingTitle, CancellationToken ct) =>
            Task.FromResult<string?>(name == "api-token" ? value : null);
    }

    private FsUri At(string crumbs = "") =>
        FsUri.Parse($"from:{FsUri.Local(_root)}?gitlab{(crumbs.Length == 0 ? "" : "/" + crumbs)}");

    private async Task<List<FsEntry>> List(FsUri uri)
    {
        var rows = new List<FsEntry>();
        await foreach (var row in _provider.ListAsync(uri, CancellationToken.None))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task TheRootListsWhatTheScriptPrintsWithCrumbsAsAddresses()
    {
        var rows = await List(At());

        rows.Select(r => r.Name).ShouldBe(["Pipeline 12345", "Readme"]);
        var pipeline = rows[0];
        pipeline.IsDirectory.ShouldBeTrue();
        // The display name is the script's; the address carries its crumb.
        pipeline.Uri.ShouldBe(At("12345"));
        rows[1].Size.ShouldBe(5);
    }

    [Fact]
    public async Task CrumbsDifferingOnlyInCaseAreTwoRowsAndAnUnsafeOneSaysSo()
    {
        var rows = await List(At("12345"));

        rows.Count(r => r.IsDirectory).ShouldBe(2);
        rows.Single(r => r.Name == "build job").Uri.ShouldBe(At("12345/Build"));
        rows.Single(r => r.Name == "build other").Uri.ShouldBe(At("12345/build"));

        // Shown, with the reason, rather than dropped or rewritten into something else.
        rows.Single(r => r.Name == "bad row").Extra![ColumnIds.Note].ShouldContain("percent-encoding");
    }

    [Fact]
    public async Task AReadGetsTheBytesAndTheSecretArrivesInTheEnvironment()
    {
        await using var stream = await _provider.OpenReadAsync(At("12345/Build/out.txt"), CancellationToken.None);

        stream.CanSeek.ShouldBeTrue(); // so a zip served this way opens as an archive
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Trim().ShouldBe("s3cret");
    }

    [Fact]
    public async Task ASecondReadSoonAfterIsServedFromTheFirst()
    {
        // Recognising an archive and then opening it are two reads; they must not be two downloads.
        var file = At("12345/Build/out.txt");
        var counting = new CountingSecret("s3cret");
        using var provider = new ScriptProvider(_catalog, counting);

        await using (await provider.OpenReadAsync(file, CancellationToken.None)) { }
        await using var again = await provider.OpenReadAsync(file, CancellationToken.None);

        counting.Asked.ShouldBe(1); // the script ran once
        using var reader = new StreamReader(again);
        (await reader.ReadToEndAsync()).Trim().ShouldBe("s3cret");
    }

    private sealed class CountingSecret(string value) : IScriptSecrets
    {
        public int Asked { get; private set; }

        public Task<string?> GetAsync(string name, string listingTitle, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult<string?>(value);
        }
    }

    [Fact]
    public async Task StatAnswersFromTheParentsRows()
    {
        var entry = await _provider.StatAsync(At("12345/Build/out.txt"), CancellationToken.None);

        entry.ShouldNotBeNull();
        entry.Name.ShouldBe("out.txt");
        entry.Size.ShouldBe(6);
        (await _provider.StatAsync(At(), CancellationToken.None))!.IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task AFailingLevelReportsTheScriptsOwnWords()
    {
        var failure = await Should.ThrowAsync<IOException>(() => List(At("999")));

        failure.Message.ShouldContain("pipeline 999 was deleted");
    }

    [Fact]
    public async Task AMissingSecretStopsBeforeTheScriptRuns()
    {
        // No way to get a secret: nothing stored and nobody to ask.
        var provider = new ScriptProvider(_catalog);

        var failure = await Should.ThrowAsync<IOException>(() => provider.OpenReadAsync(At("12345/Build/out.txt"), CancellationToken.None));

        failure.Message.ShouldContain("api-token");
    }

    [Fact]
    public void AFromAddressForAScriptListingBecomesAScriptLocation()
    {
        var factory = new ListingSourceFactory(new ProviderRegistry([_provider]), _catalog);

        var source = factory.Create(At("12345"), new ListingFilter());

        source.ShouldBeOfType<ScriptListingSource>();
        source.Title.ShouldBe("12345");
        factory.Create(At(), new ListingFilter()).Title.ShouldBe("Pipelines");
    }

    [Fact]
    public void AListingCanStartNewestFirst()
    {
        var listing = _catalog.Find("gitlab")! with { Sort = "modified" };

        var columns = ScriptProvider.ColumnsFor(listing);

        columns.DefaultSortColumnId.ShouldBe(ColumnIds.Modified);
        columns.DefaultDescending.ShouldBeTrue();
        ScriptProvider.ColumnsFor(listing with { Sort = null }).DefaultSortColumnId.ShouldBe(ColumnIds.Name);
    }

    [Fact]
    public void TheDefaultRowFormatReadsTabSeparatedLines()
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            ScriptProvider.DefaultExtract, System.Text.RegularExpressions.RegexOptions.Multiline);

        var dir = pattern.Match("d\tbuild job\t\t2026-09-24T10:00:00Z\tBuild");
        dir.Groups["kind"].Value.ShouldBe("d");
        dir.Groups["name"].Value.ShouldBe("build job");
        dir.Groups["crumb"].Value.ShouldBe("Build");

        // A bare name is a file named that, and its leading "d" is not a kind.
        var bare = pattern.Match("data.txt");
        bare.Groups["kind"].Success.ShouldBeFalse();
        bare.Groups["name"].Value.ShouldBe("data.txt");
    }
}

public class AboutProviderTests
{
    [Fact]
    public async Task TheComputerPageListsTheSystemDriveAndTheKnownFolders()
    {
        var rows = new List<FsEntry>();
        await foreach (var row in new DanceCommander.Providers.Local.AboutProvider().ListAsync(FsUri.Computer, CancellationToken.None))
            rows.Add(row);

        var system = rows.Single(r => r.Name == Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'));
        system.Uri.ShouldBe(FsUri.Local(Path.GetPathRoot(Environment.SystemDirectory)!));
        system.Extra![DanceCommander.Providers.Local.AboutProvider.TypeColumn].ShouldStartWith("Drive");
        system.Size.ShouldBeGreaterThan(0);

        rows.ShouldContain(r => r.Name == "Home" && r.Uri == FsUri.Local(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }
}
