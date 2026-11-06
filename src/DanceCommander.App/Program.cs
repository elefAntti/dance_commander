using System.Runtime.CompilerServices;
using DanceCommander.App.Services;
using DanceCommander.Core.Startup;

namespace DanceCommander.App;

/// <summary>
/// The entry point, ahead of WPF: everything that can be decided or shown without it happens here,
/// before the framework loads (App.xaml is a Page, not an ApplicationDefinition, for this reason).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The installer's run, so the first real launch is not the one that pays for Defender's
        // first look at a new binary and for unpacking the native libraries into %TEMP%\.net,
        // both done before this line (PLAN.md §3, Packaging). Gone again before anything else: no
        // window, no session, no settings, nothing in %APPDATA%. Not in the usage text; it is the
        // installer's, not a person's.
        if (args is ["--warm-up"])
            return;

        // Splits a slow "placeholder shown" into the runtime starting and our own code.
        StartupClock.Mark("main");
        var request = CommandLine.Parse(args);

        // Somebody who typed --help at a prompt wants text at that prompt. If we were launched
        // from one, that is the whole interaction; if we were not, the window shows it instead.
        if (request.Help && ConsoleEcho.TryWrite(CommandLine.Usage))
            return;

        // Handed to the copy already running, if there is one, so that "show in folder" lands in
        // the window you are using rather than a new one on top of it. Before the placeholder, so
        // a hand-off does not flash a window that then vanishes.
        var instance = new SingleInstance("DanceCommander");
        if (!instance.IsFirst && !request.NewWindow &&
            instance.TrySend(args, TimeSpan.FromSeconds(2)))
            return;

        Run(request, instance, LaunchPlaceholder.Show(restore: instance.IsFirst));
    }

    // A method of its own, so that WPF loads when this is called and not when Main is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(LaunchRequest request, SingleInstance instance, LaunchPlaceholder placeholder)
    {
        var app = new App(request, instance, placeholder);
        app.InitializeComponent();
        app.Run();
    }
}
