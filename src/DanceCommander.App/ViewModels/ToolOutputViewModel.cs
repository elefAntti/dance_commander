using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.Core.Jobs;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// Output from something that ran, rendered in the pane rather than a window: either captured
/// once at exit, or followed live from a job as it prints (§6.17).
/// </summary>
public sealed partial class ToolOutputViewModel : ObservableObject, IDisposable
{
    private readonly Job? _job;
    private readonly DispatcherTimer? _repaint;
    private bool _dirty;

    /// <summary>Output that is already complete: a captured tool, a git command.</summary>
    public ToolOutputViewModel(string title, string text, bool failed)
    {
        _title = title;
        _text = TerminalText.Plain(text);
        _failed = failed;
    }

    /// <summary>Output that is still arriving, from a job in the strip.</summary>
    public ToolOutputViewModel(Job job, Dispatcher dispatcher)
    {
        _job = job;

        // Repainted on a timer, not per line. A build prints thousands of lines a second at its
        // busiest; the panel only has to look current, and a fifth of a second is current.
        _repaint = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _repaint.Tick += (_, _) => Repaint();

        job.PropertyChanged += OnJobChanged;
        if (job.Output is { } output)
            output.Changed += OnOutputChanged;

        _title = string.Empty;
        _text = string.Empty;
        Repaint();
        if (!job.IsFinished)
            _repaint.Start();
    }

    /// <summary>The job this panel is following, if it is following one.</summary>
    public Job? Job => _job;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _failed;

    private void OnOutputChanged(object? sender, EventArgs e) => _dirty = true;

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Core.Jobs.Job.State))
            return;

        // The final lines and the final title, then stop: nothing more is coming.
        _repaint?.Dispatcher.InvokeAsync(() =>
        {
            _dirty = true;
            Repaint();
            if (_job is { IsFinished: true })
                _repaint.Stop();
        });
    }

    private void Repaint()
    {
        if (_job is null)
            return;

        Title = _job.State switch
        {
            JobState.Pending or JobState.Running => $"{_job.Title} — running",
            JobState.Completed => $"{_job.Title} — {_job.Detail.ToLowerInvariant()}",
            JobState.Cancelled => $"{_job.Title} — stopped",
            _ => _job is Extensibility.ProcessJob { ExitCode: { } code }
                ? $"{_job.Title} — exit code {code}"
                : $"{_job.Title} — failed",
        };
        Failed = _job.State == JobState.Failed;

        if (!_dirty && Text.Length > 0)
            return;
        _dirty = false;

        var output = _job.Output;
        var text = output?.Text ?? string.Empty;

        // A log whose beginning silently vanished reads as a build that started half way through.
        if (output is { Dropped: > 0 })
            text = $"(earlier output trimmed: {output.Dropped:N0} lines)" + Environment.NewLine + text;

        Text = text.Length > 0 ? text : _job.IsFinished ? "(no output)" : "(waiting for output)";
    }

    public void Dispose()
    {
        if (_job is null)
            return;

        _repaint?.Stop();
        _job.PropertyChanged -= OnJobChanged;
        if (_job.Output is { } output)
            output.Changed -= OnOutputChanged;
    }
}
