namespace DanceCommander.App.Services;

/// <summary>
/// The echo beside the Activity button (ShellViewModel.Echo), for a confirmation too small for a
/// notice: one quiet line that fades by itself, where a notice is a panel to dismiss.
/// </summary>
public interface IEcho
{
    /// <summary>On the UI thread.</summary>
    void Say(string text);
}
