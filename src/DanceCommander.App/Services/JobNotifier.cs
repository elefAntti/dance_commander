using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using DanceCommander.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DanceCommander.App.Services;

/// <summary>What the user did with a finished-job notification.</summary>
public enum JobNotificationAction
{
    /// <summary>Clicked the notification itself: bring the window back.</summary>
    Show,

    /// <summary>Clicked "Open output folder".</summary>
    OpenResult,
}

/// <summary>
/// Says that a long job has finished, to somebody who has stopped looking at the window (§6.17):
/// the taskbar button flashes, and a Windows notification offers to open what the job produced.
/// </summary>
/// <remarks>
/// Only when the window is not in front. When it is, the job strip already changed in plain sight,
/// and a toast on top of that is the application talking over itself.
///
/// The notification is best effort, and failing to show one is logged rather than reported: the
/// job strip is the record, and the flash does not depend on any of this. An unpackaged application
/// has no identity for Windows to attribute a toast to, so it registers one under
/// <c>HKCU\Software\Classes\AppUserModelId</c> — the mechanism the Windows App SDK itself uses for
/// unpackaged apps. The window's own taskbar identity is left alone; changing it would stop the
/// running window grouping with a pinned shortcut.
/// </remarks>
public sealed class JobNotifier(ILogger<JobNotifier> log)
{
    private const string AppId = "DanceCommander";

    private ToastNotifier? _notifier;
    private bool _unavailable;

    // Held until activated or dismissed: the Activated handler lives on the notification object,
    // and one that has been collected can no longer tell us it was clicked.
    private readonly List<ToastNotification> _live = [];

    public event EventHandler<(Job Job, JobNotificationAction Action)>? Activated;

    public void Announce(Window window, Job job)
    {
        if (window.IsActive)
            return;

        Flash(window);
        ShowToast(job);
    }

    private void ShowToast(Job job)
    {
        if (Notifier() is not { } notifier)
            return;

        try
        {
            var succeeded = job.State == JobState.Completed;
            var headline = succeeded ? $"{job.Title} finished"
                : job.State == JobState.Cancelled ? $"{job.Title} stopped"
                : job.FailureHeadline;

            // The child's own words when it failed (§6.17), trimmed to what a toast can hold.
            var body = succeeded || job.Error is null ? job.Detail : LastLines(job.Error, 2);

            var action = job.HasResult
                ? """<actions><action content="Open output folder" arguments="open" activationType="foreground"/></actions>"""
                : string.Empty;

            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast launch="show" activationType="foreground">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(headline)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                  {action}
                </toast>
                """);

            var toast = new ToastNotification(xml);
            toast.Activated += (_, args) =>
            {
                Forget(toast);
                var chosen = (args as ToastActivatedEventArgs)?.Arguments == "open"
                    ? JobNotificationAction.OpenResult
                    : JobNotificationAction.Show;
                Activated?.Invoke(this, (job, chosen));
            };
            toast.Dismissed += (_, _) => Forget(toast);
            toast.Failed += (_, args) =>
            {
                Forget(toast);
                log.LogWarning(args.ErrorCode, "Windows would not show the job notification");
            };

            lock (_live)
                _live.Add(toast);
            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not show a notification for {Job}", job.Title);
        }
    }

    private void Forget(ToastNotification toast)
    {
        lock (_live)
            _live.Remove(toast);
    }

    private ToastNotifier? Notifier()
    {
        if (_notifier is not null || _unavailable)
            return _notifier;

        try
        {
            Register();
            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);

            // Notifications turned off for us, or for everything, is the user's decision and the
            // flash still happens; there is nothing more to do about it.
            if (_notifier.Setting != NotificationSetting.Enabled)
                log.LogInformation("Job notifications are turned off in Windows ({Setting})", _notifier.Setting);
        }
        catch (Exception ex)
        {
            _unavailable = true;
            log.LogWarning(ex, "Windows notifications are not available; finished jobs will only flash the taskbar");
        }

        return _notifier;
    }

    /// <summary>The identity Windows shows on the toast: our name and our icon.</summary>
    private static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "Dance Commander");

        var icon = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (File.Exists(icon))
            key.SetValue("IconUri", icon);
    }

    private static string LastLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimEnd('\r')).TakeLast(count));

    // ---- taskbar flash ---------------------------------------------------------------------

    /// <summary>
    /// Flashes the taskbar button until the window comes forward — Windows' own way of saying
    /// "something here wants you", and it works with notifications switched off.
    /// </summary>
    private static void Flash(Window window)
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(window).Handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
