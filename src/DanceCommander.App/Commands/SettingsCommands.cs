using System.IO;
using System.Text.Json;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;

namespace DanceCommander.App.Commands;

/// <summary>
/// The settings window's key, and every setting's choices as palette entries (SETTINGS_PLAN.md §3):
/// "Rows: Compact" is quicker typed than found, and a setting changed often never needs the window.
/// </summary>
public static class SettingsCommands
{
    /// <param name="more">Settings from beyond the catalog: the tool packs' switches.</param>
    public static void RegisterAll(
        ICommandRegistry registry, IConfigStore<SettingsFile> settings, SettingsWriter writer, Action openWindow,
        IReadOnlyList<SettingDefinition>? more = null)
    {
        registry.Register(new CommandDefinition
        {
            Id = "dc.settings",
            Title = "Settings",
            Group = "Settings",
            Subject = CommandSubject.Window,
            InLegend = false,
            Binding = KeyStroke.Parse("Ctrl+,"),
            ExecuteAsync = (_, _) =>
            {
                openWindow();
                return Task.CompletedTask;
            },
        });

        foreach (var setting in SettingsCatalog.All.Concat(more ?? []))
        {
            foreach (var choice in setting.Choices)
            {
                registry.Register(new CommandDefinition
                {
                    Id = $"dc.setting.{setting.Id}.{choice.Value}",
                    Title = $"{setting.Title}: {choice.Label}",
                    Group = "Settings",
                    Subject = CommandSubject.Window,
                    InLegend = false,
                    // The choice in force is not on offer: choosing it would do nothing.
                    CanExecute = _ => setting.Read(settings.Current) != choice.Value,
                    ExecuteAsync = (_, _) => SetAsync(writer, setting, choice.Value),
                });
            }
        }
    }

    /// <summary>
    /// A change written through, or a failure said in words: the parser's own message is for the
    /// log, and a file with a mistake in it is the user's to fix, not ours to write over.
    /// </summary>
    public static async Task SetAsync(SettingsWriter writer, SettingDefinition setting, string value)
    {
        try
        {
            await writer.SetAsync(setting, value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "settings.json has a mistake in it, so nothing was written. Fix it and try again.", ex);
        }
        catch (IOException ex)
        {
            // Most likely the file appeared, or was held, while the change was being made. The
            // edit is refused rather than written over it.
            throw new InvalidOperationException(
                $"settings.json could not be written ({ex.Message}). Nothing in it was changed; try again.", ex);
        }
    }
}
