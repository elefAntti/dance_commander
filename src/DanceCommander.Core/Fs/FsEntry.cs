namespace DanceCommander.Core.Fs;

[Flags]
public enum FsEntryAttributes
{
    None = 0,
    Hidden = 1,
    System = 2,
    ReadOnly = 4,
    Symlink = 8,
    Compressed = 16,
    Encrypted = 32,

    /// <summary>
    /// The content is not on this disk: a OneDrive (or other cloud) file kept online only. Reading
    /// it downloads it, so nothing should read it without being asked to.
    /// </summary>
    CloudOnly = 64,

    /// <summary>A cloud file set to "Always keep on this device".</summary>
    AlwaysKept = 128,
}

/// <summary>
/// One row's worth of provider-agnostic truth. Immutable: the view model wraps it and owns
/// everything mutable (selection, decorations, resolved icon).
/// </summary>
/// <param name="Extra">
/// Provider-specific cell values keyed by column id, for columns the provider contributes to
/// the listing's <see cref="Columns.ColumnSet"/> (SFTP owner and permissions, for example).
/// </param>
public sealed record FsEntry(
    FsUri Uri,
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified,
    FsEntryAttributes Attributes = FsEntryAttributes.None,
    string? LinkTarget = null,
    IReadOnlyDictionary<string, string>? Extra = null)
{
    /// <summary>
    /// The source could not say how big this file is (a script row with no size, such as a CI
    /// job's log). <see cref="Size"/> is then 0 so sums and sorts still work, and the grid leaves
    /// the cell blank instead of claiming "0 B".
    /// </summary>
    public bool SizeUnknown { get; init; }

    /// <summary>Extension without the dot, lower-cased. Directories have none.</summary>
    public string Extension
    {
        get
        {
            if (IsDirectory)
                return string.Empty;
            var dot = Name.LastIndexOf('.');
            return dot > 0 && dot < Name.Length - 1 ? Name[(dot + 1)..].ToLowerInvariant() : string.Empty;
        }
    }

    /// <summary>
    /// The name without its <see cref="Extension"/>, for grids that show the two in separate
    /// columns. The split is deliberately the same test as <see cref="Extension"/> rather than
    /// derived from its length, so stem and extension always reconstruct the whole name — a
    /// dotfile is all stem, <c>archive.tar.gz</c> is <c>archive.tar</c> plus <c>gz</c>, and a
    /// directory is never split at all.
    /// </summary>
    public string Stem
    {
        get
        {
            if (IsDirectory)
                return Name;
            var dot = Name.LastIndexOf('.');
            return dot > 0 && dot < Name.Length - 1 ? Name[..dot] : Name;
        }
    }

    /// <summary>
    /// Hidden by either convention: the Windows attribute, or a leading dot. Dotfiles carry no
    /// attribute on Windows but are exactly the clutter people mean, and they are the only
    /// convention that exists over SFTP.
    /// </summary>
    public bool IsHidden =>
        (Attributes & (FsEntryAttributes.Hidden | FsEntryAttributes.System)) != 0 ||
        Name.StartsWith('.');
}
