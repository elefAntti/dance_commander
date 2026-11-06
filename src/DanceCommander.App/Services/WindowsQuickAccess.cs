using System.Runtime.InteropServices;
using DanceCommander.Core.Navigation;
using Microsoft.Extensions.Logging;

namespace DanceCommander.App.Services;

/// <summary>
/// Explorer's Quick access, through the shell's own automation object. Pinned is the
/// <c>System.Home.IsPinned</c> property: true for what the user pinned, false for the frequent
/// folders and recent files Windows lists beside them (checked on Windows 11, 2026-10-14). Where the
/// property is missing (older Windows 10) nothing reads as pinned, and the fixed places stand alone.
/// </summary>
public sealed class WindowsQuickAccess(ILogger<WindowsQuickAccess> log) : IQuickAccess
{
    private const string QuickAccessFolder = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public Task<IReadOnlyList<PinnedFolder>> PinnedAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource<IReadOnlyList<PinnedFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The shell's objects are apartment-threaded: a thread of its own, so neither the UI thread
        // nor a pool thread is held while Explorer answers. Abandoned at the deadline, not stopped:
        // it ends by itself when the shell does.
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(Read(ct));
            }
            // Everything: on a thread of our own, an exception not caught here ends the whole
            // process, a transfer in the other pane included (review, 2026-10-14).
            catch (Exception ex)
            {
                log.LogWarning(ex, "Quick access could not be read");
                done.TrySetResult([]);
            }
        })
        {
            IsBackground = true,
            Name = "Quick access",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<PinnedFolder> Read(CancellationToken ct)
    {
        var pinned = new List<PinnedFolder>();
        if (Type.GetTypeFromProgID("Shell.Application") is not { } shellType)
            return pinned;

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic? folder = shell.NameSpace(QuickAccessFolder);
            if (folder is null)
                return pinned;

            foreach (dynamic item in folder.Items())
            {
                if (ct.IsCancellationRequested)
                    break;

                // Folders with a file-system path only: a library or This PC has none, and
                // about:computer covers those already.
                string path = item.Path ?? string.Empty;
                object? isPinned = item.ExtendedProperty("System.Home.IsPinned");
                if (!(bool)item.IsFolder || !System.IO.Path.IsPathFullyQualified(path) || isPinned is not true)
                    continue;

                pinned.Add(new PinnedFolder((string)item.Name, path));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return pinned;
    }
}
