using DanceCommander.Core.Config;
using Microsoft.Extensions.Logging;

namespace DanceCommander.Extensibility;

/// <summary>Where each found pack's program is, as last seen: <c>packs.json</c>, local to this machine.</summary>
public sealed class PacksFoundFile
{
    public Dictionary<string, string> Found { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <param name="Arrived">
/// Packs found now that were not found before. Empty on the first look of all, when everything
/// found is the starting point rather than news.
/// </param>
public sealed record PacksChangedEventArgs(IReadOnlyList<ToolPack> Arrived);

/// <summary>
/// Which packs' programs are on this computer (SETTINGS_PLAN.md §4). It only looks, and never runs
/// what it finds. Never on the startup path: the last answer is kept in packs.json, so the tools are
/// there from the first frame, and the look itself runs on a worker after the panes have listed.
/// </summary>
public sealed class PackDetector
{
    private readonly ProgramLocator _locator;
    private readonly IConfigStore<PacksFoundFile> _cache;
    private readonly ILogger<PackDetector> _log;
    private readonly SemaphoreSlim _looking = new(1, 1);
    private readonly bool _lookedBefore;
    private IReadOnlyDictionary<string, string> _found;

    public PackDetector(ProgramLocator locator, IConfigStore<PacksFoundFile> cache, ILogger<PackDetector> log)
    {
        _locator = locator;
        _cache = cache;
        _log = log;
        _lookedBefore = cache is not IConfigFile file || file.FileExists;
        _found = new Dictionary<string, string>(cache.Current.Found, StringComparer.OrdinalIgnoreCase);
        Settings = ToolPacks.All.Select(pack => pack.Setting(() => Status(pack))).ToList();
    }

    /// <summary>Raised when what is found changes, from a worker thread.</summary>
    public event EventHandler<PacksChangedEventArgs>? Changed;

    /// <summary>Each found pack's program, by pack id.</summary>
    public IReadOnlyDictionary<string, string> Found => Volatile.Read(ref _found);

    /// <summary>Each pack's switch, for the settings window and the palette.</summary>
    public IReadOnlyList<SettingDefinition> Settings { get; }

    public string? Status(ToolPack pack) =>
        Found.TryGetValue(pack.Id, out var program) ? $"Found: {program}" : "Not found on this computer.";

    /// <summary>Looks again. A look already under way is waited for rather than doubled.</summary>
    public async Task DetectAsync()
    {
        await _looking.WaitAsync().ConfigureAwait(false);
        try
        {
            var found = await Task.Run(Look).ConfigureAwait(false);
            var before = Found;
            if (found.Count == before.Count && found.All(f => before.TryGetValue(f.Key, out var p) && p == f.Value))
                return;

            Volatile.Write(ref _found, found);
            var arrived = _lookedBefore || before.Count > 0
                ? ToolPacks.All.Where(p => found.ContainsKey(p.Id) && !before.ContainsKey(p.Id)).ToList()
                : [];

            try
            {
                await _cache.SaveAsync(new PacksFoundFile { Found = new(found, StringComparer.OrdinalIgnoreCase) })
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a cache: the next launch looks again, a moment later than it could have.
                _log.LogWarning(ex, "packs.json could not be written");
            }

            _log.LogInformation("Tool packs found: {Found}", string.Join(", ", found.Keys));
            Changed?.Invoke(this, new PacksChangedEventArgs(arrived));
        }
        finally
        {
            _looking.Release();
        }
    }

    private Dictionary<string, string> Look()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in ToolPacks.All)
        {
            try
            {
                if (pack.Locate(_locator) is { } program)
                    found[pack.Id] = program;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log.LogWarning(ex, "Looking for {Pack} failed", pack.Title);
            }
        }

        return found;
    }
}
