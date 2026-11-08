# Dance Commander — Architecture Reference

Stack, abstractions, feature designs, configuration, keyboard model, testing and risks.
For scope and design principles, see [DESIGN.md](DESIGN.md); for what is next, [ROADMAP.md](ROADMAP.md).
Citations of PLAN.md, the `*_PLAN.md` files, IMPLEMENTATION_LOG.md, BUGS.md and AUDIT.md refer to
the author's working notes, which are not published.

**Status convention.** Unless it says otherwise, everything in this file describes what is
**built and in the application today**. Designed-but-unbuilt work carries `— planned` in its
heading, and each such section opens by naming what already exists underneath it. Nothing here
is aspirational without saying so: a reader should never have to run the app to find out
whether a paragraph is a description or a wish.

---

## 2. Stack decisions

| Concern | Choice | Why |
| --- | --- | --- |
| Runtime | .NET 10, `net10.0-windows`, x64 | Installed SDK is 10.0.403; WindowsDesktop 10.0.14 reference pack present. |
| UI | WPF, MVVM via `CommunityToolkit.Mvvm` | Source-generated `[ObservableProperty]`/`[RelayCommand]`, no reflection cost. |
| Composition | `Microsoft.Extensions.Hosting` + DI | Providers registered as keyed services by URI scheme. |
| Logging | Serilog → rolling file in `%LOCALAPPDATA%` | Diagnosing remote/IO failures needs a real log. |
| SFTP | `SSH.NET` | Mature, supports key auth, `SftpClient.ListDirectory`, seekable read streams. |
| WebDAV | `WebDav.Client` | Thin PROPFIND/PUT/MKCOL wrapper over `HttpClient`; we control auth handler. |
| Archives | `SharpCompress` | Zip/7z/tar/gz/bz2/rar (read), pure managed, streaming + random access for zip. |
| Shell icons | Hand-rolled `SHGetFileInfo` P/Invoke | ~25 lines, no dependency. `IFileOperation` for the Recycle Bin is hand-rolled too (`ShellRecycle`, 2026-10-17): only its delete is used, so a few interface declarations did, without Vanara. It recycles with `FOFX_RECYCLEONDELETE` rather than `FOF_ALLOWUNDO`, which also put each delete on the shell's undo stack, shared with Explorer. |
| Name matching | `DotNet.Glob` | Fast compiled globs for highlight rules and tool conditions. |
| Git | Read `.git` directly (no LibGit2Sharp initially) | Branch name is a 40-byte read of `HEAD`; avoids a native dependency and libgit2's repo-open cost. Revisit only if we want status/ahead-behind. |
| Tests | xUnit + Shouldly | Avoids FluentAssertions ≥8 commercial licensing. |

---

## 3. Solution layout

```
DanceCommander.sln
src/
  DanceCommander.Core/                 # abstractions, FsUri, models, config, rules engine
  DanceCommander.Providers.Local/      # local disk + FileSystemWatcher + git decoration
  DanceCommander.Providers.Sftp/       # SSH.NET provider, connection pool, host keys
  DanceCommander.Providers.WebDav/     # PROPFIND-based provider
  DanceCommander.Providers.Archive/    # archive-as-filesystem provider (nested URIs)
  DanceCommander.Extensibility/        # tool definitions, argument expansion, execution
  DanceCommander.App/                  # WPF: views, viewmodels, shell integration, DI root
tests/
  DanceCommander.Tests/
```

Rule: `Core` has no WPF and no Win32 reference. Providers depend only on `Core`. The App is
the only project that touches the Shell or the dispatcher. This keeps providers testable
headlessly and makes a future CLI/test harness trivial.

---

## 4. Core abstractions

### 4.1 Addressing: `FsUri`

One value type addresses everything, including nesting. Archive entry paths use `!` as the
boundary, recursively:

```
file:///C:/src/app
sftp://antti@build01:22/home/antti/logs
davs://cloud.example.com/remote.php/dav/files/antti/Docs
file:///C:/dl/release.zip!/bin/tools.tar.gz!/usr/bin
```

```csharp
public readonly record struct FsUri(string Scheme, string Authority, string Path, FsUri? Inner)
{
    public static FsUri Parse(string text);
    public FsUri Child(string name);
    public FsUri? Parent { get; }
    public string Display { get; }       // what the address bar shows
    public string ToPersistString();     // bookmarks/history/session
}
```

Everything downstream — bookmarks, history, tabs, tools, session restore — stores an `FsUri`
string. That is what makes "bookmark a remote location" fall out for free.

### 4.2 Provider contract

```csharp
[Flags]
public enum ProviderCapabilities
{
    None = 0, Read = 1, Write = 2, Delete = 4, Rename = 8, CreateDirectory = 16,
    RandomAccessRead = 32, Watch = 64, ExecuteHost = 128, // can be handed to an external exe
}

public interface IFileSystemProvider
{
    string Scheme { get; }
    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<FsEntry> ListAsync(FsUri dir, CancellationToken ct);
    Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct);
    Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct);
    Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct); // lands on CommitAsync
    Task CreateDirectoryAsync(FsUri dir, CancellationToken ct);
    Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct);
    Task MoveAsync(FsUri from, FsUri to, CancellationToken ct);   // same-provider only
    IDirectoryWatcher? Watch(FsUri dir);                          // null if unsupported
}

public sealed record FsEntry(
    FsUri Uri, string Name, bool IsDirectory, long Size,
    DateTimeOffset? Modified, FileAttributes Attributes, string? LinkTarget);
```

`FsEntry` is immutable and provider-agnostic; the grid binds to a `FileRowViewModel` wrapper
that adds selection, decorations, and lazily-resolved icon.

**A write lands only when committed (§1.6).** Every provider writes to `name.part` (numbered if
that name is taken, never opened over an existing file) and the stream implements
`ICommittableStream`. The caller calls `stream.CommitAsync(ct)` after the last byte; disposing
without it removes the `.part` and leaves the old file as it was. Committing over an existing
file never deletes it first: locally it is one `MoveFileEx` replace, on SFTP and WebDAV the old
file is renamed aside, the new one renamed in, and only then the old one deleted. A provider that
can tell two addresses are one file implements `IFileIdentity` (local: volume serial and file
index), which the transfer asks before writing over anything.

### 4.3 Provider registry

```csharp
public interface IProviderRegistry { IFileSystemProvider Resolve(FsUri uri); }
```

Resolution walks `Inner` first: a URI whose outermost segment is an archive resolves to
`ArchiveProvider`, which itself resolves its backing file through the registry. That is how
browsing a zip *on an SFTP server* works with no special-casing — the archive provider only
needs a `Stream` from whoever owns the inner URI.

### 4.4 Transfer engine

Copy/move across providers is a separate service, not a provider method:

```csharp
public interface ITransferService
{
    TransferJob Enqueue(IReadOnlyList<FsUri> sources, FsUri targetDir, TransferMode mode);
}
```

- Local→local uses `CopyFileEx` (progress callbacks, cancellation) via P/Invoke; falls back
  to stream copy.
- Everything else is stream copy with a bounded concurrency queue (default 2 parallel
  transfers, 1 per remote connection).
- Jobs surface in the job strip at the bottom of the window (§6.17), never a modal. A transfer is
  one kind of job; a build is another.
- Conflict policy resolved up-front per job (overwrite / skip / rename / ask-each).
- A copied file is committed only if all of it was read: fewer bytes than listed is checked
  against the source as it is now. A move deletes its source only after the target has been
  stat'ed at the size written. A target that is the source under another name is skipped.
- A job that stops early (cancel, failure, a "Cancel" answer, giving up on a file in use) puts
  back whatever a move had put into a folder being replaced, then records and, for a move, makes
  undoable what it did get done, as a finished job does. Replacing a folder where there is no
  Recycle Bin swaps the old one aside, as a file commit does.

---

## 5. UI architecture

### 5.1 Visual structure

```
┌──────────────────────────────────────────────────────────────┐
│ ◀ ▶ ↻  [ address bar / breadcrumb ]        ★ bookmarks  ⋯    │  per pane
├──────────────────────────┬───────────────────────────────────┤
│  tab  tab  +             │  tab  +                           │
│  ┌ Name  Ext  Size  Date │  ┌ Name  Ext  Size  Date          │
│  │ ..                    │  │ ..                             │
│  │ src/        main ⎇    │  │ file.txt                       │
│  └                       │  └                                │
├──────────────────────────┴───────────────────────────────────┤
│ status: 12 items, 3 selected (1.2 MB)      [transfers ▾]     │
└──────────────────────────────────────────────────────────────┘
```

- Two `PaneView`s in a `Grid` with a draggable `GridSplitter`; one has focus (visually
  marked), and it is the source for two-pane operations; the other is the default target.
- Each pane owns a tab collection; each tab owns its own history stack and current `FsUri`.
- "Uncluttered" is a design constraint, not a feature: one toolbar row per pane, no ribbon,
  no icon strip, chrome colors from the OS accent, list density configurable (compact default).

### 5.2 ViewModel graph

```
ShellViewModel
 ├─ PaneViewModel (left) ──┬─ TabViewModel* ──┬─ DirectoryViewModel
 └─ PaneViewModel (right) ─┘                  │   ├─ ObservableCollection<FileRowViewModel>
                                              │   ├─ NavigationHistory (back/forward)
                                              │   └─ IDirectoryWatcher subscription
 ├─ BookmarkService     (shared)
 ├─ HistoryService      (shared, global MRU)
 ├─ ToolCatalog         (shared, hot-reloaded)
 ├─ HighlightRuleEngine (shared, hot-reloaded)
 └─ TransferService     (shared)
```

### 5.3 Grid

Use `ListView` + `GridView`, **not** `DataGrid`: `DataGrid`'s per-cell container cost is the
usual reason WPF file lists stutter at 50k rows. Required settings:

- `VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`,
  `ScrollUnit=Item`, `IsVirtualizingWhenGrouping=True`.
- No implicit `DataTemplate` with nested panels per cell beyond what's needed; one
  `TextBlock` per column, icon column uses a shared image cache.
- Columns: Name, Ext, Size, Modified, Attributes, plus optional provider-specific
  (Owner/Permissions for SFTP). Column set is per-provider and persisted.
- Sorting and filtering are `ListingOrder` / `ListingFilter` in Core, applied to a backing list
  and assigned as a whole — **not** via `CollectionView.SortDescriptions` (which re-sorts on
  every add during a watcher burst). The pane keeps the unfiltered listing, so toggling hidden
  entries or the sort never goes back to the provider.
- **Folders sort together with files by default**, the way Finder does it, with classic
  folders-on-top available as `ListingOrder.DirectoriesFirst`. `..` is a navigation affordance
  and never takes part in the ordering.
- Names compare naturally, so `file9` precedes `file10`; zero-padded equivalents get a
  deterministic tiebreak rather than comparing equal.
- Typing in the list filters it in place; Backspace and Esc take the filter back.

### 5.4 Icons

`IIconService` returns a cached `ImageSource` keyed by *extension* (not path) for ordinary
files, and by path only for local `.exe` and `.ico` files and special folders (not `.lnk`: a
shortcut's icon is its target's, and a target on a missing share stalls the one-at-a-time shell
lookup for every pane). Resolution happens off the UI
thread via `SHGetFileInfo` with `SHGFI_USEFILEATTRIBUTES` so it never touches the disk for a
remote or archive entry. Remote/archive entries always use the extension-keyed icon.

A path-keyed icon means opening the file, which Defender scans first when it is new, so a grid
fills in two passes: every extension-keyed icon at once, with the extension's icon standing in
on `.exe`/`.ico` rows, then their own one file at a time, nearest the cursor first, each handed
to its row as it arrives. Rows made from the cache get the stand-in straight away.

### 5.5 Threading model

- All provider calls are async and run off the UI thread; `ListAsync` results are batched
  (e.g. 256 entries or 50 ms, whichever first) and marshalled once per batch to the
  dispatcher. This is what keeps a 200k-entry directory from freezing the shell.
- Every navigation gets a `CancellationTokenSource`; navigating away cancels the in-flight
  listing before starting the next.
- Watcher events are coalesced on a background timer before touching the UI (see §6.1).

---

## 6. Feature designs

### 6.1 Live folder monitoring (local)

`LocalDirectoryWatcher` wraps `FileSystemWatcher` with the three things that make the naive
version unreliable:

1. **Buffer overflow.** `InternalBufferSize = 64 KB`, and on `Error` (`ERROR_NOTIFY_ENUM_DIR`)
   we drop all pending deltas, raise `ResyncRequired`, and the `DirectoryViewModel` does a
   full re-list. Bursty builds *will* overflow; this is not optional.
2. **Event storms & duplicates.** Raw events go into a `Channel<FsChange>`; a consumer
   coalesces per-name over a 50–100 ms debounce window (Created+Changed+Changed → one
   upsert; Created+Deleted → nothing) and emits a batch. `Renamed` is a single
   remove+insert keyed on old/new name.
3. **Partial writes.** A `Created` event fires before the writer is done. Size/time come
   from a `Stat` at flush time; if the file is locked we keep the entry with the last known
   values and re-stat on the next tick rather than showing 0 bytes forever.

The watcher is non-recursive (`IncludeSubdirectories = false`) — we only show one directory.
Exception: the git decoration watcher (§6.3) which watches `.git/HEAD` specifically.

**Remote/archive:** `Watch()` returns `null`. The UI falls back to (a) manual `F5`,
(b) optimistic local mutation after a successful write/delete/rename, and (c) an optional
per-connection background re-list on an interval (default off; 30 s when enabled). Archives
are immutable while open, so no refresh at all unless the backing file changes.

### 6.2 Navigation, history, bookmarks

- **Per-tab history** is a classic back/forward stack of `FsUri` + scroll position +
  selection. `Alt+Left/Right`, mouse back/forward buttons, `Backspace` = up (not back).
- **Global history** is an MRU list persisted to `history.json`, capped (2000 entries), used
  for address-bar autocomplete. Remote entries record scheme+authority+path, never
  credentials.
- **Bookmarks** (`bookmarks.json`) are a user-ordered tree of `{ name, uri, group?, hotkey? }`.
  Grouping is the foldering mechanism, and it needs no dedicated UI: typing `Work/Build logs`
  when adding files the bookmark under a `Work` heading in the popup. Each entry carries the
  shell folder icon, or a network icon for a remote scheme, so the list is scannable.
  The popup also offers "Edit bookmarks.json…", because the file is the real interface and
  hiding it would contradict the point of the project.
  Because the target is an `FsUri`, `sftp://build01/var/log` is bookmarkable exactly like
  `C:\src`. Opening a remote bookmark triggers connect-on-demand (credentials from the
  credential store; prompt only if missing).
- Address bar accepts: a local path, a full `FsUri`, or a bookmark name. `Ctrl+L` focuses it.
- The address bar **completes inline** as you type, as a browser does: the rest is filled in after
  the caret, selected, so Enter takes it, Tab takes it and leaves the caret at the end to type on,
  typing on replaces it and Backspace/Delete drops it. A
  bookmark whose name starts with the text wins; otherwise the shortest known address that
  carries on from it — the folder being typed, not something buried under it. Only after typing
  at the end of the text, never after a deletion. The suggestion list is unchanged, so bookmarks
  that only *contain* the text are still offered there.
- The suggestion list starts with **the folders on disk** that carry on from a typed path
  (`FolderCompletion`): `C:\Users\An` offers the folders in `C:\Users` starting with "An", hidden
  ones only once a letter is typed. Drives and Windows shares only; listing an SFTP or WebDAV
  folder would connect to it mid-typing. Enter after an arrow key goes to the suggestion's own
  address, not its text read back, which a name with `!`, `%` or a trailing space would change.
  The folder is listed once, off the window's thread, and each further
  letter filters that listing; history is on show until it arrives, and it is let go when the
  address bar loses focus. Up and Down **write the highlighted suggestion into the box**, caret at
  the end, so a separator and the next letters carry on from it; the "Search for" row puts the
  typed words back instead. The list is not rebuilt while the arrows are in it.
- It **searches, but only with text that could never be an address** (`AddressSearch`). Enter
  tries, in order: `?text` (always a search, as in a browser); a bookmark's name or the start of
  one; a bare word with no `\`, `/` or `:`, which is a search; and then an address. A bare word
  used to parse as a driveless path that led nowhere, so nothing is lost, and a mistyped path still
  errors rather than quietly searching for itself. The suggestion list offers it as its last
  row, so it never displaces a place.
- The address bar's search is **the whole computer, from here outwards** (`everywhere:C:/work?word`,
  `EverywhereListingSource`), as a browser's searches the web; `Ctrl+S` stays the search of one
  folder (`search:`). From a server, an archive or an app page the address bar falls back to that.
  By the quick filter's rule, as `search:` is. The places (`SearchAreas`) are walked one after
  another, in the order what is wanted is likely to be there: the folder the pane is in, Desktop,
  Documents, Downloads and OneDrive, the rest of the home folder, the system drive's other folders
  and other users', the other fixed and removable drives (never network ones), Program Files, and
  Windows last. Each is fenced off from the others' roots, so nothing is walked twice and a folder
  that is a place of its own is still found by name from the one above. The order is most of the
  speed: on the development machine the first row from here was on screen in 30 ms, from Documents
  in 50, and the whole machine took 10 s.
  - Besides hidden folders it declines **clutter** by name (`PrunePolicy`: `node_modules`, caches,
    temp folders, `WinSxS`, Windows Installer's store …) and doesn't follow links to folders. The
    pane says how many folders it declined, and how many it could not read, which does not end the
    walk. Ctrl+H walks the clutter too, as it walks the hidden.
  - A **Where** column says which place a row came from, and the rows sort by it by default; it
    sorts by a key (`Relevance`, via `ColumnIds.SortKeySuffix`), not its text: the place, then
    whether a person or a machine made it (`.dll`, `.pdb` and the like, anything under `bin`, `obj`,
    `build`…), then how well the name matches (`NameMatcher.Rank`), then changed in the last month,
    year or before, then how deep. The Folder column is the whole path. An index (Everything, Windows Search) could sit behind
  `search:` as an engine later, re-filtered by `NameMatcher` so results do not depend on which
  answered; not built, because the walk is fast enough and neither can be assumed installed.
- A `search:` **opens in order of relevance** too (`ColumnSet.OfSearch`): the same key with the
  place left out, since one folder is one place. It is an order with no column (`ColumnIds.Relevance`,
  a sort key on each row), so the Name header says "(by relevance)", as it says "(by type)" for
  Ctrl+F4. Ctrl+F7 sorts by a listing's `RelevanceColumnId`, in `search:` and `everywhere:` only.

### 6.3 Git branch decoration

When a local directory is listed:

1. Walk up from the directory looking for `.git` (a directory, or a file containing
   `gitdir: …` for worktrees/submodules). Cache the result per directory in a bounded LRU,
   plus a negative cache so non-repo trees cost one lookup.
2. Read `HEAD`: `ref: refs/heads/<branch>` → branch name; a raw SHA → `(detached: abc1234)`.
3. Display the branch:
   - in the pane header/breadcrumb when the current directory is inside a repo,
   - as a badge on child rows that are themselves repo roots (so a folder of repos shows all
     branches at a glance).
4. Keep it live with a `FileSystemWatcher` on the repo's `.git` filtered to `HEAD`, so
   branch switches update instantly. One watcher per distinct repo currently on screen,
   released on navigation.

Branch is read from `.git` directly because a branch name is a forty-byte read. **Per-file
status is not**, and is now built (§6.3a): reproducing it means reading the index, applying
every `.gitignore` in the tree and stat-ing the work tree, which is a reimplementation of git
rather than a shortcut. So it shells out.

### 6.3a Per-file git status

`GitStatusService` runs, once per repository and cached:

```
git --no-optional-locks status --porcelain=v1 -z
```

- **`--no-optional-locks` is load-bearing, not an optimisation.** Without it `git status` takes
  `index.lock` to write back its refreshed stat cache. A file manager that idly locks the index
  of every repository you browse will eventually collide with the terminal you are working in.
  It costs a little speed on repeat calls and buys never being the reason a commit failed.
- `-z` so a filename containing a quote, a newline or a non-ASCII character arrives intact.
- Not `--ignored`. Ignored rows get no letter, and walking the ignored build trees to say so
  was half the time on a large repository (1.2 s against 0.57 s). A wholly untracked directory
  still comes back collapsed as one entry. A feature that needs ignored entries (the `.gitignore`
  lens) asks for them itself, when it is on.
- Results are **rolled up**: a folder containing a modified file is reported modified, because a
  row that gives no sign anything below it changed is the reason people keep a terminal open
  beside their file manager. Loudest state wins. Collapsed untracked directories are
  *not* rolled up — the point of git collapsing them is that nothing inside needs looking at.

`GitStatusDecorator` turns that into a `RowMarker` (§6.5): git's own letter in git's own colour,
so there is nothing to learn. Unchanged gets no letter — a column of marks saying "this is
fine" would drown the three rows that are not.

Staleness is handled by the tab, which is the only thing that knows a change happened: the pane's
directory watcher invalidates on any work-tree change, and the `.git` watcher covers `HEAD` *and*
`index` — a commit does not rewrite `HEAD`, so watching `HEAD` alone would leave every row marked
modified after the change that unmodified them.

Invalidating drops the cached answer, not the last one. An editor's own git rewrites `index`
every few seconds, so a fresh answer is rarely there on arrival: the decorator puts the last
answer's marks up at once and corrects them when git replies. Only the highlight rules decide
sort order, so the view is sorted when they are done, not when git is.

### 6.4 Highlighting rules

`highlighting.json`, hot-reloaded, first match wins (top-down), evaluated once per row at
list time and cached on the row VM. The file is seeded with example rules, every one
`"enabled": false`: a fresh listing has no colour until the user turns some on
(`HighlightDecorator.Defaults`). The shape:

```jsonc
{
  "rules": [
    { "name": "Sources",   "match": { "glob": "*.{cs,xaml}" },              "foreground": "#4FC1FF" },
    { "name": "Build junk","match": { "glob": "{bin,obj,node_modules}" , "directoriesOnly": true },
                           "foreground": "#808080", "italic": true },
    { "name": "Big files", "match": { "minSize": "100MB" },                 "background": "#3A2A00" },
    { "name": "Secrets",   "match": { "regex": "(?i)(secret|\\.pem$|id_rsa)" },
                           "foreground": "#FF6B6B", "bold": true }
  ]
}
```

- Matchers: `glob`, `regex`, `extensions`, `attributes`, `minSize`/`maxSize`,
  `olderThan`/`newerThan`, `directoriesOnly`/`filesOnly`. Multiple conditions in one rule AND
  together.
- Styling: `foreground`, `background`, `bold`, `italic`, `icon`, `sortGroup` (lets a rule
  also pin matched rows to the top).
- Implementation: rules compile once to a `CompiledRule[]` (`DotNet.Glob` + `Regex` with
  `RegexOptions.Compiled`). Applied in the VM as a `HighlightStyle` object bound to the row —
  no per-cell converters, no triggers, so the cost is one array scan per row.

### 6.5 Decorations (extension point behind git + highlighting)

```csharp
public interface IRowDecorator
{
    ValueTask DecorateAsync(IReadOnlyList<FileRowViewModel> rows, FsUri dir, CancellationToken ct);
}
```

Git branch badges and highlight styles are both decorators. Decorators run after the listing
is materialised, are allowed to be async and slow, and mutate rows on the dispatcher. Adding
"show image dimensions" or "show git status" later means adding a decorator, not touching the
grid.

### 6.6 Custom tools / right-click menu ("Open in bash")

This is the central extensibility mechanism, so it is declarative and hot-reloaded, with a
code plugin path reserved for later.

`tools.json`:

```jsonc
{
  "tools": [
    {
      "id": "open-in-bash",
      "title": "Open in bash",
      "group": "Terminal",
      "icon": "terminal",
      "appliesTo": "directory|background",     // file | directory | background | multi
      "schemes": [ "file" ],                   // only where a real path exists
      "exec": "C:/Program Files/Git/bin/bash.exe",
      "args": [ "--login", "-i" ],
      "workingDirectory": "${dir}",
      "launch": "process",
      "keybinding": "Ctrl+Shift+B"
    },
    {
      "id": "open-in-wsl",
      "title": "Open in WSL",
      "appliesTo": "directory|background",
      "schemes": [ "file" ],
      "exec": "wsl.exe",
      "args": [ "--cd", "${dir}" ],
      "launch": "console"
    },
    {
      "id": "vscode",
      "title": "Open with VS Code",
      "appliesTo": "file|directory|background",
      "exec": "code",
      "args": [ "${selection}" ],
      "when": { "glob": "*.{cs,md,json}" }
    },
    {
      "id": "sha256",
      "title": "SHA-256",
      "appliesTo": "file|multi",
      "exec": "certutil",
      "args": [ "-hashfile", "${path}", "SHA256" ],
      "launch": "capture",                     // show stdout in an output pane
      "forEachSelected": true
    }
  ]
}
```

**Editors.** `"edits": true` makes a tool what F4 (`dc.edit`) uses for the files its `when`
matches; the first applicable one in file order wins. Without one, F4 reads the file's first 8000
bytes with the preview's `TextInspector`: text is opened as a `.txt` (ShellExecuteEx with
`SEE_MASK_CLASSNAME`, so the `.txt` association's whole command applies, never the file's own),
anything else goes to ShellExecute's `edit` verb, and `ERROR_NO_ASSOCIATION` from that becomes a
notice. Enter means use the file and F4 its source, so F4 never falls through to
`open`: it is the key for looking at a script without running it. For the same reason the catalog
refuses `edits` on a tool whose `exec` reads an entry (it would run the file) or whose `args` never
name one.

**Run** (`dc.run`, Shift+Enter; `dc.runWithArguments`) is the other half: `RunLine.For` builds the
line a person would type to run the file (`bash ./x.sh`, `pwsh -NoProfile -File .\x.ps1`,
`py ./x.py`, `.\x.exe`), quoted for the command line's shell, and hands it to the command line,
either run at once or left there for arguments. No process of its own, so a run is exactly a typed
line: same shell, folder, output, jobs and history. Always `./` or a full path, so a name is never
an option or a PATH lookup. Refused rather than escaped, judged on the whole path that goes on the
line: `${` (the line's placeholders); `%` under cmd; and for a `.bat`/`.cmd` under any shell,
`% & ^ ( ) ! | < >`, because Windows starts a batch file with `cmd /c`, which reads them again
(AUDIT.md §2 kind 26); `[ ]` under PowerShell, which Windows PowerShell reads as a wildcard
even quoted, and `' " [ ] { } * ? ~` in a `.sh` reached from cmd or PowerShell, which Git Bash's
runtime globs (kind 28). The interpreter (pwsh, py, Git Bash) goes on the line by its full path in
every shell, since cmd searches the browsed folder first and any shell does with `.` on PATH; one
that isn't found is not run. Typed bash and pwsh lines reach the shell quoted by `ProcessArguments`
(MSYS's rules and the C runtime's respectively), verbatim, so they are expanded only once. A file with a Zone.Identifier of Internet or worse is
asked about inline before it runs, as ShellExecute would have warned on Enter.

**Location conditions.** `"inRepository": true` offers a tool only inside a git work tree. This
is a fact about the location rather than about any entry, so the §6.4 matcher — which matches
entries — cannot express it; it lives on `ICommandContext` instead, where the pane answers it
for free from what the branch decoration already worked out. A handful of read-only git commands
ship as built-ins using the same predicate (`Git status`, `Git log`, `Git diff`, `Git blame`),
which is the cheap two-thirds of what a plugin host would have been for: no process boundary, no
load context, no API to keep stable. Staging and committing are deliberately absent — they belong
to whatever you already commit with, and a file manager that half-implements git is worse than
one that shows you what git thinks and gets out of the way.

**Placeholder expansion** (`${…}`): `path`, `dir`, `name`, `stem`, `ext`, `selection` (all
selected paths as separate argv entries), `otherPaneDir`, `uri`, `env:NAME`,
`prompt:Label` (asks the user for a value), and the location tokens `repoRoot`, `branch` and
`remoteUrl` (below). Expansion produces an **argv array**, never a
command string — arguments are passed to `ProcessStartInfo.ArgumentList`, so no quoting or
shell-injection hazard from filenames containing `"` or `&`.

**Location tokens.** Every other token answers a question about the *entry*. These three answer
questions about the *location*: `repoRoot`, `branch`, `remoteUrl`. They are the same from anywhere
inside a work tree, which is the point — "Open repository in VS Code" (`code ${repoRoot}`, shipped in
the defaults) opens the repository from a file four folders down.

- **Where the values come from.** `ICommandContext.Repository` (a Core `RepositoryInfo`: root,
  branch, remote URL), filled in by the tab from the same `GitService.Describe` read that draws the
  branch chip, so the chip and the tokens cannot disagree. `InRepository` is now just
  `Repository is not null`. A flattened tree or a search asks about the folder it was made from, so
  a repository command does not vanish on `Ctrl+Shift+F`.
- **`branch` is null when HEAD is detached.** A seven-character SHA is not a branch, and a script
  parsing a ticket key out of one would be parsing noise.
- **`remoteUrl` is read from `.git/config` directly** (`GitConfig`), for the reason HEAD is: a few
  lines of INI against a process per listing. The branch's own upstream wins, then `origin`, then
  the only remote there is; several with nothing to choose between them is no answer rather than a
  guess. A linked worktree's config is the main repository's, found through `commondir`. Written
  exactly as it appears, so an SSH remote stays an SSH remote.

**Filters** reshape a value for a program with its own ideas about spelling: `${name|filter}`.
There is one, `lowerDrive` (`c:\work\orbit`), and it exists because of dev containers: VS
Code labels a container with the folder it was opened from, drive letter lowercased, and
`devcontainer exec --id-label devcontainer.local_folder=${repoRoot|lowerDrive}` is then the one
spelling that finds the container for whichever worktree you are standing in. (`--workspace-folder`
cannot be made to work for a container VS Code started through WSL: it also matches the config
file's path, which VS Code recorded as its WSL-side copy.) Named for what it does rather than who
wants it — the application knows about drive letters, not about VS Code. A misspelt filter keeps
the tool from loading, with a log line, rather than passing the value through unchanged: a lookup
that almost works is the failure this exists to fix.

**`remoteMatches`** offers a tool only where the repository's remote URL matches —
`"remoteMatches": "*/orbit.git"` — so a project's own build stays out of every other
repository's menus and follows it into its worktrees, which share the remote. The quick filter's
rule (a bare word means "contains"), except that `*` crosses `/`: a URL is mostly slashes, and a
file-name glob's `*` would never match one.

**`inConflict`** offers a tool only where git reports a merge conflict, so the Beyond Compare pack's
"Resolve conflicts" is not on every repository menu for the one day in fifty it has work.
`RepositoryInfo.Conflicts` carries the unmerged paths from the tab's last `git status` (the one
the row marks came from, `IGitStatusService.Last`): a lookup when the context is made, never a git
run, so a menu does not wait on one. A tool about the selection wants every row to be a conflicted
file or a folder holding one; a tool about the folder, a conflict anywhere in the repository.

**`maxSelected`** caps the rows a tool takes: "Compare" is for two or three files. **A tool that
reads `${otherPaneDir}`** is offered only with the other pane on screen and showing one of the
tool's schemes. With one pane, `${otherPaneDir}\${name}` came out as `\name`, the root of the drive.

**An unresolvable token suppresses the command** rather than expanding to empty: an argv entry that
silently becomes `""` produces a confusing failure from the child process instead of a clear
absence. The tool catalog checks every template a tool has (`exec`, `args`, `workingDirectory`,
`outputDirectory`) in `CanExecute`, so such a tool is simply not on the menu — which is why the VS
Code entry needs no `inRepository` flag. The command line has no applicability check to lean on, so
there the expansion stops before anything runs *or asks* (no `${prompt:…}` answered and thrown
away) and the pane says which token had no value and why.

Location-level built-ins are offered **whatever is selected**: `Git status`, `Git log` and the
`Worktrees` listing used to be scoped to empty space, so they vanished whenever a row had focus —
which in a listing is nearly always.

**Launch kinds.** `process` (through the shell), `console` (its own console window), `capture` (run,
wait, show the output in the pane) and `job` (§6.17). Everything except `process` bypasses the
shell, so a bare name like `devcontainer` is resolved against `PATH`/`PATHEXT` by hand
(`ExecutableSearch`) — otherwise only `devcontainer.exe` would ever be looked for, and the npm shim
is a `.cmd`. A batch file breaks the argv guarantee, because cmd.exe re-parses its command line with
its own rules; rather than lose the guarantee quietly, an argument containing a cmd metacharacter
(`& | < > ^ % "`) is refused for a `.cmd`/`.bat` target, with a notice naming it.

**Materialisation for non-local schemes.** `${path}` on an SFTP or in-archive entry has no
meaning to an external exe. The tool runner checks the provider's `ExecuteHost` capability;
if absent it either (a) fails with a clear message when `schemes` excludes the current one,
or (b) if the tool sets `"materialize": true`, downloads the selection to a temp dir, runs
the tool against the temp copy, and — for `"writeBack": true` — re-uploads on change (watched
via the same watcher machinery). This is what makes "edit a remote file in your editor" work.

**Menu assembly.** The context menu is built per right-click from: built-in commands (open,
copy, move, delete, rename, new folder, properties, group-into-folder) + matching tools,
grouped by `group`, with an overflow submenu once a group exceeds ~8 items. `when` conditions
reuse the §6.4 matcher engine — one matcher implementation for both features.

**Tool packs** (SETTINGS_PLAN.md §4, 2026-10-13). The tools for programs a machine may or may not
have live in the app, not in `tools.json`: `ToolPacks.All` (Git Bash, Windows Terminal, WSL,
VS Code, Windows), each with a way to find its program (`ProgramLocator`: PATH and App Paths, a
known file, WSL only with a Linux installed). `PackDetector` looks after the first listings and
again when Settings → Tools opens, never on the startup path; the last answer is kept in
`%LOCALAPPDATA%\DanceCommander\packs.json`, so the tools are there from the first frame. A found
pack's tools are registered unless `settings.json` says `"tools": { "packs": { "vscode": false } }`;
a tool in `tools.json` with a pack tool's id, on or off, is used instead of it, and the user's tools
register after the packs' so their keys win. A pack found later than the last look is announced
once in the pane. `tools.json` is seeded only with the devcontainer example, turned off. This
replaced the offer of "tools new since your tools.json was written" (`ToolOffers`): a better pack
now reaches everyone, as a better default setting does.

**Future code plugins.** `IToolProvider` implemented in a DLL dropped into
`%APPDATA%/DanceCommander/plugins/<name>/`, loaded in a collectible `AssemblyLoadContext`
with the plugin folder as probing path. Deferred to M7 — the declarative form covers the
stated needs and cannot destabilise the app.

### 6.7 Group selection into a folder

Command `GroupIntoFolder` (default `Ctrl+Shift+G`):

1. Propose a name: longest common prefix of the selection with separators trimmed, else the
   parent directory name, else `New Folder`. Shown in an inline rename-style editor.
2. Create the directory, then move each selected item into it via the provider's
   `MoveAsync` (same-provider fast path) or the transfer service.
3. Single undo entry: the operation records (originalUri → newUri) pairs and the created
   folder, so `Ctrl+Z` moves everything back and removes the folder if empty.
4. Collisions inside the new folder are resolved by the standard conflict policy.

A general undo stack (`IUndoableOperation`, depth ~20, in-memory, covering move/rename/create
/group; delete only when it went to the Recycle Bin) is worth building for this feature and
reused by everything else.

### 6.11 Starting from another program (command line)

Another program starting Dance Commander is a file manager's front door, and there is a precedent
to follow rather than invent. Explorer's syntax is accepted **verbatim**, and that is the design
goal rather than a nicety: every installer, browser and chat client that offers "show in folder"
already shells out to `explorer.exe /select,"C:\pathfile.zip"`. Understanding that exact string
means those integrations work with no change at the other end, including the ones that cannot be
changed at all — a shipped binary, someone else's script.

```
DanceCommander                     where you left off
DanceCommander PATH                open PATH
DanceCommander LEFT RIGHT          one location per pane (implies two panes)

DanceCommander /select,PATH        Explorer: PATH's folder, cursor on PATH
DanceCommander /n,PATH             Explorer: one pane
DanceCommander /e,PATH             Explorer: two panes

--select PATH / --left / --right / --one-pane / --two-panes / --new-window / --help
```

The comma with no space after it is Explorer's spelling, quirk and all, so it is ours. `/select`
with the path as a *separate* argument is accepted too, because callers that build argv as an array
tend to lose the comma and accepting it costs nothing.

A PATH is an `FsUri`, not a path: `sftp://user@host/var/log` and `flat:C:\work` are valid places to
start. The one ambiguity is that `C:` looks like a URI scheme — resolved by the fact that a drive
letter is one character and a scheme is more.

**Single instance by default.** "Show in folder" has to land in the window you already have open;
a file manager that opened a fifth window every time a download finished would be abandoned by
lunchtime. A per-user mutex plus a named pipe (`SingleInstance`) hands the arguments to the running
copy, which opens them **in a new tab** rather than in place — the command line arrives while
somebody is in the middle of something, and replacing what they were looking at to show them a
finished download would lose their place to tell them something they did not ask about just then.
The window is restored and activated, because doing this behind three other windows is the same as
not doing it. `--new-window` opts out, and a failure to hand over is ordinary rather than
exceptional: a second window is a worse outcome than reusing the first and a much better one than
refusing to start.

**`--help` goes to the terminal**, not to a dialog — this application has no `MessageBox` (§1.2/7),
and somebody who typed `--help` at a prompt wants text at that prompt. Redirected output is used
directly when there is any; otherwise `AttachConsole(ATTACH_PARENT_PROCESS)` borrows the parent's.
Launched from Explorer with no console at all, the usage goes into the pane's output panel, which
is already where this application puts text you asked for and can copy.

**Arguments that cannot be read are reported**, on the pane's notice bar, naming the offender. A
launcher passing the wrong thing is a bug in the launcher, and silently opening the home folder
instead hides it from the only person who can fix it.

### 6.12 Listings from a command

A location whose rows are the paths a program printed. The motivating case is git worktrees —
with several agents working in parallel checkouts of one repository, the set of places you care
about changes under you, and **a bookmark list is one you maintain while a generated one maintains
itself**. But nothing in the mechanism is about git.

```jsonc
// tools.json
"listings": [
  {
    "id": "todo-paths",
    "title": "Paths in TODO.md",
    "exec": "rg",
    "args": ["-o", "[A-Za-z]:[\\/][^\s]+", "TODO.md"],
    "extract": "^(?<path>.+)$"
  }
]
```

Addressed as `from:<location>?<id>` — a view (§4.1) whose inner URI is the directory the command
runs in and whose parameter names the entry. **The command is never in the URI.** A `from:` address
names something in the user's own configuration, so a bookmark cannot carry an arbitrary command
line; that would be a very different kind of object to hand around.

- **`extract` is matched repeatedly against the whole output**, not line by line. That one decision
  covers both shapes of output there are: `^(?<path>.+)$` with `Multiline` for a list of paths, and
  a pattern spanning several lines for a stanza format. It is what makes git's `--porcelain` usable,
  and `--porcelain` is required — the plain `git worktree list` separates fields with spaces, so a
  worktree in `C:/My Projects` has no unambiguous reading.
- The path comes from a group named `path`, or from group 1. **Every other named group can become a
  column**, declared in `columns` so the header text and the order are the author's rather than the
  regex's. This is also the answer to "which worktree has a fresh build": the app cannot know what
  counts as a build, but a script can print it, and the column shows whatever it printed.
  The path column takes the width left over unless one declared column says `"flexible": true` —
  the worktree listing does, for the branch, because worktrees usually sit side by side in one
  folder and the path says the same thing on every row. It also sets `"showModified": false`: for a
  list of folders, Modified is only when something at their top level last changed.
- Rows are **real entries**, stat-ed through the provider registry, so opening, copying, previewing
  and `F3` behave exactly as they would in the folder the file actually lives in.
- **A path that no longer exists is shown, not dropped** — with `not found` in the Note column and a
  count in the footer. A worktree list quietly missing the one you deleted has stopped telling you
  the truth about your machine.
- A failing command reports **its own stderr**, not ours. It knows why it failed and we do not.
- Each entry also registers a command, gated on `inRepository` where asked, so it appears in the
  context menu on empty space rather than needing a URI typed by hand.

Worktrees ship built in, because that is the case that motivated the feature and because
`Defaults()` only writes `tools.json` when there is none — anyone with an existing file would
otherwise never see it. Branch names on those rows are not part of this mechanism at all: a worktree
root is a repository root, so the existing branch decorator (§6.3) badges them for free.

**The boundary of this mechanism.** Every row here is a path that already exists on a mounted
filesystem; the command's only job is to *name* rows, and the provider registry does the rest.
That is what keeps it small, and it is also the reason it cannot carry GitLab artifacts or JIRA
attachments: those have no path until a network call spends one. The generalisation that covers
them is §6.16, and it is a different mechanism — a provider — rather than a bigger `listings`
block. `from:` as described above stays exactly as it is.

### 6.13 Quick filter (type to narrow)

Typing letters into a listing narrows it. This **replaces** the grid's type-ahead rather than
sitting beside it, and the reasoning is that filtering is strictly the better version of the same
gesture: type-ahead moved the cursor to one row, filtering shows every row those letters match with
the first already selected — so `Enter` still opens what type-ahead would have found, and the rest
are there to see. There is no setting because there is no trade-off.

- The query rule is `NameQuery`, **shared with the search location** (§1.2/3): a bare word means
  "the name contains this", anything with `*` or `?` is a glob. What you learn typing in a pane is
  what you know when you search from one.
- **Several words are all required**, each by that rule and in any order — the command palette's
  rule too. `mon` shows CPU monitor, disk monitor and pokemon; `mon d` leaves disk monitor. So a
  space narrows rather than having to be matched, and a name with a space in it still matches.
- **Matches are ranked, then sorted.** In order of weight: the first word starts the name; every
  word starts a word of the name (after a separator, a camel-case hump, a letter/digit change —
  CPU monitor before pokemon); the words come in the order typed. Within a rank the listing's own
  order holds. Glob words take no part in ranking: a wildcard already says where the text goes.
- It is post-hoc over the loaded rows, so it is instant however the rows got there — a directory, a
  flattened tree, a search result, a command listing.
- It is **not** part of `ListingFilter`. That is remembered per location (§1.2/5), and a filter
  still in force when you came back tomorrow would be a folder that had quietly lost its files.
  This one clears on navigation.

Three keys are contested, and each is resolved the same way — the filter owns them only while it is
running, and `Escape` is always the way out:

| Key | Not filtering | Filtering |
| --- | --- | --- |
| letters | *(nothing)* | appends to the filter |
| `Space` | belongs to the listing | appends — a filter never *starts* with a space |
| `Backspace` | go to parent folder | deletes the last character |
| `Escape` | dismisses whatever panel is up | clears the filter |

`Backspace` is handled in the window's key router next to the existing exemptions ("a text box owns
its keys", "a focused button owns Enter and Space") rather than by weakening `dc.up`'s
`CanExecute`: the command still means "go to parent folder" everywhere, it is only the *key* that is
on loan. `Escape` is one more link in the `dc.dismiss` chain, ahead of stopping a load.

`Space` is in the router for a less obvious reason: a `ListView` handles Space itself as a
selection key and marks it handled, so it never arrives as text input at all. Left to
`PreviewTextInput`, a filename with a space in it silently lost the space.

The bar sits directly above the listing, carries the accent, and says `Esc to clear`. Rows
disappearing from a pane with nothing on screen to explain why is indistinguishable from a bug, so
the filter is never invisible — and when nothing matches, the pane says so, because an empty
listing otherwise reads as an empty folder, which is a much more alarming thing.

### 6.14 Clipboard (copy, cut, paste)

`Ctrl+C` / `Ctrl+X` / `Ctrl+V`, alongside `F5`/`F6` rather than instead of them. The two-pane keys
move things between the panes you can see; these move things between this application and every
other one, which is a different job and deserves its own keys.

**Copy and cut put two formats on the clipboard**, the same pair a drag already carries:

| Format | For | Notes |
| --- | --- | --- |
| `DanceCommander.Uris` | us | Full-fidelity URIs, so a remote or in-archive entry survives |
| `CF_HDROP` + `Preferred DropEffect` | everyone else | Local paths only, plus the DWORD that distinguishes a cut from a copy |

A selection with no local path — an SFTP file, something inside an archive — is put on the
clipboard in our own format **only**. Advertising `CF_HDROP` for it would promise Explorer a file
that is not there. Pasting it back into Dance Commander still works, because the transfer service
copies between providers anyway.

**Paste reads in a fixed order, and the order is the whole design:**

1. **Files** — becomes a transfer, exactly as a drop does.
2. **Bitmap** — encoded to PNG and written as a new file.
3. **Text** — written as a new UTF-8 file.

Files must win, and not as a tie-break: copying an image file in Explorer puts *both* a file list
and a bitmap on the clipboard. A paste that reached for the bitmap first would quietly re-encode a
perfectly good JPEG as a new PNG under a made-up name, losing the original's name, its metadata and
its bytes. The bitmap branch is therefore what is left when nothing behind the image is a file,
which is the screenshot case it exists for.

**Where it lands.** Always the directory the pane is showing, never a folder that merely happens to
be selected. Explorer does the second thing from a right-click and the first from `Ctrl+V`, and the
difference between them is invisible at the moment you press the key.

**What a paste decides** is `PastePolicy`, deliberately not `DropPolicy`. A drag has to guess its
verb from where it started and where it is hovering; a paste was told, when the user chose copy or
cut. So `PastePolicy` takes the clipboard's verb and only refuses what cannot be done at all. The
visible difference is pasting into the folder you copied from: a drag there is a slip and is
refused, a paste there is the ordinary way to duplicate a file.

**Text and images become files through `IFileOperationService.CreateFileAsync`**, so they are
reified like every other mutation (§1.2/6) — undoable, and in the activity log with a shell
equivalent that genuinely reproduces them (`(Get-Clipboard -Format Image).Save(…)`). They are named
from the clock (`Pasted image 2026-09-21 14-30-45.png`) rather than by prompting: pasting three
screenshots in a row should not be three dialogs, and the new row arrives selected with `F2` right
there. They never overwrite — a paste names itself, so a collision steps aside to `name (2)`.

**Every call into the clipboard retries.** It is a single system-wide resource, any process can
hold it, and `CLIPBRD_E_CANT_OPEN` is routine rather than exceptional — frequent enough to have
been hit while testing this. WPF's `Clipboard` has no retrying overload (WinForms' does), so
`WindowsClipboard` wraps both directions. The retry covers *opening* the clipboard; a per-format
guard around each `GetData` covers an application that offers a format and then fails to render it,
where the right answer is to fall through to the next format rather than fail the paste.

`CanExecute` for paste deliberately does **not** peek at the clipboard. It runs on every legend
repaint, which is every modifier keypress, and reading the clipboard is a synchronous call into
whichever process owns it — one slow OLE server would be felt as a stuttering keyboard. Paste
always offers itself and reports that there was nothing there, which costs one keystroke in the
rare case and keeps the window responsive in the common one.

### 6.15 Command palette

`Ctrl+Shift+P`. The fourth renderer over the one registry, after the F-key legend, the context menu
and the keymap (§1.2/1) — it registers nothing of its own and knows nothing a command did not
already declare. `CommandPalette` in Core decides what is on offer and in what order;
`CommandPaletteViewModel` is a thin wrapper and the view is a text box and a list.

- **The query rule is `NameQuery`'s** (§6.13): a bare word means "the title contains this". Several
  words are all required, in any order. The group matches too but scores lower, so "git" finds all
  of them while "Git status" still leads its own group.
- **An empty query is not a query.** The resting list is ordered by group and title — the same
  grouping the context menu uses — not by the search ranking, whose tiebreaks sort a list nobody
  asked a question about into an order nobody can see.
- **Recently invoked rises to the top**, session-lived and not persisted. It describes what you are
  doing now.
- **Every row shows its key.** The legend teaches one modifier layer at a time; this teaches the
  rest, and the point of a palette entry is to stop needing the palette.
- **Only what applies here is listed**, and the footer says so with a count — rows missing with
  nothing on screen to explain why is the thing this application exists to avoid (§1.3). Commands
  with no group stay out, the line the context menu already draws: no group means a key rather than
  a menu item, which is `dc.dismiss` and the palette itself.
- **A third text input, and transient rather than a region.** The address bar and the command line
  are permanent regions (§1.2/7); this one floats over the window, owns the keyboard only while it
  is up, returns focus to the grid on `Escape`, and dismisses when a click moves focus elsewhere
  without stealing that click back. No scrim: it is transient, not modal (§1.2/2).
- **It closes before the command runs**, because half of what is in it raises an inline prompt in
  the pane, and that prompt must not arrive behind the thing that asked for it.

### 6.16 Script-backed locations

*Built:* the provider, the addressing and secrets — as designed below, with the details the design
left open settled as follows. JIRA upload (step 4 in PLAN.md §13) is an ordinary §6.6 tool and is
not part of this.

```jsonc
"listings": [
  {
    "id": "gitlab",
    "title": "Pipelines",
    "exec": "pwsh",
    "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/gitlab-pipelines.ps1",
             "${remoteUrl}", "${branch}"],
    "script": true,
    "secrets": ["gitlab-token"],
    "inRepository": true,
    "sort": "modified"
  }
]
```

`scripts/gitlab-pipelines.ps1` is that script: the branch's open merge requests (each with a
`review-comments.md` and its own pipelines), the branch's pipelines and an "(all branches)" folder
→ a pipeline's jobs → a job's `artifacts.zip` and `job.log`, the project found from the
remote URL, the API at `https://<host>/api/v4` unless `DC_GITLAB_API` says otherwise. It lives in
the repository's `scripts/`, not in the application, which still knows nothing about GitLab.

`scripts/git-history.ps1` is the second (`?git-gone`, "Deleted files"): what git remembers below the
folder that is no longer there, one flat level. A row's crumb is `<blob id>.<repository path,
percent-encoded>`. The blob says which bytes; the path is there because `read` runs
`git cat-file --filters --path=…`, the conversions a checkout applies (autocrlf, `eol`, LFS), and a
raw blob would restore LF text and LFS pointers on Windows. The listed size is the blob's, before
conversion. A checkout only grows a file, and the transfer engine rejects a copy shorter than
listed, not a longer one.

- **The call** is `exec args… list <crumbs>` or `exec args… read <crumbs>`, run in the folder the
  location is anchored to; the crumbs are the levels joined with `/`, empty at the root. There is
  no `stat` verb: a stat answers from the parent level's rows, cached for 30 seconds, because the
  transfer engine stats every item it copies.
- **Rows** come through `extract` as in §6.12. Groups: `name` (required), `kind` (`d` makes a
  folder), `crumb` (defaults to the name), `size`, `modified` (ISO 8601) and any declared column.
  Without an `extract`, a script location reads one tab-separated line per row:
  `kind  name  size  modified  crumb`, all but the name optional.
- **Crumbs** are letters, digits and `. _ ~ - %`; a row with any other crumb is shown with a note
  saying it needs percent-encoding, not dropped and not rewritten.
- **Tokens** `${repoRoot}`, `${branch}`, `${remoteUrl}` and `${dir}` expand in `args`, from the same
  `.git` reader the branch badge uses.
- **Secrets** arrive as `DC_SECRET_<NAME>` (`gitlab-token` → `DC_SECRET_GITLAB_TOKEN`). Read from
  the Windows credential store under `script:<name>`; missing, asked for inline in the pane, stored
  only if "remember" is ticked, otherwise kept for the session.
- **Reads** go to a temporary file, so the stream seeks and an `artifacts.zip` opens as an archive.
  A download answers repeat reads of the same file for two minutes — recognising an archive and
  then opening it would otherwise be two downloads.
- A script location is a view (§1.2/3), so anything that writes "here" writes into its anchor, like
  a flattened tree or a search. Opening a file in its own application is refused as for any
  non-local file: copy it somewhere local first.

The motivating cases are GitLab pipeline artifacts and JIRA tickets, but **the application ships
no knowledge of either**. A script owns the service; DanceCommander owns listing, navigation,
preview and copy.

**These two cases are one feature.** Pipelines → jobs → artifacts and ticket → attachments are
the same shape at different depths. Designing them separately would produce two mechanisms where
one does.

**It is a provider, not a listing.** The moment rows are hierarchical, have identity, and can
produce bytes, the thing being described is `IFileSystemProvider`'s read subset — and adopting
that contract rather than inventing a parallel one is what makes the rest free. A `ScriptProvider`
declares `Read` and, deliberately, **not** `ExecuteHost`:

| Script verb | Contract method | Notes |
| --- | --- | --- |
| `list <opaquePath>` | `ListAsync` | prints rows; named regex groups become columns as in §6.12 |
| `read <opaquePath>` | `OpenReadAsync` | writes bytes to stdout |
| `stat <opaquePath>` (optional) | `StatAsync` | falls back to what `list` reported for the parent |

Because the contract is the ordinary one, the transfer engine, F3 preview, bookmarks, history,
session restore, sorting, quick filter and per-location view state all work with no changes. The
payoff worth stating explicitly: artifacts are zips, and §4.3 already resolves `Inner` before the
outer scheme, so browsing *into* `artifacts.zip` on a pipeline row and copying one file out of it
needs no new code in either provider.

**Addressing.** The §6.12 shape extends rather than changes:

```
from:file:///C:/src/app?gitlab/12345/build-job
     └── inner URI ───┘ └id┘ └── crumb ──┘
```

The inner URI is the repository directory the script runs in; the listing id names something in
the user's own configuration; the crumb is data the script printed and later receives back. The
§6.12 invariant is unchanged and load-bearing: **the command is never in the URI.**

One command serves every depth. Depth 0 prints pipelines, depth 1 prints that pipeline's jobs,
depth 2 prints a job's files; the script owns the hierarchy entirely and DanceCommander only
passes the crumb back.

*The separator question, settled.* `!` was the obvious candidate and is the wrong answer.
`TryParse` splits the whole address on `!` **before** any scheme is examined and wraps every
later segment in `ArchiveEntry`, which hard-codes the `archive` scheme — so
`from:…?gitlab!/12345` resolves to the archive provider, and making it not do so means the
separator's meaning must depend on what sits to its left. It would also give `Inner` two jobs in
one chain: the containing *file* for an archive entry, the location being *viewed* for a view.

The crumb therefore lives in the view's own `Path`, and the parameter carries `id` first with the
crumb after the first `/`. This is the smaller change because the real defect is elsewhere:
**views are flat by construction.** `View()` pins `Path` to `"/"`, `Parent` leaves the view
immediately from any depth, and `Child()` drops `Parameter` — so descending into a row loses the
listing id. That last one is a bug in its own right (PLAN.md §13) and wants fixing
whether or not this feature is ever built. What this design needs from `FsUri` is exactly:

- `View()` takes a path rather than hard-coding the root.
- `Child()` carries `Parameter` through.
- `Parent` walks `Path` down to the view's root before returning `Inner`.
- `ToString()`/`Display` render `scheme:inner?id/crumb`; parsing keeps its `LastIndexOf('?')`
  rule and splits the parameter at its first `/`, so the listing lookup still reads an id.

*Crumbs are constrained, not escaped.* Anything in `Path` passes through `Normalize`, which
resolves `..`, collapses `//` and drops empty segments — correct for paths and wrong for an
identifier that must come back byte-for-byte. Rather than bypassing normalisation for one scheme,
the contract restricts crumbs to a safe alphabet; pipeline ids, `ABC-123` ticket keys and
attachment ids all satisfy it natively, and a script needing arbitrary bytes percent-encodes its
crumb. The schema says so, and a crumb that fails the rule is rejected when the script prints it,
where the message can name the offending row.

*Crumbs compare ordinally.* `Parameter` currently compares and hashes `OrdinalIgnoreCase`, which
is defensible for a `search:` query and wrong for a crumb, where `Build` and `build` may be two
different jobs. View state, history and tab identity are all keyed by `FsUri`, so two sibling
rows differing only in case would silently share one entry. The crumb segment needs ordinal
comparison; the listing id may keep its current looseness.

**Deriving the location's identity is the script's job.** A JIRA ticket key lives at the front of
a branch name, but the convention is the user's, so the script receives `${branch}` (§6.6) and
parses it. The cost is that applicability cannot be known without running the script: a branch
with no ticket yields an empty listing rather than a command that never appeared. Accepted for a
first version; if a second case wants it, `when: { branchMatches: … }` reuses the §6.4 matcher
engine rather than adding a second one.

**Two consequences to expect rather than discover:**

- A bookmarked `from:…?jira-ticket` resolves to a *different* ticket next week on a different
  branch. This is correct — it is "the ticket for this branch", the same self-maintaining
  property §6.12 exists for — but it will surprise someone, so the address bar should read as a
  query rather than as a place.
- Session restore (§7) would otherwise mean network calls at startup. A restored tab holding a
  script location shows its loading row and fetches lazily; it does not make application start
  wait on a service being reachable.

**Writing is deliberately absent.** Attaching a file to a JIRA ticket is a §6.6 tool
(`appliesTo: file|multi`), not a `Write` capability — not because a fourth verb would be hard,
but because the path is not the addressing. "Attach this to issue X" has no meaningful overwrite,
rename, delete or mkdir, and declaring `Write` would be a claim the transfer engine believes and
acts on. `${otherPaneDir}` already exists, so "upload the selection to the ticket open in the
other pane" needs no new placeholder.

**Secrets.** Config declares `"secrets": ["gitlab-token"]`; the runner resolves each name from the
credential store (§7) and passes it **in the environment, never in argv**. §6.6 insists on argv
arrays to defeat injection, but argv is readable by any process on the machine and an environment
block is not — different threat, different channel. Script stderr reaches the pane and the
activity log verbatim (§6.12: a failing command reports its own reason, not ours), so a script
that echoes its own token leaks it into the log; say so in the schema description.

**Everything here is slow**, which is the one cross-cutting requirement. No script runs
synchronously on the UI thread: listings get the loading-plus-stop treatment that flatten and
search (M8.5) already established, a `read` of a large artifact is a job in the strip (§6.17)
because it is indistinguishable from a download, and a missing credential raises an inline
prompt in the pane, never a dialog (§1.2/2).

### 6.17 Background jobs

A tool with `"launch": "job"` runs in the job strip: its output streams into the pane as it
arrives, it can be stopped, and it says when it is done. The motivating case is running a compiler
in a devcontainer:

```jsonc
{
  "id": "devcontainer-build",
  "title": "Build in devcontainer",
  "exec": "devcontainer",
  "args": ["exec", "--workspace-folder", "${repoRoot}", "make"],
  "workingDirectory": "${repoRoot}",
  "outputDirectory": "${repoRoot}/build",   // offered as "Open output folder" on success
  "launch": "job"
}
```

That entry ships in the defaults, disabled, because what "build" means is the project's business.

**The transfer strip became a job strip.** A transfer is one kind of job alongside a build, and a
build reuses the strip rather than claiming a sixth chrome region (§1.3). `Job` (Core) owns title,
detail line, progress or indeterminacy, state, error, cancellation and an optional
`ResultLocation`; `TransferJob` and `ProcessJob` are the two kinds. `IJobService` holds the list
the strip binds to and raises `JobFinished` once per job; `ITransferService` shrank to `Enqueue`.
The rename happened while transfers were the only implementation, which is what made it cheap.

A job is **broader than a reified operation** (§1.2/6): a build has no undo and no invertible
shell equivalent. `IOperation` stays what it is; jobs are the surface that owns progress,
cancellation and completion, and operations are one thing that can run as one.

**The new work was streaming, not backgrounding.**

- `OutputBuffer` keeps what the process printed as it arrives: a bounded ring (4,000 lines) that
  drops the oldest first and counts what it dropped, and the panel prints that count — a log whose
  beginning silently vanished reads as a build that started half way through.
- The pane's output panel follows a live job (`IOutputHost.Follow`), repainting on a 200 ms timer
  rather than per line, and keeps the newest line in view unless you have scrolled up to read.
  Dismissing the panel stops the following, not the job; the strip's **Output** button brings it
  back in the active pane.
- The strip's one detail line is the last line printed, throttled to five changes a second.
- **Stop kills the whole process tree.** A devcontainer build is node running the docker CLI running
  a compiler; killing only the first leaves the rest printing to nobody. What it cannot reach is a
  process *inside* the container, which `docker exec` leaves running when its client goes away —
  that is docker's behaviour, and worth knowing before trusting Stop to free the CPU.
- A progress bar with no total says only that it is moving (indeterminate), and the taskbar does
  the same when nothing running can measure itself — a build counted as 0% would drag a copy
  running beside it backwards.

**Failure shows the child's own words**: its last lines of stderr, or, when it wrote nothing there,
the end of stdout — plenty of compilers report errors on stdout. The headline carries the exit
code, in the semantic red of §1.3.

**Completion.** A job that finishes stays in the strip until dismissed (a transfer that succeeds
still retires itself after four seconds). On success with an `outputDirectory`, the row offers
**Open output folder**, which opens it in the *other* pane — the one you are in is where you started
the build from and are presumably still working. When the window is not in front, finishing also
flashes the taskbar button (`FlashWindowEx`, until the window comes forward) and shows a Windows
notification carrying the same action. When it is in front, the strip changing in plain sight is
the announcement, and a toast on top would be the application talking over itself.

The notification is best effort. An unpackaged application has no identity for Windows to
attribute a toast to, so `JobNotifier` registers one under `HKCU\Software\Classes\AppUserModelId`
— the mechanism the Windows App SDK uses for unpackaged apps — and leaves the window's own taskbar
identity alone, since changing it would stop the window grouping with a pinned shortcut. Failure to
show one is logged, not reported: the strip is the record, and the flash does not depend on it.

### 6.18 "This folder": commands about the location

*Built*, all three steps: `CommandSubject` on every command, with tool inference; the context menu
built from it; empty space opening *This folder* without touching the selection; the `..` row's own
menu; the folder submenu at the foot of every row menu; the palette in sections
(`CommandPalette.Sections`), with Tab and Shift+Tab between them; and the location chip with
`Ctrl+.`, dragging and dropping. One piece of the sketch is not built: dropping the chip on the
Bookmarks button to bookmark the folder (`Ctrl+D` does it).

**Dragging the chip copies, even on the same drive** (Shift moves). The ordinary rule — a drag within
one volume moves — was right for a selection and wrong for this: picking up where you are and dropping
it somewhere means "a copy of this, there", and moving the folder the pane is showing left the pane on
a path that no longer existed. Separately, **a tab follows a folder the application moves** — Shift
on the chip, or F6 on a folder open in the other pane — to where it landed, instead of reporting it
gone. Only when the move finished, the old path is really empty and the new one really exists: a
folder skipped at the "already there" question never moved. Underneath both: the registry and its renderers, the location facts on
`ICommandContext` (§6.6), shell icons for locations as the bookmarks popup draws them (§6.2), and
drag and drop.

As built, three details differ from the sketch below. **Opening leads every row menu as one
block** — Open in bold, then Open in new tab on a folder or Preview on a file, each offered only
where it works — rather than those two sitting in one-item groups further down. **The `..` menu is
Open, Open in new tab and Copy path**, run against a context standing on the parent entry, which
the selection never contains. And **"Open with VS Code" no longer offers itself on empty space**:
it reads `${selection}`, so it is about rows, and with nothing selected it only ever launched VS
Code with no arguments. "Open folder in VS Code" (`code ${dir}`) is the location version, in the
defaults; an existing `tools.json` gets neither change automatically.

The palette as built: the window's section is headed "This window", having no name of its own to
give. With nothing selected and no row under the cursor the selection's section is left out, and
its commands are not counted in the footer as hidden by the query — with nothing to act on they
were never on offer. The `..` row, when it has the cursor, heads its section ".. (parent folder)".

**The gap is in the model, not the menu.** The registry records *where* a command may be offered
(`CommandScope`: background, file, directory, multiple) but never *what it acts on*. Every renderer
has been guessing the second from the first. The context menu's rule — "Navigate commands only on
empty space, unless they lack the Background scope" — is such a guess, and it broke in both
directions: `Worktrees` vanished from the menu of any selected row, and when its scope was widened
to fix the palette it vanished from the empty-space menu instead whenever a selection existed,
because right-clicking empty space never cleared the selection and so the menu was built for the
selection anyway. Two bugs from one missing field.

**Every command declares a subject:**

| Subject | Acts on | Examples |
| --- | --- | --- |
| `Selection` | the selected rows | Open, Rename, Delete, Copy path, Git diff, SHA-256 |
| `Location` | the folder the pane shows, or its repository | Paste, New folder, Search here, Flatten, Bookmark, Git status, Git log, Worktrees, Open in bash, Open repository in VS Code |
| `Window` | the window itself: its tabs, panes, view and history | New tab, Two panes, Sort by…, Undo, Command palette |

`CommandScope` survives only for `Selection` commands, where it still means something (a file, a
folder, several). A `Location` command has no scope: it is about the folder whatever is selected.

For tools the subject is **inferred from what the tool reads**. A tool whose templates use only
`${dir}`, `${otherPaneDir}`, the location tokens or `${env:…}` is about the location; one that
reads `${path}`, `${name}`, `${stem}`, `${ext}`, `${selection}` or `${uri}` is about the selection.
An explicit `"subject"` in `tools.json` overrides the inference. This makes one existing default
honest: "Open in bash" is offered on a selected folder but opens `${dir}`, the folder you are
*in* — it has only ever been a location command, and a menu entry under a folder's name that
ignores the folder is a small lie. Anybody wanting "open bash *in* the selected folder" writes
`"workingDirectory": "${path}"`, and inference files it under the selection.

That same rule suggests the second VS Code default the open issue asked for: "Open folder in VS
Code" (`code ${dir}`, a location command) beside "Open repository in VS Code" (`code ${repoRoot}`,
also location, and absent outside a repository by §6.6's rule).

#### Three doors to one menu

The *This folder* menu is the `Location` commands, built by the same code wherever it opens:

```
 ┌──────────────────────────────────────┐
 │ ▣ dance_commander            ⎇ main  │   header: the folder, as its bookmark would show it
 ├──────────────────────────────────────┤
 │   Paste                       Ctrl+V │
 │   New folder                      F7 │
 ├──────────────────────────────────────┤
 │   Open in bash          Ctrl+Shift+B │
 │   Open in Windows Terminal           │
 │   Open repository in VS Code         │
 ├──────────────────────────────────────┤
 │   Git status                         │
 │   Git log                            │
 │   Worktrees                          │
 ├──────────────────────────────────────┤
 │   Search here                 Ctrl+S │
 │   Flatten this tree     Ctrl+Shift+F │
 │   Bookmark this location      Ctrl+D │
 │   Copy this folder's path            │
 └──────────────────────────────────────┘
```

The header is not clickable. It exists because a menu about a place should say which place, and
because it is the one thing that tells this menu apart from a row's at a glance.

1. **The location chip.** At the left end of the address bar, inside its border, sits the folder's
   own shell icon — the same icon its bookmark shows, which is why it does not break the "no
   icon-only toolbars" rule (§1.3): it is not a tool, it is the place, drawn the way this
   application already draws places. A network icon for a remote location; for a flattened tree
   or a search, the icon of the folder it was made from. It does three things, all of them
   what the icon beside a browser's address already does:
   - **Click or right-click** opens *This folder*, anchored under the chip.
   - **Drag it** and you are dragging the folder itself: onto the other pane to copy it, onto
     Explorer, onto the bookmarks button to bookmark it.
   - **Drop onto it** and the drop lands in this folder. Dropping files into the current folder
     used to need an empty stretch of grid to aim at, which a full folder does not have.

   The chip is the answer to "the only stand-in for the current folder is `..`". The `..` row goes
   back to meaning exactly what it says: its menu is about the *parent* — Open, Open in new tab,
   Copy path — and it stops being pressed into service as a proxy for somewhere it is not.

2. **Empty space.** Right-clicking anywhere in the grid that is not a row opens *This folder* and
   **leaves the selection alone**. Clearing it would throw away a selection that took twenty
   clicks to build for a menu that does not act on it.

3. **The last item of every row menu** is a submenu named after the folder:

   ```
    ┌──────────────────────────────┐
    │   Open                 Enter │
    ├──────────────────────────────┤
    │   Rename                  F2 │
    │   Copy                    F5 │
    │   …                          │
    ├──────────────────────────────┤
    │ ▣ In dance_commander       ▸ │ ──▶  This folder menu
    └──────────────────────────────┘
   ```

   That is the fix for "there isn't much empty space in a listing": the folder's commands are one
   hover away from any row, and they are visibly *not* the row's — they live under the folder's
   name and icon, one level down.

**From the keyboard**, `Ctrl+.` opens *This folder* at the chip. `.` is the name every shell gives
the current directory, the way `..` names its parent, so the key spells what it opens. `Shift+F10`
and the Menu key stay with the selection, as everywhere else in Windows.

#### The palette, in sections

The palette lists all three subjects, and the difference has to be visible before you press Enter,
not after. Rows are grouped under a header that names the subject:

```
 ┌────────────────────────────────────────────────────────────────┐
 │ git                                                            │
 ├────────────────────────────────────────────────────────────────┤
 │ ARCHITECTURE.md                                                │  header: the selection
 │   Git diff                                  Git                │
 │   Git log for this                          Git                │
 │   Git blame                                 Git                │
 │ ▣ dance_commander  ⎇ main                                      │  header: this folder
 │   Git status                                Git                │
 │   Git log                                   Git                │
 │   Worktrees                                 Navigate           │
 ├────────────────────────────────────────────────────────────────┤
 │ 6 commands here · 3 about the selection, 3 about this folder   │
 └────────────────────────────────────────────────────────────────┘
```

- **The headers name things, not categories.** "ARCHITECTURE.md" and "dance_commander ⎇ main"
  rather than "Selection" and "Location": the question the header answers is *what will this act
  on*, and a name answers it where a category only restates it. Several selected rows read
  "3 selected". The folder header carries the chip's icon, the same mark as in the menus.
- **Fixed order: selection, this folder, window.** Ranking happens *inside* each section, so the
  best match for "git" among the selection's commands leads that section rather than being
  interleaved with the folder's. A list whose sections moved with every keystroke would make you
  re-read the headers each time.
- **Empty sections vanish**, headers included. With nothing selected the palette starts at the
  folder.
- **Headers take no highlight**: the arrow keys step over them. `Tab` jumps to the first command of
  the next section, which is what makes the sections navigable rather than decorative.
- **Recently used rises within its own section**, not above them all: recency is a guess about
  which command, never about which subject.
- The footer keeps its count of what is not offered here (§6.15), and adds the split.

#### What this replaces

- The group-based `Belongs` filter in the context-menu builder, and its "Navigate only on empty
  space" special case: the menu becomes "the selection's commands, or the location's", with the
  subject deciding.
- The empty-space right-click that built the selection's menu.
- The brainstorm's stand-in for the current folder (PLAN.md §13). Its neighbour, the address bar
  as a drop target, turned out to mean *navigating* to a dropped folder, which the chip does not
  do — dropping on the chip copies into this folder — and stays open.

**Build order**, each step useful alone: (1) `Subject` on `CommandDefinition` with tool inference,
the menu builder switched to it, empty space opening *This folder* without touching the selection,
and the row menu's folder submenu — this alone fixes Worktrees; (2) palette sections; (3) the chip,
with `Ctrl+.`, drag and drop.

### 6.19 Places of the application's own: `about:` pages and collections

*Designed 2026-09-26. Built: all five steps.* The popups (Bookmarks, address suggestions) stay as the
quick way to jump. These pages are where the same things are *managed* and browsed with the full
listing: sorted, filtered, many selected at once.

**One scheme, the browser's.** Internal places are `about:` pages — one scheme, plainly internal,
unable to collide with a path or a remote scheme, and read as ~~about:~~computer by the muted-scheme
address bar:

| Address | What it lists |
| --- | --- |
| `about:computer` | The top of everything: drives (label, free and total space, local/removable/network), the known folders, and the pages below as entries |
| `about:bookmarks` | Bookmarks; groups are folders |
| `about:history` | Where you have been, newest first |
| `about:collections`, `about:collections/<name>` | Named collections, and one collection's contents |
| `about:shelf` | The shelf: files picked up across folders (`about:marked` still opens it) |

Up from a drive root goes to `about:computer` with the cursor on the drive, instead of stopping.
Drive details are read in the background with a timeout: a disconnected network drive can take
tens of seconds to answer, and it shows as "not responding", never as a frozen pane.

**A collection is a named set of references.** Each of these is one, with its own job:

- **Collections** — files gathered to act on together: copy, zip, hand over. Rows *are* the files
  (columns as a flattened tree: name, where it lives, size, modified), so preview, copy and the
  rest work as in `flat:` and `search:`. Stored in `collections.json`. Any location can contribute:
  local, SFTP, archive entries, script locations.
- **Bookmarks** — places you jump to. Entries carry their own label and Enter goes *to* the place:
  rows are records that point somewhere, shown with the link glyph, which says exactly that.
  `bookmarks.json` keeps its format.
- **The shelf** — Norton's marking, extended across folders: one key adds the selection, the
  footer counts it, `about:shelf` shows it, the shelf window (Win+Shift+B) shows it above every
  other program, and an operation takes it all. **Session only**, like marks always were; "Save as
  collection" keeps it when it is worth keeping. One thing with one name: until 2026-10-29 it was
  the basket, its page `about:marked` (still accepted) and only the window the shelf.

**Four ways of gathering, and why each exists.** They differ in *what* they hold and *how long*:

| | Holds | Lasts | Answers |
|---|---|---|---|
| Marks | rows in the folder on screen | until you leave, Esc, or an operation | "which of these do I mean now?" |
| Shelf | files from many folders | this session | "what have I picked up on the way?" |
| Collections | files from anywhere, by name | saved | "which files belong together?" |
| Bookmarks | places, with a label | saved | "where do I go often?" |

- *Marks:* "copy these seven files from this folder to the other pane" — Insert/Space, F5, done.
- *Shelf:* "put together what goes to the customer: a log here, two configs there, a screenshot
  from Downloads" — Ctrl+B wherever you find something, then act on it all from `about:shelf`.
  Marks that survive leaving the folder; unsaved because it is work in progress, F7 when it is not.
- *Collections:* "the files for the release checklist, the same set every release" — a named set
  that lasts and follows our moves; Del takes a file out of the set, never off the disk.
- *Bookmarks:* "the build server's log folder, my worktrees" — a bookmark goes *somewhere*; you open
  it rather than act on it, which is what the link glyph says.

The distinctions: a place against files (bookmarks against the rest); how long (one operation, one
session, until deleted — each step up a deliberate act, Ctrl+B then F7); one folder against many
(only marks are tied to one).

**Concepts cost attention; this set is on trial.** Every adjacent concept is a decision the user has
to make, and too many near-neighbours is strain rather than power. The blurred edges are known:

- *Shelf against collection* is the closest pair — the shelf is an unnamed, unsaved collection,
  justified only by Ctrl+B asking nothing. If that difference does not earn its keep, the shelf
  becomes a collection that empties itself at exit (SHELF_PLAN.md, Plan D).
- *A collection of folders against a bookmark group* differ in intent (act on these / take me
  there) more than in mechanics.
- *Tags* (not now) would add visibility on the rows, not a new way of gathering.

Decided 2026-09-27: dogfood the four as built and prune by use, not by argument. Notes on which
of them get used, and which get confused with each other, belong in PLAN.md.

**`Del` edits the list.** In any collection `Del` removes the entry and never touches the file;
history is a log rather than a collection, and `Del` there removes the visit. Deleting a file
itself means going to where it lives (Ctrl+Shift+Enter). F2 renames what the row shows: a
bookmark's label, a collection member's file — and the collection follows.

**References are paths, so they are kept true.** Every move and rename the application makes is
already a reified operation (§1.2/6), so collections and bookmarks update their references from
it. A file moved outside the application shows as "not found" rather than silently disappearing,
as the worktree listing does (§6.12).

**Capabilities per page, not per provider.** §"commands say where they apply" asks a provider what
it can do; these pages differ from one another — bookmarks rename and delete, history only
deletes, `about:computer` neither — so the question gains a location: what can be done *here*.

**Not now: tags.** macOS-style tags are collections that also show on their members' rows, in a
colour the user picks (the user owns colour, DESIGN.md §1.3). The design leaves room for them — a
membership lookup per row, a colour per collection — and nothing else depends on them. Stored as
path references they break on moves made outside the application; NTFS alternate data streams
would survive those but not OneDrive, shares or other file systems, so that is an option for
later at most.

**As built (step 2).** `collections.json` holds `{ name, items: [address…] }`; `about:collections`
lists them (Items column) and `about:collections/<name>` lists the members as the files themselves,
read where they live in parallel with a deadline, with a Where column and a Note that says "not
found" or "not responding". Ctrl+Shift+A adds the selection (proposing the collection last added
to); a drop onto a collection's page or its row adds, with the link cursor. On the list, F7 makes a
collection and Del deletes one (asking when it has members; the files are never touched). In a
collection, Del removes members and Ctrl+Z puts them back; F2, F5, F6 and F3 act on the files.
*Where a key means something else* is a command with `OnlyAt`, which takes the key only at those
locations, over the general command; *what applies on a page* is `AboutPages.HoldsFiles` — rows
that are files versus rows that are places — plus `DeletesEntries` for Delete. References follow
our own moves through `IRelocationFeed`, reported by rename, group-into-folder, transfers and their
undos; a folder moved by a transfer is reported as itself as well as its files.

**As built (step 3).** `about:bookmarks` lists the ungrouped bookmarks and a folder per group
(`about:bookmarks/<group>`). A row is labelled with the bookmark's name, its address is the place
(so Enter goes there, and a drop onto it lands there) and it carries the link glyph. It is a page of
places: no general command that reads or changes the row applies, since that would reach the folder.
Its own commands: F2 renames a label or a group, F6 moves the selection to a group (empty takes it
out), Del removes bookmarks — a group's row removes the group's, after asking — and Ctrl+Z restores
them. A drop onto the page bookmarks what is dropped into the group shown. Bookmarks follow our own
moves like collection members. The Bookmarks popup gains "Manage bookmarks", which opens the page.

**As built (step 4).** `about:history` lists the recent places with a Visited column, newest first.
`history.json` gained a `visited` map beside the `recent` list (older files still read; places
visited before it have no time and sort after the rest, A–Z). Del forgets the selected visits and
Ctrl+Z puts them back where they stood. Ties in any sort now read A–Z whichever way the column runs.

**As built (step 5).** `IShelf` is an in-memory set of addresses that follows our moves. Ctrl+B adds
the selection from anywhere (Flatten moved to Ctrl+Shift+F); the foot of the window counts it
("Shelf 3", a click opens it); `about:shelf` lists the files as a collection page does, where Del
takes them out (undoable), F7 saves the shelf as a collection (or adds it to one), and "Empty
shelf" is in the menu. A drop onto the page adds. `ShelfWindow` is the same list in an
always-on-top window. Marking itself is the commander's: Insert and Space mark and move on, arrows
keep marks, Esc unmarks.

**Build order**, each useful alone: (1) `about:computer` and Up from a drive root; (2) the
collection core — storage, `about:collections`, add and remove, drop to add, references following
our own moves; (3) `about:bookmarks`; (4) `about:history`; (5) the shelf.

### 6.8 SFTP provider

- `SSH.NET` `SftpClient`, one connection per `{host, port, user}` in a pool with idle
  timeout (default 5 min) and keepalive; reconnect transparently on `SshConnectionException`
  once, then surface the error on the pane's inline error bar.
- Auth order: ssh-agent (Pageant/OpenSSH via named pipe) → private key from config →
  password from the credential store → interactive prompt (with "save" checkbox).
- **Host key verification is mandatory**: `known_hosts`-compatible store under
  `%APPDATA%/DanceCommander/known_hosts`; unknown key → TOFU dialog showing the SHA-256
  fingerprint; changed key → hard block with an explicit override. `HostKeyReceived` must
  never be blanket-accepted.
- Listing maps `SftpFile` → `FsEntry`, filters `.`/`..`, resolves symlink targets lazily
  (a `LSTAT`-only listing, follow on demand — resolving every link makes big dirs crawl).
- Reads use `SftpFileStream` (seekable → archives inside SFTP work). Writes stream with a
  temp `.part` name + rename on commit; a file being replaced is renamed aside first and
  deleted only once the new one has its name (§4.2).
- Permissions/owner exposed as extra grid columns.

### 6.9 WebDAV provider

`dav://` is HTTP and `davs://` is TLS — two registrations of one implementation, because the
registry resolves by scheme and no port settles which transport a server wants. That is the
spelling every other client uses.

- **List** = `PROPFIND` `Depth: 1` for `displayname, getcontentlength, getlastmodified,
  creationdate, resourcetype, getetag`. **Stat** is the same at `Depth: 0`.
- **Write** = `PUT` streamed through a pipe so an upload of any size is never held in memory,
  `MKCOL`, `DELETE`, and `MOVE` with a `Destination` header — server-side, so renaming a gigabyte
  costs one request rather than a download and an upload. The `PUT` goes to `name.part` and is
  `MOVE`d into place on commit, the old file stepping aside as on SFTP. `DELETE` on a collection
  is always recursive in WebDAV, so a non-recursive delete checks with `PROPFIND Depth: 1` that the
  folder is empty first.
- **Authentication is discovered from the request that matters, not from a probe.** A 401 on the
  actual request is the signal; it is answered and the request retried. An earlier design probed
  `PROPFIND /` at connect time and was wrong against Nextcloud, where the DAV area is a sub-path
  and the site root answers 405 — the probe concluded "anonymous" for a server that requires a
  password. Where the DAV area lives is the server's business. An anonymous share is still never
  prompted for, because its first request simply succeeds. A `CredentialCache` holding the
  Windows account for the server's own address, and only for an intranet name (dotless, or in
  this computer's domain), covers the IIS/SharePoint case where Negotiate or NTLM means there is
  nothing to type. Neither it nor a password goes to a `dav://` server unless
  `remote.unencryptedSignIn` is on (loopback excepted): a dotless name is whoever answers for it
  on a strange network, and only TLS proves otherwise (BUGS.md 54). Each server's client records
  whether it was made to sign in, and is replaced when the setting no longer agrees.
- **`dav://` to a server that redirects to HTTPS is refused, not followed.** .NET strips the
  Authorization header across a change of scheme — correctly, since sending a password to a
  different origin than it was typed for is how credentials leak — so following the redirect
  produces a request that is refused forever with nothing to say why. The error names the `davs://`
  address that would have worked.
- **The `Multi-Status` parser is written by hand**, not taken from a library, because it is the
  whole of what the provider needs and it is where the interoperability problems live. Two are
  visible in the first response any server sends: the namespace *prefix* is arbitrary (the
  specification says `D:`, wsgidav says `ns0:`) so everything matches on the `DAV:` namespace
  instead; and hrefs are percent-encoded and only sometimes absolute, so `/a%20file.txt` and
  `http://host/a%20file.txt` both have to yield `a file.txt`.
- **URLs are escaped segment by segment.** Escaping a whole path turns every `/` into `%2F` and
  asks the server for one file with slashes in its name.
- HTTP statuses are translated into the exception types the rest of the application already
  understands — 404 to `FileNotFoundException`, 423 to "locked by someone else", 507 to "the
  server has no room". Otherwise every failure reaches the pane as "Response status code does not
  indicate success: 507", which is an accurate way of telling somebody nothing.
- **Search is asked of Nextcloud, not walked** (`ISearchingProvider`, `NextcloudSearch`). A walk is
  one PROPFIND per folder in a row; under `/remote.php/dav/files/<user>/` a `search:` sends one
  RFC 5323 `SEARCH` to the DAV root instead, scope the folder, depth infinity, a `like` on the
  display name for each run of plain text in the query. The patterns are looser than the query,
  never stricter (wildcards, `[…]`, `{a,b}` and backslashes are dropped, not translated), and the
  answer goes through `NameMatcher` again, so results do not depend on who answered. Finds inside
  hidden folders are dropped and counted unless Ctrl+H; a find whose address does not end in its
  name (a `\` or `..` in it) is no row. Any other server, any answer but a Multi-Status (405, 501,
  400, 403, a proxy's 200), and an answer with any find outside the folder, are walked as before;
  only a refused password is an error. Nextcloud sends 100 when not told, so the answer is asked
  for in pages of 500, ordered by `oc:fileid` and each asking for the numbers after the last one
  seen (`gt`), until one comes back empty: a short page is not the end, since the server may cap
  it. By number, not by offset, because a file removed between pages shifts an offset and hides
  the row at the edge, and a name has ties. The numbers are read back, and one missing or not
  rising sends the search back to the walk, as does a page refused part way.
  The answer is Nextcloud's file index: files put on its disk behind its back or on an unscanned
  external storage are missing until it scans them.
- **No `Watch`**: HTTP has no change notification, so the pane falls back to `Ctrl+R`.
- **`RandomAccessRead` is not claimed yet.** Servers that answer `Range` requests would allow a
  seekable stream, and browsing a zip without downloading it is what that buys — but a capability
  is a promise the transfer service and the archive provider act on, so it stays unclaimed until
  the stream exists.

**Testing.** `scripts/webdav-test-server.ps1` runs wsgidav on localhost from a throwaway virtual
environment under `temp/`, seeded with the cases that break providers rather than with lorem ipsum:
a name with spaces, a non-ASCII name, a file large enough for a Range request to mean something,
and a zip. It requires a password by default, because authentication is part of what needs testing
and a provider only ever tried anonymously fails on first real use. It is not a second
implementation, though — WebDAV servers disagree with each other far more than the specification
suggests, so anything that works here should still be tried against Nextcloud or IIS before it is
trusted.

### 6.10 Archive provider

- Opening `…/x.zip` as a directory: ask the registry for the backing stream; if seekable use
  `SharpCompress`'s `ZipArchive`/`SevenZipArchive` random access; otherwise copy to a temp
  file first (tar.gz, rar in solid mode, non-seekable sources).
- Entry list is flattened by the library; we build an in-memory directory tree once per open
  archive and cache it (keyed by path + size + mtime), so navigating inside a zip is instant.
- Open archives are reference-counted and disposed when no tab references them, with a
  30 s grace period so back/forward doesn't reopen.
- Read-only in v1 (`Read | RandomAccessRead`). Writing into an archive means repacking; for
  zip we can add `Write` in M7 (SharpCompress supports rewriting), other formats stay
  read-only. Extracting *out* of an archive is just the transfer service, which works day one.
- Nested archives work via `FsUri.Inner` with no extra code: the inner archive's backing
  stream is the outer archive's entry stream (temp-materialised if non-seekable).

---

## 7. Configuration & data

All under `%APPDATA%/DanceCommander/`:

| File | Contents |
| --- | --- |
| `settings.json` | choices: scheme, density, text size, folders first, the command line's shell, keys chosen over the commands' own, tool packs switched off |
| `bookmarks.json` | bookmark tree |
| `history.json` | global MRU |
| `tools.json` | custom tools (§6.6) |
| `highlighting.json` | highlight rules (§6.4) |
| `connections.json` | SFTP/WebDAV endpoints — **no secrets** |
| `known_hosts` | SSH host keys |
| `session.json` | open tabs/panes for restore — in `%LOCALAPPDATA%`, not here: it describes this machine (WORKSPACES_PLAN §4) |
| `packs.json` | which tool packs' programs were found, a cache — in `%LOCALAPPDATA%` for the same reason (§6.6) |

- All JSON is JSONC (comments + trailing commas allowed) because these files are meant to be
  hand-edited. Ship a JSON Schema for each and a `$schema` line so VS Code gives completion.
- `IConfigStore<T>` watches each file and hot-reloads on change (debounced, with validation;
  a broken file logs an error and keeps the previous good version rather than falling back to
  defaults and silently losing the user's rules).
- **A file that fails to parse is said in the pane**, with the line and column, not only in the log.
  At startup there is no earlier version to keep, so it loads as empty — and is **never written
  over with defaults**: defaults are seeded only when the file is missing, because an unparseable
  file also comes out empty and seeding on that once replaced a hand-edited `tools.json` with the
  shipped one. Anything else that saves over a broken file (adding a bookmark) copies it aside
  first, as `name.broken-<timestamp>.json`.
- **New tools are offered, once.** `tools.json` is written on first run and never again by the
  application, so a tool added to the defaults later never reached anyone who already had the
  file. At startup, tools a *pack* has that the file does not mention by id are offered in the
  pane — *Add*, *Add, turned off*, *Not now*. Accepted ones are appended; declined ones are
  appended with `"enabled": false`, which keeps them out of the menus and out of the question.
  The file is therefore the whole record of what has been offered, with nothing hidden to fall
  out of step with it. The shipped defaults are the only pack today (`ToolPack`); script packs
  (§6.16) are meant to arrive the same way. Appending is a **text edit** that finds the end of the
  `tools` array and leaves the rest of the file byte for byte — saving the parsed object would
  have dropped every comment in a hand-edited file to add three entries. Nothing is offered while
  the file has an error in it.
- **`settings.json` is sparse and described once.** It holds only what differs from the
  defaults, so a better default reaches everyone who never chose otherwise; a choice set back to
  its default is taken out of the file. Every setting is a `SettingDefinition` in
  `SettingsCatalog` (path in the file, title, description, choices, default), and the settings
  window (Ctrl+,) and the palette's "Rows: Compact"-style entries are both renderers over it, as
  the legend is over the command registry. `AppliedSettings` puts it into effect live: density and
  text size override the size tokens at the application's top level, and `keys` goes to the
  registry. Writing is a **text edit** (`JsoncEdit`: set or remove one property by path, editing
  only its span, creating missing parent objects, dropping emptied ones), so comments survive; a
  file that doesn't parse is refused, never rewritten.
- **Keys chosen in settings.json** (`"keys": { "dc.search": "Ctrl+Shift+S" }`, null for none) win
  over every declared key. `CommandDefinition.Binding` is the key in force and `DefaultBinding`
  the one it was declared with; the registry rebuilds its index from both on every registration,
  so a tool reloading from tools.json gets its chosen key again and cannot take back one chosen
  for another command. The command that lost its key shows none, and `Taken` says who has it.
- Writes are atomic (temp file + `File.Replace`).
- **Secrets** go to Windows Credential Manager (`CredRead`/`CredWrite`, target
  `DanceCommander:sftp:user@host`). Never in the JSON, never in logs, never in history.
  - *Planned (§6.16):* script secrets are keyed by a name the config chooses rather than by an
    endpoint, since a script's service is not a host we connect to —
    `DanceCommander:secret:<name>` for a `"secrets": ["gitlab-token"]` declaration. They reach
    the child process in its environment, never in argv.

---

## 8. Keyboard model

Norton/Total Commander muscle memory where it doesn't fight Explorer:

`Tab` switch pane · `Enter` open · `Backspace` up · `Alt+←/→` back/forward ·
`F2` rename · `F5` copy to other pane · `F6` move to other pane · `F7` new folder ·
`Del` delete (Recycle Bin), `Shift+Del` permanent · `Ctrl+T`/`Ctrl+W` tab · `Ctrl+L` address ·
typing filters · `Ctrl+H` show/hide hidden entries · `Ctrl+1`/`Ctrl+2` one pane / two panes ·
`Ctrl+D` bookmark · `Ctrl+Shift+G` group into folder · `Ins`/`Space` toggle selection ·
`Ctrl+R` refresh · `Ctrl+Z` undo · `PgUp`/`PgDn` move the cursor a page ·
`Ctrl+PgUp`/`Ctrl+PgDn`/`Ctrl+Tab` step through tabs ·
`Ctrl+Shift+PgUp`/`Ctrl+Shift+PgDn` move a tab along its strip · `Ctrl+Alt+←/→` move it to that pane ·
`Ctrl+F3`/`Ctrl+F4`/`Ctrl+F5`/`Ctrl+F6` sort by name / type / date / size, `Ctrl+F7` by relevance in a search ·
`Ctrl+Shift+C` copy path · `Ctrl+Shift+Enter` open containing folder ·
`Ctrl+Shift+P` command palette · `Ctrl+.` this folder's commands · `Ctrl+E` command line ·
`F3` preview · `Ctrl+I`/`Ctrl+Shift+A` invert / unmark all · `Ctrl+Shift+H`/`Ctrl+Shift+O` history / bookmarks ·
`Alt+Home` this computer · `Esc` dismiss, one thing at a time ·
`Ctrl+Shift+F` flatten · `Ctrl+B` add to shelf · `Ctrl+S` search ·
`Win+Shift+B` show or hide the shelf window, from any program (a system-wide key). Keys are not rebindable yet: only a tool in `tools.json` can claim one
(`keybinding`), taking it from a built-in; each takeover is logged at startup.

**Sorting is a keystroke, not a header click.** The column headers work and always did, but a
header is a mouse target, and in a keyboard-first application that made sorting the one common
operation you had to reach for the mouse to do. `Ctrl+F3`–`F6` are Total Commander's, so anyone
arriving from there already knows them, and they need no documentation at all: holding `Ctrl` turns
the F-key legend into exactly that list. Pressing the same one again reverses it, the way clicking
the same header twice does.

This is also what let the `Ext` column go. It existed so that sorting by type was possible and
spent 66 px of a half-width pane doing it, while the extension it displayed was already on screen
at the end of every filename. Sorting by something with no column of its own leaves nothing to put
an arrow on, so the name header says it instead: `Name (by type) ↑`. A listing in an order that
nothing on screen explains looks like a listing in no order at all.

**A binding taken over by a later registration is recorded** (`ICommandRegistry.Displaced`) and
logged at startup. Last-one-wins is deliberate — it is how a tool in `tools.json` overrides a
built-in — but the same rule lets two built-ins collide by accident, which is silent, survives the
tests, and is only ever discovered by pressing the key and getting the wrong command.

**One pane and two panes are the same app, not two modes.** Single-pane shows whichever pane is
active, so `Tab` swaps which one is on screen rather than focusing something invisible. When
watchers arrive in M2, a hidden pane must release its watcher and re-sync on return.

---

## 9. Testing

- **Core** (pure): `FsUri` parse/round-trip incl. nesting, highlight matcher semantics,
  tool placeholder expansion (esp. argv quoting), conflict resolution, undo.
- **Local provider**: temp-directory integration tests; watcher tests driving real file
  churn and asserting coalescing + resync-on-overflow.
- **Archive provider**: committed fixture archives (zip/tar.gz/7z, nested, unicode names,
  path traversal `../` entries — which must be rejected).
- **SFTP/WebDAV**: contract tests written against `IFileSystemProvider` run over Docker
  containers (`atmoz/sftp`, `bytemark/webdav`), marked as an integration collection so the
  default `dotnet test` stays fast and offline. Docker Desktop is available on this machine.
- **UI**: viewmodel-level tests only (no UI automation). The views stay logic-free enough
  that this is sufficient.


---

## 11. Principal risks

| Risk | Mitigation |
| --- | --- |
| WPF grid perf on huge directories | `ListView`+`GridView` with recycling, batched insertion, sort-then-assign. Benchmark against a 200k-file dir at M1 — it's a load-bearing assumption, not a detail. |
| `FileSystemWatcher` misses events under churn | Resync-on-`Error`, debounce, periodic reconcile when the window regains focus. |
| Long paths (>260) | `<LongPathAware>` in the manifest + `\\?\` prefixing in the local provider from day one; retrofitting is painful. |
| Blocking UI on slow remotes | Every provider call async + cancellable; navigation cancels the previous listing; inline (non-modal) error bar per pane. |
| Tool config as an injection vector | argv arrays only, no shell string interpolation; tools run as the user with no elevation path. |
| Archive path traversal | Reject/normalise `..` entries before showing or extracting. |
| Scope creep in git support | Branch-only in M5; anything more goes behind a decorator and a flag. |

