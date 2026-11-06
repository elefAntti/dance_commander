using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.Core.Operations;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// The echo: one quiet line beside the Activity button saying what just happened to your files —
/// "Renamed report-final.pdf to report.pdf · Ctrl+Z undoes" — which fades after a few seconds and
/// is still there in the log behind the button.
/// </summary>
/// <remarks>
/// A rename or a new folder otherwise completes in silence, and the only way to be sure what it did
/// was to open the log. Failures are not echoed: each already has a louder place of its own, the
/// job strip or the pane's notice, and a failure fading away by itself would be the wrong message.
/// Nor are undos, which the pane already reports.
/// </remarks>
public sealed partial class ShellViewModel
{
    private static readonly TimeSpan EchoLasts = TimeSpan.FromSeconds(6);

    /// <summary>How long after an entry an undo push still counts as that entry's undo.</summary>
    private static readonly TimeSpan EchoPairsWithin = TimeSpan.FromSeconds(1);

    private DispatcherTimer? _echoExpiry;
    private DateTimeOffset _echoAt;

    /// <summary>What the echo says. Kept after it hides, so the fade-out has text to fade.</summary>
    [ObservableProperty]
    private string? _echo;

    /// <summary>The undo offer, apart from <see cref="Echo"/> so trimming a long path never takes it.</summary>
    [ObservableProperty]
    private string? _echoUndo;

    [ObservableProperty]
    private bool _isEchoing;

    private void EchoLatestActivity()
    {
        if (ActivityLog.Entries is not [{ Outcome: ActivityOutcome.Succeeded } latest, ..])
            return;

        _echoAt = latest.At;
        Echo = latest.Description;
        EchoUndo = null;
        ShowEcho();
    }

    /// <summary>
    /// An operation records itself before it becomes undoable — published last, after everything
    /// that makes it safe — so the offer arrives a moment after the echo it belongs to.
    /// </summary>
    private void OfferUndoInEcho()
    {
        if (!IsEchoing || DateTimeOffset.Now - _echoAt > EchoPairsWithin)
            return;

        // Whatever the key is now, since keybindings.json can move it.
        if (Commands.Find("dc.undo")?.Binding is not { } key)
            return;

        EchoUndo = $" · {key} undoes";
        ShowEcho();
    }

    private string? _undoingEcho;

    /// <summary>
    /// While an undo runs, said and kept until it ends. A long one (a copy of 85,000 files) showed
    /// nothing, and the next Ctrl+Z looked like the one that had worked (2026-10-17).
    /// </summary>
    private void EchoUndoing()
    {
        if (Undo.Running is { } running)
        {
            _echoAt = DateTimeOffset.Now;
            Echo = _undoingEcho = $"Undoing: {running.Description}…";
            EchoUndo = null;
            _echoExpiry?.Stop();
            IsEchoing = true;
        }
        else if (_undoingEcho is not null)
        {
            // Only our own line: something echoed meanwhile keeps its few seconds.
            if (Echo == _undoingEcho)
                IsEchoing = false;
            _undoingEcho = null;
        }
    }

    /// <summary>
    /// A confirmation that is not an operation on your files — "Error copied to the clipboard." A
    /// notice in the pane for it was one more panel to dismiss over the thing just copied.
    /// </summary>
    public void Say(string text)
    {
        _echoAt = DateTimeOffset.Now;
        Echo = text;
        EchoUndo = null;
        ShowEcho();
    }

    private void ShowEcho()
    {
        IsEchoing = true;

        _echoExpiry ??= CreateEchoExpiry();
        _echoExpiry.Stop();
        _echoExpiry.Start();
    }

    private DispatcherTimer CreateEchoExpiry()
    {
        var timer = new DispatcherTimer { Interval = EchoLasts };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            IsEchoing = false;
        };
        return timer;
    }
}
