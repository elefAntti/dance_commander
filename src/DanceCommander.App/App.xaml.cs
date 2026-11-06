using System.IO;
using System.Windows;
using System.Windows.Interop;
using DanceCommander.App.Services;
using DanceCommander.App.ViewModels;
using DanceCommander.App.Views;
using DanceCommander.Core.Clipboard;
using DanceCommander.Core.Collections;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Decorations;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Startup;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Extensibility;
using DanceCommander.Providers.Archive;
using DanceCommander.Core.Security;
using DanceCommander.Providers.Local;
using DanceCommander.Providers.Sftp;
using DanceCommander.Providers.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DanceCommander.App;

public partial class App : Application
{
    private readonly LaunchRequest _request;
    private readonly SingleInstance _instance;
    private readonly LaunchPlaceholder _placeholder;
    private IHost? _host;

    internal App(LaunchRequest request, SingleInstance instance, LaunchPlaceholder placeholder)
    {
        _request = request;
        _instance = instance;
        _placeholder = placeholder;
    }

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DanceCommander");

    // --help and the hand-off to a running copy were settled in Program.Main, before WPF loaded.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DanceCommander", "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDirectory, "dance-commander-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled exception on the UI thread");
            // Carried on, and said: decided 2026-10-30 for the release. A file operation whose own
            // code threw has stopped already, its records written in its finally blocks; one
            // running on a worker is better left to finish than ended with the process. Ended
            // still before there is a window, and in a storm (ErrorReports).
            if (ErrorReports.Record(args.Exception, "on the UI thread"))
            {
                args.Handled = true;
                ErrorReports.Tell?.Invoke(ErrorReports.NoticeFor(args.Exception));
                return;
            }
            NoteCrash(args.Exception);
            args.Handled = false;
        };
        // A task nobody awaited that failed: kept for the report, and nothing said, as nothing
        // waited on it. .NET no longer ends the process for these.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "A task failed with nobody waiting on it");
            ErrorReports.Record(args.Exception, "in a background task");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
            // Once per crash: a UI-thread one reaches here too, after the handler above.
            if (!_crashNoted)
                NoteCrash(args.ExceptionObject as Exception);
            Log.CloseAndFlush();
        };
        DanceCommander.Core.Config.Debouncer.ExitFailure = ex => Log.Error(ex, "A save at exit failed");

        // A bare host, not Host.CreateDefaultBuilder(): the defaults add appsettings.json, user
        // secrets, environment and command-line configuration, and a file watcher on the working
        // directory for reloading them — a measurable part of every launch, for an application
        // that reads no configuration through any of them.
        _host = new HostBuilder()
            .UseSerilog()
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync();
        StartupClock.Mark("services");

        // The palette before anything is built from it: dark, light, or Windows' own choice.
        _host.Services.GetRequiredService<ColourScheme>().Apply();
        // Sizes and keys likewise, before the window measures anything or the legend is drawn.
        _host.Services.GetRequiredService<AppliedSettings>().Apply();

        // Tools enter the same registry as the built-ins, after those are registered.
        // The providers ask their questions in a pane showing the server, else in whichever pane
        // the user is looking at.
        var shell = _host.Services.GetRequiredService<ShellViewModel>();
        StartupClock.Mark("shell");
        _host.Services.GetRequiredService<SftpPrompts>().UseHost(shell.TabShowing);
        _host.Services.GetRequiredService<ScriptSecrets>().UseHost(() => shell.ActiveTab);
        _host.Services.GetRequiredService<DavPrompts>().UseHost(shell.TabShowing);

        // The first listings start now, not when the window has loaded: reading the folders, their
        // icons and their git status needs nothing from the window, so it runs on worker threads
        // while the UI thread spends the next half second building it. The window awaits this.
        _host.Services.GetRequiredService<ToolCatalog>().Reload();
        var initialized = shell.InitializeAsync(_request);

        var window = _host.Services.GetRequiredService<MainWindow>();
        StartupClock.Mark("window built");

        // A tool from tools.json taking a built-in's key is a feature. Two built-ins colliding is
        // a bug that is otherwise completely silent, so both end up in the log and whoever is
        // looking at it can tell which one this was.
        foreach (var (stroke, lost, won) in _host.Services.GetRequiredService<ICommandRegistry>().Displaced)
            Log.Information("{Stroke} moved from {Lost} to {Won}", stroke, lost, won);
        window.Launch(_request, initialized);

        // Opened exactly over the placeholder, which goes once there is something drawn on top of
        // it: the window appears not to move, only to fill in.
        if (_placeholder.PlaceOver(new WindowInteropHelper(window).Handle) is true)
            window.WindowState = WindowState.Maximized;
        //
        // Not on ContentRendered alone: WPF posts that at Input priority, below the Normal-priority
        // continuations of the first listings and their decorations, so on a busy start it came
        // seconds after the window was drawn and the finished window sat hidden under the
        // placeholder. The second rendering tick after Show means the first frame is presented.
        // ContentRendered stays as the fallback, for a start where nothing asks for a second frame.
        var frames = 0;
        var dismissed = false;
        void Dismiss()
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
            if (dismissed)
                return;
            dismissed = true;
            StartupClock.Mark("window drawn");
            _placeholder.Dismiss(new WindowInteropHelper(window).Handle);
        }
        void OnRendering(object? sender, EventArgs e)
        {
            if (++frames >= 2)
                Dismiss();
        }
        // Marked even when the frame count came first: the gap between the two is what the
        // placeholder used to linger for.
        void OnContentRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnContentRendered;
            StartupClock.Mark("content rendered");
            Dismiss();
        }

        window.ContentRendered += OnContentRendered;
        // One dispatcher turn first, so that rows listed while the window was built go into the grid
        // before its first layout, instead of into a second layout and frame after Show. Never a
        // wait for a listing: one still running simply arrives after, as before. Measured A/B, four
        // launches each on a restored session: rows on screen ~2500 -> ~2170 ms (DESIGN.md §1.5).
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        window.Show();
        StartupClock.WindowShown();
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;

        if (_instance.IsFirst)
            _instance.Listen(args => Dispatcher.InvokeAsync(() => window.Reopen(CommandLine.Parse(args))));

        // From here an error can be said in a pane, and carried past (ErrorReports).
        ErrorReports.Tell = notice => shell.ActiveTab.Report(notice);
        // With the window gone, no pane would say it, and the next launch wouldn't either: an
        // error then ends the application as before, and is told about next time (review, 2026-10-30).
        window.Closed += (_, _) => ErrorReports.Tell = null;
        _ = TellAboutLastCrashAsync(shell, initialized, logDirectory);
    }

    /// <summary>
    /// Beside the logs: a line per crash, written as the process goes down, read and removed by the
    /// next launch. Only a crash writes it; a kill (Task Manager, the publish script) does not, so
    /// the next launch has nothing to say about it.
    /// </summary>
    private static string CrashMarker => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanceCommander", "last-crash.txt");

    private static bool _crashNoted;

    private static void NoteCrash(Exception? ex)
    {
        _crashNoted = true;
        try
        {
            // One line, appended: two crashes before the next launch each keep theirs.
            var what = ex is null ? "an unknown error" : $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" ");
            File.AppendAllText(CrashMarker, $"{DateTimeOffset.Now:O}\t{what}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Going down already; the log has it.
        }
    }

    /// <summary>
    /// After a crash, the next launch says so, once and quietly: the window used to just disappear,
    /// and the next one looked as if nothing had happened. The log is one choice away, opened in a
    /// pane at the file of that day.
    /// </summary>
    private static async Task TellAboutLastCrashAsync(ShellViewModel shell, Task initialized, string logDirectory)
    {
        try
        {
            await TellAsync(shell, initialized, logDirectory);
        }
        catch (Exception ex)
        {
            // News about the last session must not be the next thing to go wrong in this one.
            Log.Warning(ex, "Could not tell about the last crash");
        }
    }

    private static async Task TellAsync(ShellViewModel shell, Task initialized, string logDirectory)
    {
        string[] lines;
        try
        {
            if (!File.Exists(CrashMarker))
                return;
            lines = await File.ReadAllLinesAsync(CrashMarker);
#pragma warning disable RS0030 // §1.6: our own crash marker beside the logs, read just above; its news is told next.
            File.Delete(CrashMarker);
#pragma warning restore RS0030
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read the crash marker");
            return;
        }

        var last = lines.LastOrDefault(l => l.Contains('\t'));
        if (last is null)
            return;

        await initialized;

        // Not over a question already showing (a restored tab's password prompt, say): asking
        // replaces it, and whoever was waiting on it takes that as Cancel.
        while (shell.ActiveTab.Decision is not null)
            await Task.Delay(500);

        var parts = last.Split('\t', 2);
        var at = DateTimeOffset.TryParse(parts[0], out var when) ? when : (DateTimeOffset?)null;
        var detail = (at is { } t ? $"At {t.LocalDateTime:g}: " : "") + parts[1]
                     + (lines.Length > 1 ? $" ({lines.Length:N0} times since the last launch.)" : "");

        var answer = await shell.ActiveTab.AskAsync(
            new ChoiceRequest(
                "Dance Commander stopped unexpectedly last time.",
                [
                    new DecisionOption("log", "Show the log", IsDefault: true),
                    new DecisionOption("dismiss", "Dismiss", IsCancel: true),
                ],
                detail,
                NoticeSeverity.Info),
            CancellationToken.None);

        if (answer != "log")
            return;

        // That day's log, by the date in its name: this launch has written to today's since, so
        // its time says nothing about when it stopped.
        var day = (at?.LocalDateTime ?? DateTime.Now).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var log = new DirectoryInfo(logDirectory).GetFiles($"dance-commander-{day}*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        await shell.ActiveTab.NavigateAsync(FsUri.Local(logDirectory), log?.Name);
    }



    private static void ConfigureServices(IServiceCollection services)
    {
        // Providers register themselves by scheme; the registry resolves by URI (§4.3).
        services.AddSingleton<IFileSystemProvider, LocalFileSystemProvider>();
        services.AddSingleton<IFileSystemProvider, AboutProvider>();
        services.AddSingleton<IFileSystemProvider, ArchiveProvider>();
        services.AddSingleton<IFileSystemProvider, SftpFileSystemProvider>();
        services.AddSingleton<IFileSystemProvider, WebDavFileSystemProvider>();
        services.AddSingleton<IFileSystemProvider, WebDavSecureFileSystemProvider>();
        // Script locations (§6.16): the location tokens come from the same .git reader the branch
        // badge uses, so a script is told the branch its location is anchored in.
        services.AddSingleton<ScriptSecrets>();
        services.AddSingleton<IScriptSecrets>(sp => sp.GetRequiredService<ScriptSecrets>());
        services.AddSingleton<IFileSystemProvider>(sp =>
        {
            var git = sp.GetRequiredService<IGitService>();
            return new ScriptProvider(
                sp.GetRequiredService<ICommandListingCatalog>(),
                sp.GetRequiredService<IScriptSecrets>(),
                anchor => git.Describe(anchor) is { } info
                    ? new RepositoryInfo(FsUri.Local(info.WorkTreeRoot), info.Detached ? null : info.Branch, info.RemoteUrl)
                    : null);
        });

        // Secrets live in the OS vault, host keys in a known_hosts of our own (§6.8, §7).
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IKnownHostsStore>(_ => new KnownHostsStore(DataDirectory));
        services.AddSingleton<SftpPrompts>();
        services.AddSingleton<ISftpPrompts>(sp => sp.GetRequiredService<SftpPrompts>());
        services.AddSingleton<DavPrompts>();
        services.AddSingleton<IDavPrompts>(sp => sp.GetRequiredService<DavPrompts>());
        services.AddSingleton<IDavConnections>(sp =>
        {
            var settings = sp.GetRequiredService<IConfigStore<SettingsFile>>();
            return new DavConnections(
                sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<IDavPrompts>(),
                () => settings.Current.Remote.UnencryptedSignIn);
        });
        services.AddSingleton<ISftpConnections, SftpConnectionPool>();
        services.AddSingleton<IRemoteCommandService, SftpCommandService>();

        // The archive provider sits in the registry and also needs it, so it takes an
        // accessor rather than the registry itself: the cycle is only in construction order.
        services.AddSingleton<Func<IProviderRegistry>>(sp => sp.GetRequiredService<IProviderRegistry>);
        services.AddSingleton<IArchiveCache, ArchiveCache>();
        services.AddSingleton<IArchiveBuilder, ArchiveBuilder>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<ICommandListingCatalog, CommandListingCatalog>();
        services.AddSingleton<IListingSourceFactory, ListingSourceFactory>();

        services.AddSingleton<ICommandRegistry, CommandRegistry>();
        services.AddSingleton<IIconService, IconService>();
        services.AddSingleton<IPreviewLoader, PreviewLoader>();

        // Hand-editable JSONC under %APPDATA%\DanceCommander, hot-reloaded (§7).
        services.AddSingleton<IConfigStore<BookmarksFile>>(sp => new JsonConfigStore<BookmarksFile>(
            DataDirectory, "bookmarks.json", sp.GetRequiredService<ILogger<BookmarksFile>>()));
        services.AddSingleton<IConfigStore<HistoryFile>>(sp => new JsonConfigStore<HistoryFile>(
            DataDirectory, "history.json", sp.GetRequiredService<ILogger<HistoryFile>>()));
        services.AddSingleton<IConfigStore<CollectionsFile>>(sp => new JsonConfigStore<CollectionsFile>(
            DataDirectory, "collections.json", sp.GetRequiredService<ILogger<CollectionsFile>>()));
        // Moves and renames the application makes, for whatever keeps paths to follow (§6.19).
        services.AddSingleton<IRelocationFeed, RelocationFeed>();
        // What the application itself put somewhere, for the views showing it to point at.
        services.AddSingleton<IArrivalFeed, ArrivalFeed>();
        services.AddSingleton<ICollectionService, CollectionService>();
        services.AddSingleton<IShelf, Shelf>();

        // tools.json is seeded with working examples on first run: an empty array would leave
        // the headline extensibility feature looking unimplemented (§6.6).
        services.AddSingleton<IConfigStore<ToolsFile>>(sp =>
        {
            var store = new JsonConfigStore<ToolsFile>(
                DataDirectory, "tools.json", sp.GetRequiredService<ILogger<ToolsFile>>());
            // Only when there is no file. A file that failed to parse also comes out with no tools,
            // and seeding on that wrote the defaults over whatever the user was editing.
            if (!store.FileExists)
                store.SaveAsync(ToolCatalog.Defaults()).GetAwaiter().GetResult();
            return store;
        });
        // Every config file, as one kind of thing the window can watch for mistakes in.
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<ToolsFile>>());
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<HighlightingFile>>());
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<BookmarksFile>>());
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<HistoryFile>>());
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<CollectionsFile>>());
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<ViewStateFile>>());
        // settings.json: not written until something in it is changed; missing is the defaults.
        services.AddSingleton<IConfigStore<SettingsFile>>(sp => new JsonConfigStore<SettingsFile>(
            DataDirectory, "settings.json", sp.GetRequiredService<ILogger<SettingsFile>>()));
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<SettingsFile>>());
        services.AddSingleton<CommandShells>();
        services.AddSingleton<ColourScheme>();
        services.AddSingleton(sp => new SettingsWriter((IConfigFile)sp.GetRequiredService<IConfigStore<SettingsFile>>()));
        services.AddSingleton<AppliedSettings>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<Func<SettingsViewModel>>(sp => sp.GetRequiredService<SettingsViewModel>);
        // commands.json: the command line's history, one list per shell spoken to.
        services.AddSingleton<IConfigStore<CommandHistoryFile>>(sp => new JsonConfigStore<CommandHistoryFile>(
            DataDirectory, "commands.json", sp.GetRequiredService<ILogger<CommandHistoryFile>>()));
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<CommandHistoryFile>>());
        services.AddSingleton<CommandHistory>();
        // palette.json: which commands are chosen in the palette, so the usual one leads a query.
        services.AddSingleton<IConfigStore<CommandUsageFile>>(sp => new JsonConfigStore<CommandUsageFile>(
            DataDirectory, "palette.json", sp.GetRequiredService<ILogger<CommandUsageFile>>()));
        services.AddSingleton<IConfigFile>(sp => (IConfigFile)sp.GetRequiredService<IConfigStore<CommandUsageFile>>());
        services.AddSingleton<CommandUsage>();
        services.AddSingleton<IToolRunner, ToolRunner>();
        // Which programs the tool packs found: local, as session.json is, because it is a fact
        // about this machine and roaming it would tell another one it has VS Code. Only a cache,
        // so not among the files whose mistakes are reported.
        services.AddSingleton<IConfigStore<PacksFoundFile>>(sp => new JsonConfigStore<PacksFoundFile>(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanceCommander"),
            "packs.json", sp.GetRequiredService<ILogger<PacksFoundFile>>()));
        services.AddSingleton(sp => new PackDetector(
            ProgramLocator.Machine, sp.GetRequiredService<IConfigStore<PacksFoundFile>>(),
            sp.GetRequiredService<ILogger<PackDetector>>()));
        services.AddSingleton<ToolCatalog>();

        // Decorators: git branches and highlight rules both arrive this way (§6.5), which is why
        // the grid knows about neither.
        services.AddSingleton<IConfigStore<HighlightingFile>>(sp =>
        {
            var store = new JsonConfigStore<HighlightingFile>(
                DataDirectory, "highlighting.json", sp.GetRequiredService<ILogger<HighlightingFile>>());
            if (!store.FileExists)
                store.SaveAsync(HighlightDecorator.Defaults()).GetAwaiter().GetResult();
            return store;
        });
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<HighlightDecorator>();
        services.AddSingleton<IRowDecorator>(sp => sp.GetRequiredService<HighlightDecorator>());
        services.AddSingleton<IRowDecorator, GitDecorator>();
        services.AddSingleton<IGitStatusService, GitStatusService>();
        services.AddSingleton<IRowDecorator, GitStatusDecorator>();

        services.AddSingleton<IQuickAccess, WindowsQuickAccess>();
        services.AddSingleton<IBookmarkService, BookmarkService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<FolderCompletion>();
        services.AddSingleton<IConfigStore<ViewStateFile>>(sp => new JsonConfigStore<ViewStateFile>(
            DataDirectory, "viewstate.json", sp.GetRequiredService<ILogger<ViewStateFile>>()));
        services.AddSingleton<IViewStateStore, PersistentViewStateStore>();
        // One for all tabs: Back in one pane is as good a reason to recall a folder as in the other.
        // Kept beside session.json: the server's names are this machine's business, not settings.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IConfigStore<SettingsFile>>();
            var memory = new ListingMemory(
                store: new JsonConfigStore<ListingsFile>(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanceCommander"),
                    "listings.json", sp.GetRequiredService<ILogger<ListingsFile>>(), compact: true),
                keep: () => settings.Current.Remote.KeepListings);
            var keeping = settings.Current.Remote.KeepListings;
            settings.Changed += (_, now) =>
            {
                if (now.Remote.KeepListings == keeping)
                    return;
                keeping = now.Remote.KeepListings;
                memory.KeepChanged();
            };
            return memory;
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IConfigStore<SettingsFile>>();
            return new ListingPrefetcher(
                sp.GetRequiredService<IProviderRegistry>(), sp.GetRequiredService<ListingMemory>(),
                () => settings.Current.Remote.Prefetch, sp.GetRequiredService<ILogger<ListingPrefetcher>>());
        });
        // Local, beside window.json: which folders were open is this machine's business (WORKSPACES_PLAN §4).
        services.AddSingleton<IConfigStore<SessionFile>>(sp => new JsonConfigStore<SessionFile>(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanceCommander"),
            "session.json", sp.GetRequiredService<ILogger<SessionFile>>()));

        // Operations are reified: undo, the activity log and the transfer strip all read the
        // same records (§1.2/6).
        services.AddSingleton<IUndoStack>(_ => new UndoStack());
        services.AddSingleton<IActivityLog>(_ => new ActivityLog());
        services.AddSingleton<IRecycleBin, WindowsRecycleBin>();
        // Asked only when something is in use, to say which program has it (§1.3).
        services.AddSingleton<IUsageFinder, WindowsUsageFinder>();
        services.AddSingleton<Launcher>();
        services.AddSingleton<IClipboardService, WindowsClipboard>();
        services.AddSingleton<IFileOperationService, FileOperationService>();
        services.AddSingleton<ITransferService, TransferService>();

        // One strip for everything that runs in the background: transfers and builds (§6.17).
        services.AddSingleton<IJobService, JobService>();
        services.AddSingleton<JobNotifier>();

        services.AddTransient<TabViewModel>();
        // Panes create tabs on demand; the container does not supply factory delegates itself.
        services.AddSingleton<Func<TabViewModel>>(sp => sp.GetRequiredService<TabViewModel>);
        services.AddTransient<PaneViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IEcho>(sp => sp.GetRequiredService<ShellViewModel>());
        services.AddSingleton<MainWindow>();
        services.AddSingleton<ShelfWindow>();
        services.AddSingleton<Func<ShelfWindow>>(sp => sp.GetRequiredService<ShelfWindow>);
    }

    /// <remarks>
    /// Synchronous on purpose. The exit saves (history, view state, command usage, listings) run
    /// in the services' Dispose; as an <c>async void</c> awaiting the host's stop first, a stop
    /// that ever yielded handed control back to WPF, which goes on to end the process, and the
    /// saves never ran (BUGS.md, 2026-10-28). The stop is bounded and its failure logged, so
    /// neither keeps the saves from running.
    /// </remarks>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                // Nothing hosted runs today, so this is done at once; the bound is for the day
                // something is, and waits on no continuation that needs this thread.
                if (!_host.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3)))
                    Log.Warning("The host did not stop within 3 s; saving and closing anyway");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Stopping the host failed; saving and closing anyway");
            }

            // Logged, not thrown: a Dispose that threw skipped releasing the single instance and
            // writing out the log that would have said why.
            try
            {
                _host.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Disposing the services at exit failed");
            }
        }
        _instance?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
