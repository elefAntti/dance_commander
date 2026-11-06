using System.Text.Json;
using System.Text.Json.Nodes;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>settings.json: edited as text so comments survive, sparse, and keys chosen over commands' own.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-settings-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static SettingsFile Read(string json) =>
        JsonSerializer.Deserialize<SettingsFile>(json, JsonConfigStore<SettingsFile>.SerializerOptions)!;

    [Fact]
    public void ReplacingAValueKeepsCommentsAndEverythingElse()
    {
        var json = Lf("""
            {
              // mine, keep it
              "appearance": {
                "scheme": "dark", // the old one
                "density": "compact",
              },
            }
            """);

        var edited = JsoncEdit.Set(json, ["appearance", "scheme"], JsonValue.Create("light"));

        edited.ShouldBe(json.Replace("\"dark\"", "\"light\"", StringComparison.Ordinal));
    }

    [Fact]
    public void AddingToAnExistingObjectIndentsLikeItsNeighbours()
    {
        var json = Lf("""
            {
              "appearance": {
                "scheme": "dark"
              }
            }
            """);

        var edited = JsoncEdit.Set(json, ["appearance", "density"], JsonValue.Create("compact"));

        edited.ShouldBe(Lf("""
            {
              "appearance": {
                "scheme": "dark",
                "density": "compact"
              }
            }
            """));
    }

    [Fact]
    public void AddingAfterATrailingCommaKeepsTheStyle()
    {
        var json = "{\n  \"a\": 1,\n}\n";

        JsoncEdit.Set(json, ["b"], JsonValue.Create(2)).ShouldBe("{\n  \"a\": 1,\n  \"b\": 2,\n}\n");
    }

    [Fact]
    public void MissingObjectsOnThePathAreCreated()
    {
        var edited = JsoncEdit.Set("{\n  // nothing yet\n}\n", ["keys", "dc.search"], JsonValue.Create("Ctrl+Shift+S"));

        edited.ShouldBe(Lf("""
            {
              // nothing yet
              "keys": {
                "dc.search": "Ctrl+Shift+S"
              }
            }

            """));
        Read(edited).Keys["dc.search"].ShouldBe("Ctrl+Shift+S");
    }

    [Fact]
    public void AnEmptyFileBecomesAnObject()
    {
        var edited = JsoncEdit.Set(string.Empty, ["appearance", "density"], JsonValue.Create("compact"));

        Read(edited).Appearance.Density.ShouldBe("compact");
    }

    [Fact]
    public void KeepsTheFilesOwnLineEndings()
    {
        var edited = JsoncEdit.Set("{\r\n  \"a\": 1\r\n}\r\n", ["b", "c"], JsonValue.Create(true));

        edited.ShouldBe("{\r\n  \"a\": 1,\r\n  \"b\": {\r\n    \"c\": true\r\n  }\r\n}\r\n");
    }

    [Fact]
    public void SomethingInTheWayThatIsNotAnObjectIsReplacedNotDuplicated()
    {
        var edited = JsoncEdit.Set("{\n  \"keys\": null\n}\n", ["keys", "dc.search"], null);

        edited.ShouldBe("{\n  \"keys\": {\n    \"dc.search\": null\n  }\n}\n");
    }

    [Fact]
    public void NamesMatchWithoutCaseAsTheLoaderReadsThem()
    {
        var edited = JsoncEdit.Set("{ \"Appearance\": { \"Scheme\": \"dark\" } }", ["appearance", "scheme"], JsonValue.Create("light"));

        edited.ShouldBe("{ \"Appearance\": { \"Scheme\": \"light\" } }");
    }

    [Fact]
    public void RemovingTakesItsLinesAndComma()
    {
        var json = Lf("""
            {
              "appearance": {
                "scheme": "dark", // going
                "density": "compact"
              }
            }
            """);

        JsoncEdit.Remove(json, ["appearance", "density"]).ShouldBe(Lf("""
            {
              "appearance": {
                "scheme": "dark" // going
              }
            }
            """));
    }

    [Fact]
    public void RemovingTheFirstKeepsTheRest()
    {
        var json = "{\n  \"a\": 1,\n  \"b\": 2\n}\n";

        JsoncEdit.Remove(json, ["a"]).ShouldBe("{\n  \"b\": 2\n}\n");
    }

    [Fact]
    public void RemovingTheLastSettingOfASectionTakesTheSectionToo()
    {
        var json = Lf("""
            {
              // mine
              "view": {
                "directoriesFirst": true
              },
              "appearance": {
                "scheme": "dark"
              }
            }
            """);

        JsoncEdit.Remove(json, ["view", "directoriesFirst"]).ShouldBe(Lf("""
            {
              // mine
              "appearance": {
                "scheme": "dark"
              }
            }
            """));
    }

    [Fact]
    public void RemovingWhatIsNotThereChangesNothing()
    {
        var json = "{ \"a\": 1 } // end\n";

        JsoncEdit.Remove(json, ["b"]).ShouldBeSameAs(json);
        JsoncEdit.Remove(json, ["a", "deeper"]).ShouldBeSameAs(json);
        JsoncEdit.Remove(string.Empty, ["a"]).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("{ \"a\": 1 ")]
    [InlineData("{ \"a\": 1, \"b\" }")]
    [InlineData("[1, 2]")]
    public void RefusesAFileThatDoesNotParse(string json)
    {
        Should.Throw<JsonException>(() => JsoncEdit.Set(json, ["a"], JsonValue.Create(2)));
        Should.Throw<JsonException>(() => JsoncEdit.Remove(json, ["a"]));
    }

    // From the independent review, 2026-10-13.

    [Theory]
    [InlineData("{ \"a\": 1 /* c */, \"b\": 2 }")]
    [InlineData("{\n  \"a\": 1 // c\n  , \"b\": 2\n}")]
    public void ACommaAfterACommentIsStillTheComma(string json)
    {
        var removed = JsoncEdit.Remove(json, ["a"]);

        var parsed = JsonNode.Parse(removed, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        parsed["a"].ShouldBeNull();
        parsed["b"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void AddingAfterACommentedCommaAddsNoSecondComma()
    {
        var edited = JsoncEdit.Set("{\n  \"a\": 1 /* c */,\n}\n", ["b"], JsonValue.Create(2));

        edited.ShouldBe("{\n  \"a\": 1 /* c */,\n  \"b\": 2,\n}\n");
    }

    [Fact]
    public void ASectionHoldingACommentStaysWhenItsLastSettingGoes()
    {
        var json = Lf("""
            {
              "appearance": {
                // dark hurts my eyes at night
                "scheme": "light"
              }
            }
            """);

        JsoncEdit.Remove(json, ["appearance", "scheme"]).ShouldBe(Lf("""
            {
              "appearance": {
                // dark hurts my eyes at night
              }
            }
            """));
    }

    [Fact]
    public void SomethingInTheWayThatIsTheUsersIsRefusedNotReplaced() =>
        Should.Throw<JsonException>(() =>
            JsoncEdit.Set("{ \"keys\": [ \"mine\" ] }", ["keys", "dc.search"], JsonValue.Create("F9")));

    [Fact]
    public void ResetTakesOutEveryDuplicateSoTheDefaultIsInForce()
    {
        var json = "{\n  \"density\": \"compact\",\n  \"Density\": \"compact\"\n}\n";

        JsoncEdit.Remove(json, ["density"]).ShouldBe("{\n}\n");
    }

    [Fact]
    public async Task AFileNotInUtf8IsNotEditedAndKeepsItsCharacters()
    {
        var path = Path.Combine(_root, "settings.json");
        byte[] ansi = [.. "{ // "u8, 0xE4, .. "\n}"u8];
        await File.WriteAllBytesAsync(path, ansi);
        using var store = new JsonConfigStore<SettingsFile>(_root, "settings.json", NullLogger.Instance);

        await Should.ThrowAsync<JsonException>(() => new SettingsWriter(store).SetAsync(SettingsCatalog.Density, "compact"));

        (await File.ReadAllBytesAsync(path)).ShouldBe(ansi);
    }

    [Fact]
    public async Task AByteOrderMarkIsKept()
    {
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. "{\n}\n"u8]);
        using var store = new JsonConfigStore<SettingsFile>(_root, "settings.json", NullLogger.Instance);

        await new SettingsWriter(store).SetAsync(SettingsCatalog.Density, "compact");

        var bytes = await File.ReadAllBytesAsync(path);
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        Read(System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)).Appearance.Density.ShouldBe("compact");
    }

    [Fact]
    public async Task AnEditMadeToAVersionSavedOverMeanwhileIsNotWritten()
    {
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "{\n}\n");
        using var store = new JsonConfigStore<SettingsFile>(_root, "settings.json", NullLogger.Instance);

        // The user saving from an editor while the edit is being made.
        await Should.ThrowAsync<IOException>(() => store.EditTextAsync(text =>
        {
            File.WriteAllText(path, "{ // mine\n}\n");
            return JsoncEdit.Set(text, ["a"], JsonValue.Create(1));
        }));

        (await File.ReadAllTextAsync(path)).ShouldBe("{ // mine\n}\n");
    }

    [Fact]
    public async Task EditsMadeAtOnceAreAllKept()
    {
        using var store = new JsonConfigStore<SettingsFile>(_root, "settings.json", NullLogger.Instance);
        var writer = new SettingsWriter(store);

        await Task.WhenAll(
            writer.SetAsync(SettingsCatalog.Density, "compact"),
            writer.SetAsync(SettingsCatalog.TextSize, "1.25"),
            writer.SetAsync(SettingsCatalog.Scheme, "light"));

        var file = Read(await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
        SettingsCatalog.Density.Read(file).ShouldBe("compact");
        SettingsCatalog.TextSize.Read(file).ShouldBe("1.25");
        SettingsCatalog.Scheme.Read(file).ShouldBe("light");
    }

    [Fact]
    public async Task ChoosingTheDefaultTakesTheSettingOutSoTheFileStaysSparse()
    {
        using var store = new JsonConfigStore<SettingsFile>(_root, "settings.json", NullLogger.Instance);
        var writer = new SettingsWriter(store);
        var path = Path.Combine(_root, "settings.json");

        // Nothing to write for a default, and no file made for it.
        await writer.SetAsync(SettingsCatalog.Density, "comfortable");
        File.Exists(path).ShouldBeFalse();

        await writer.SetAsync(SettingsCatalog.Density, "compact");
        await writer.SetAsync(SettingsCatalog.TextSize, "1.25");
        await writer.SetAsync(SettingsCatalog.DirectoriesFirst, "true");
        var file = Read(await File.ReadAllTextAsync(path));
        SettingsCatalog.Density.Read(file).ShouldBe("compact");
        SettingsCatalog.TextSize.Read(file).ShouldBe("1.25");
        SettingsCatalog.DirectoriesFirst.Read(file).ShouldBe("true");

        await writer.SetAsync(SettingsCatalog.Density, "comfortable");
        await writer.SetAsync(SettingsCatalog.TextSize, "1");
        await writer.SetAsync(SettingsCatalog.DirectoriesFirst, "false");
        Lf(await File.ReadAllTextAsync(path)).ShouldBe("{\n}\n");
    }

    [Fact]
    public async Task AFileThatDoesNotParseIsNotWrittenOver()
    {
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "{ \"appearance\": { \"scheme\": \"dark\" ");
        using var store = new JsonConfigStore<SettingsFile>(_root, "settings.json", NullLogger.Instance);

        await Should.ThrowAsync<JsonException>(() => new SettingsWriter(store).SetAsync(SettingsCatalog.Density, "compact"));

        (await File.ReadAllTextAsync(path)).ShouldBe("{ \"appearance\": { \"scheme\": \"dark\" ");
    }

    [Fact]
    public void EveryDefaultIsOneOfItsChoicesAndWhatAnEmptyFileReads()
    {
        var empty = new SettingsFile();
        foreach (var setting in SettingsCatalog.All)
        {
            setting.Choices.ShouldContain(c => c.Value == setting.Default, setting.Id);
            setting.Read(empty).ShouldBe(setting.Default, setting.Id);
        }
    }

    [Fact]
    public void EveryPathIsWhereTheLoaderReadsIt()
    {
        foreach (var setting in SettingsCatalog.All)
        {
            var other = setting.Choices.First(c => c.Value != setting.Default).Value;
            var json = JsoncEdit.Set(string.Empty, setting.Path, setting.ToJson(other));
            setting.Read(Read(json)).ShouldBe(other, setting.Id);
        }
    }

    [Theory]
    [InlineData("""{ }""", "dc.openInNewTab")]
    [InlineData("""{ "mouse": { "middleClick": "otherPane" } }""", "dc.openInOtherPane")]
    [InlineData("""{ "mouse": { "middleClick": " OTHERPANE " } }""", "dc.openInOtherPane")]
    [InlineData("""{ "mouse": { "middleClick": "close" } }""", "dc.openInNewTab")]
    public void MiddleClickReadsAnyCaseAndFallsBackToANewTab(string json, string command)
    {
        SettingsCatalog.MiddleClickCommand(Read(json)).ShouldBe(command);
    }

    [Fact]
    public void AMiddleClickValueOfYourOwnReadsBackAsWritten()
    {
        // As every setting's does, so the window can say the file holds a value of its own.
        SettingsCatalog.MiddleClick.Read(Read("""{ "mouse": { "middleClick": "close" } }""")).ShouldBe("close");
    }

    // Keys chosen in settings.json.

    private static CommandDefinition Command(string id, string? binding = null) =>
        new()
        {
            Id = id,
            Title = id,
            Binding = binding is null ? null : KeyStroke.Parse(binding),
            ExecuteAsync = (_, _) => Task.CompletedTask,
        };

    [Theory]
    [InlineData("ctrl+shift+s", "Ctrl+Shift+S")]
    [InlineData("Alt + left", "Alt+Left")]
    [InlineData("esc", "Escape")]
    [InlineData("shift+pgdn", "Shift+PageDown")]
    [InlineData("f5", "F5")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    public void TypedKeysAreSpelledAsTheKeyboardReportsThem(string typed, string canonical)
    {
        KeyStroke.TryParseTyped(typed, out var stroke).ShouldBeTrue();
        stroke.ToString().ShouldBe(canonical);
    }

    [Theory]
    [InlineData("Ctrl+Hyper+S")]
    [InlineData("Ctrl+NumLock")]
    [InlineData("Ctrl+")]
    [InlineData("")]
    public void KeysNoKeyboardProducesAreRefused(string typed) =>
        KeyStroke.TryParseTyped(typed, out _).ShouldBeFalse();

    [Fact]
    public void AChosenKeyReplacesTheCommandsOwn()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.search", "Ctrl+S"));

        registry.ApplyKeys(new Dictionary<string, string?> { ["dc.search"] = "ctrl+shift+s" }).ShouldBeEmpty();

        registry.ForKey(KeyStroke.Parse("Ctrl+Shift+S"))!.Id.ShouldBe("dc.search");
        registry.ForKey(KeyStroke.Parse("Ctrl+S")).ShouldBeNull();
        var search = registry.Find("dc.search")!;
        search.Binding.ShouldBe(KeyStroke.Parse("Ctrl+Shift+S"));
        search.DefaultBinding.ShouldBe(KeyStroke.Parse("Ctrl+S"));
    }

    [Fact]
    public void NullTakesTheKeyAway()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.search", "Ctrl+S"));

        registry.ApplyKeys(new Dictionary<string, string?> { ["dc.search"] = null });

        registry.ForKey(KeyStroke.Parse("Ctrl+S")).ShouldBeNull();
        registry.Find("dc.search")!.Binding.ShouldBeNull();
    }

    [Fact]
    public void AChosenKeyWinsAndTheCommandThatHadItIsLeftWithoutOne()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.search", "Ctrl+S"));
        registry.Register(Command("dc.copyPath", "Ctrl+Shift+C"));

        registry.ApplyKeys(new Dictionary<string, string?> { ["dc.copyPath"] = "Ctrl+S" });

        registry.ForKey(KeyStroke.Parse("Ctrl+S"))!.Id.ShouldBe("dc.copyPath");
        registry.Find("dc.search")!.Binding.ShouldBeNull();
        registry.Taken.ShouldHaveSingleItem().ShouldBe((KeyStroke.Parse("Ctrl+S"), "dc.search", "dc.copyPath"));
    }

    [Fact]
    public void ACommandRegisteredLaterDoesNotTakeAChosenKeyBack()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.copyPath"));
        registry.ApplyKeys(new Dictionary<string, string?> { ["dc.copyPath"] = "F9" });

        // A tool reloading from tools.json with F9 of its own.
        registry.Register(Command("tool.build", "F9"));

        registry.ForKey(KeyStroke.Parse("F9"))!.Id.ShouldBe("dc.copyPath");
        registry.Find("tool.build")!.Binding.ShouldBeNull();
    }

    [Fact]
    public void AToolThatReloadsGetsItsChosenKeyAgain()
    {
        var registry = new CommandRegistry();
        registry.ApplyKeys(new Dictionary<string, string?> { ["tool.build"] = "F9" });
        registry.Register(Command("tool.build", "Ctrl+B"));
        registry.Remove("tool.build");
        registry.Register(Command("tool.build", "Ctrl+B"));

        registry.ForKey(KeyStroke.Parse("F9"))!.Id.ShouldBe("tool.build");
        registry.ForKey(KeyStroke.Parse("Ctrl+B")).ShouldBeNull();
    }

    [Fact]
    public void TakingTheChoiceBackRestoresEveryonesOwnKeys()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.search", "Ctrl+S"));
        registry.Register(Command("dc.copyPath", "Ctrl+Shift+C"));
        registry.ApplyKeys(new Dictionary<string, string?> { ["dc.copyPath"] = "Ctrl+S" });

        registry.ApplyKeys(new Dictionary<string, string?>());

        registry.ForKey(KeyStroke.Parse("Ctrl+S"))!.Id.ShouldBe("dc.search");
        registry.ForKey(KeyStroke.Parse("Ctrl+Shift+C"))!.Id.ShouldBe("dc.copyPath");
        registry.Taken.ShouldBeEmpty();
    }

    [Fact]
    public void AKeyThatCannotBeReadIsSaidByCommand()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.search", "Ctrl+S"));

        // An id with no command yet is not a problem: settings are applied before the commands
        // register, and a tool that is off gets its key when it is turned on.
        var problems = registry.ApplyKeys(new Dictionary<string, string?>
        {
            ["dc.search"] = "Ctrl+Hyper+S",
            ["tool.notYet"] = "F9",
        });

        problems.Keys.ShouldBe(["dc.search"]);
        // A key that cannot be read leaves the command its own rather than none.
        registry.ForKey(KeyStroke.Parse("Ctrl+S"))!.Id.ShouldBe("dc.search");
    }
}
