using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Listing;

/// <summary>
/// What a listing shows. Hiding is always reversible and never silent: the pane reports how many
/// entries the filter removed, so the tool never quietly conceals part of your filesystem.
/// </summary>
public sealed record ListingFilter(bool ShowHidden = false)
{
    public bool Matches(FsEntry entry) => ShowHidden || !entry.IsHidden;

    public ListingFilter Toggled() => this with { ShowHidden = !ShowHidden };
}
