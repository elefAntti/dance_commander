using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanceCommander.Core.Decisions;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// A decision rendered inline in the pane rather than in a message box (§1.2/2). The operation
/// that raised it is awaiting <see cref="Completion"/>, which may take as long as the user likes.
/// </summary>
public sealed partial class DecisionViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DecisionViewModel(ChoiceRequest request)
    {
        Title = request.Title;
        _detail = request.Detail;
        Severity = request.Severity;
        Options = request.Options;
        IsTextInput = false;
        TakesFocus = request.TakesFocus;
        FollowsFront = request.FollowsFront;
        if (request.LateDetail is { } late)
            _ = ReplaceDetailAsync(late);
    }

    private async Task ReplaceDetailAsync(Task<string?> late)
    {
        try
        {
            if (await late.ConfigureAwait(true) is { } detail)
                Detail = detail;
        }
        catch (Exception)
        {
            // The detail was an explanation, not the question; the question still stands.
        }
    }

    public DecisionViewModel(TextRequest request)
    {
        Title = request.Title;
        _detail = request.Detail;
        Severity = NoticeSeverity.Info;
        Options = [];
        IsTextInput = true;
        ConfirmLabel = request.ConfirmLabel;
        SelectStemOnly = request.SelectStemOnly;
        _validate = request.Validate;
        _text = request.InitialValue;
    }

    public DecisionViewModel(SecretRequest request)
    {
        Title = request.Title;
        _detail = request.Detail;
        Severity = NoticeSeverity.Info;
        Options = [];
        IsTextInput = true;
        IsSecret = true;
        RememberLabel = request.RememberLabel;
        ConfirmLabel = "Connect";
        FollowsFront = request.FollowsFront;
    }

    public string Title { get; }

    [ObservableProperty]
    private string? _detail;

    public NoticeSeverity Severity { get; }

    public IReadOnlyList<DecisionOption> Options { get; }

    public bool IsTextInput { get; }

    /// <summary>False for a question nobody asked for: it waits in the pane without the keyboard.</summary>
    public bool TakesFocus { get; } = true;

    /// <summary>See <see cref="ChoiceRequest.FollowsFront"/>.</summary>
    public Func<Core.Fs.FsUri, bool>? FollowsFront { get; }

    /// <summary>The tab showing it now, which is not always the one that asked.</summary>
    internal TabViewModel? Holder { get; set; }

    /// <summary>True when the field must be masked and its contents kept out of everything else.</summary>
    public bool IsSecret { get; }

    /// <summary>See <see cref="TextRequest.SelectStemOnly"/>.</summary>
    public bool SelectStemOnly { get; }

    /// <summary>Label for the "save this" toggle, or null when saving is not offered.</summary>
    public string? RememberLabel { get; }

    [ObservableProperty]
    private bool _remember;

    public string ConfirmLabel { get; } = "OK";

    public Task<string?> Completion => _completion.Task;

    private readonly Func<string, string?>? _validate;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _validationError;

    partial void OnTextChanged(string value) => ValidationError = _validate?.Invoke(value);

    [RelayCommand]
    private void Choose(string optionId) => _completion.TrySetResult(optionId);

    [RelayCommand]
    private void Confirm()
    {
        ValidationError = _validate?.Invoke(Text);
        if (ValidationError is not null)
            return;
        _completion.TrySetResult(Text);
    }

    [RelayCommand]
    public void Cancel() => _completion.TrySetResult(null);
}
