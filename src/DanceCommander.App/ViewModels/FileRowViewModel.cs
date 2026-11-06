using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.App.Services;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Decorations;
using DanceCommander.Core.Fs;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// One grid row. Built on a worker thread during listing, so everything it touches must be
/// thread-safe and any resolved icon must already be frozen.
/// </summary>
public sealed partial class FileRowViewModel : ObservableObject, IRowDecorationSink
{
    private readonly FsEntry _entry;

    public FileRowViewModel(FsEntry entry, IIconService icons)
    {
        _entry = entry;

        // Only what is already known: asking the shell for an icon it has not given us yet takes
        // milliseconds per extension, and a listing that waited for every row's icon was a listing
        // that spent most of its time on them. The rest arrive a moment later (TabViewModel.FillIconsAsync).
        if (icons.TryGetCachedIcon(entry, out var icon))
        {
            _icon = icon;
        }
        else
        {
            NeedsIcon = true;

            // A program's own picture means opening the file, and a fresh download is scanned
            // first; its kind's icon stands in meanwhile, so the row is never blank while it waits.
            if (icons.ReadsOwnIcon(entry) && icons.TryGetCachedTypeIcon(entry, out var standIn))
                _icon = standIn;
        }
    }

    /// <summary>The icon was not in the cache when the row was made; one is on its way.</summary>
    public bool NeedsIcon { get; private set; }

    /// <summary>Called on the UI thread with an icon to show until the row's own arrives.</summary>
    public void ReceiveStandIn(ImageSource? icon) => Icon ??= icon;

    /// <summary>Called on the UI thread when the icon has been fetched.</summary>
    public void ReceiveIcon(ImageSource? icon)
    {
        NeedsIcon = false;

        // None given keeps the stand-in: a program that looks like a program beats a blank.
        Icon = icon ?? Icon;
    }

    public FsEntry Entry => _entry;

    /// <summary>Set by the highlight decorator; null means the row draws normally.</summary>
    [ObservableProperty]
    private HighlightStyle? _highlight;

    /// <summary>Set by the git decorator: the branch a repository folder is on.</summary>
    [ObservableProperty]
    private string? _badge;

    /// <summary>Set by the git status decorator: one letter saying how this row differs.</summary>
    [ObservableProperty]
    private RowMarker? _marker;

    /// <summary>
    /// How strongly the row shows that this application just put it here — copied, moved, created —
    /// from 0 (not at all, nearly always) to 1, as a brief wash of the accent. Never for a file some
    /// other program wrote. A level rather than a flag with an animation on the row: rows are
    /// replaced and their containers recycled mid-fade, and a level carries across both.
    /// </summary>
    [ObservableProperty]
    private double _arrival;

    /// <summary>Non-zero pins the row above the normal ordering.</summary>
    [ObservableProperty]
    private int _sortGroup;

    /// <summary>True while a drag hovers this folder, so the aim is visible before the release.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public string Name => _entry.Name;

    /// <summary>The name without its extension, which the Name column draws separately.</summary>
    public string Stem => _entry.Stem;

    /// <summary>
    /// The extension as it appears at the end of the name, dot and all, or empty.
    /// </summary>
    /// <remarks>
    /// Drawn immediately after <see cref="Stem"/> and dimmed. Splitting the extension into its own
    /// column made it sortable and made every filename in the listing look truncated; putting it
    /// back where it belongs and de-emphasising it keeps both -- the name reads as a name, and the
    /// part you scan for is still the part in front.
    /// </remarks>
    public string Suffix => IsDirectory || _entry.Extension.Length == 0
        ? string.Empty
        : "." + _entry.Extension;

    public bool IsDirectory => _entry.IsDirectory;

    public FsUri Target => _entry.Uri;

    [ObservableProperty]
    private ImageSource? _icon;

    /// <summary>
    /// On its way out: a Del has it, and the row says so from the key press on, struck through,
    /// until the listing no longer has it (2026-10-18). A delete with nothing on screen for two
    /// seconds read as one that hadn't happened.
    /// </summary>
    [ObservableProperty]
    private bool _isLeaving;

    /// <summary>
    /// Hidden and system entries are shown, just quietly. We do not hide things.
    /// </summary>
    public bool IsDimmed =>
        (_entry.Attributes & (FsEntryAttributes.Hidden | FsEntryAttributes.System)) != 0;

    /// <summary>What "r-h-" means, spelled out; null when there is nothing set.</summary>
    public string? AttributeNames => Format.AttributeNames(_entry.Attributes, IsDirectory);

    /// <summary>
    /// The flags worth seeing without asking, as icon-font glyphs after the name: a link, and a
    /// read-only file (a read-only <em>folder</em> means nothing on Windows — see
    /// <see cref="Format.Attributes"/>). Empty for nearly every row, which is the point: the Attr
    /// column this replaces was blank there too, but took 60 px to be blank in.
    /// </summary>
    public string NameGlyphs
    {
        get
        {
            var link = IsLink ? "\uE71B" : string.Empty;
            var locked = !IsDirectory && _entry.Attributes.HasFlag(FsEntryAttributes.ReadOnly) ? "\uE72E" : string.Empty;
            return link.Length > 0 && locked.Length > 0 ? $"{link} {locked}" : link + locked;
        }
    }

    /// <summary>
    /// A OneDrive file's state, after <see cref="NameGlyphs"/>: a cloud for one that is only online,
    /// a pin for one kept on this device. Empty for every other file.
    /// </summary>
    /// <remarks>
    /// Two of Explorer's three states; the third, "on this device for now", is how every other file
    /// is, so it gets nothing, as they do. Apart from the other glyphs because it is drawn on the
    /// cursor row only unless the settings say every row: a folder that is mostly online put a
    /// cloud on two rows in three. Its own leading space, so the gap after the name is the same
    /// with or without a link or lock before it.
    /// </remarks>
    public string CloudGlyph
    {
        get
        {
            var glyph = _entry.Attributes.HasFlag(FsEntryAttributes.CloudOnly) ? "\uE753"
                : _entry.Attributes.HasFlag(FsEntryAttributes.AlwaysKept) ? "\uE718"
                : string.Empty;
            return glyph.Length > 0 && NameGlyphs.Length > 0 ? " " + glyph : glyph;
        }
    }

    /// <summary>What the glyphs mean, and where a link points.</summary>
    public string? NameGlyphTip
    {
        get
        {
            var names = AttributeNames;
            if (!IsLink)
                return names;
            var target = _entry.LinkTarget is { Length: > 0 } t ? $"Link to {t}" : "Link";
            return names is null ? target : $"{target}\n{names}";
        }
    }

    private bool IsLink => _entry.Attributes.HasFlag(FsEntryAttributes.Symlink) || _entry.LinkTarget is { Length: > 0 };

    /// <summary>
    /// Whether the size column says anything: files, and drives — a drive's size is its capacity,
    /// which the computer page lists. A folder's size is not known without walking it, and neither
    /// is a file whose source didn't say (<see cref="FsEntry.SizeUnknown"/>).
    /// </summary>
    private bool HasSize => (!IsDirectory && !_entry.SizeUnknown) || _entry.Uri is { IsLocal: true, IsRoot: true };

    /// <summary>The size's number, for a column that aligns numbers and units separately.</summary>
    public string SizeNumber => !HasSize ? string.Empty : Format.SizeParts(_entry.Size).Number;

    /// <summary>The size's unit — kB, MB — or empty for plain bytes and for folders.</summary>
    public string SizeUnit => !HasSize ? string.Empty : Format.SizeParts(_entry.Size).Unit;

    /// <summary>Cell text by column id. Unknown ids fall through to provider-supplied values.</summary>
    public string this[string columnId] => columnId switch
    {
        ColumnIds.Name => Name,
        // Directories are given a value rather than left blank so that sorting by Ext gathers them
        // instead of scattering them through the empty-extension files. Angle brackets because
        // that is how every file manager since Norton has written it.
        ColumnIds.Extension => IsDirectory ? "<DIR>" : _entry.Extension,
        ColumnIds.Size => !HasSize ? string.Empty : Format.Size(_entry.Size),
        ColumnIds.Modified => Format.Date(_entry.Modified),
        ColumnIds.Attributes => Format.Attributes(_entry.Attributes, IsDirectory),
        _ => _entry.Extra?.GetValueOrDefault(columnId) ?? string.Empty,
    };

}
