# Dance Commander — Design

What the app is for, the commitments it is built on, and what it declines. Read this before any
feature or UI work. [ROADMAP.md](ROADMAP.md) says what is next and [WISHLIST.md](WISHLIST.md)
collects ideas; [ARCHITECTURE.md](ARCHITECTURE.md) is the reference for how things are built.
Code comments cite this file's sections as §1.1–§1.6.

## 1.1 What this is

A file manager from the era before file management was something an OS tried to hide from you,
rebuilt with today's ergonomics: two panes, keyboard first, browser-like navigation, live
folders, git awareness, and tools and highlights driven by your own config.

- **Windows only;** cross-platform is not a goal. Win32 and Shell dependencies are taken freely. It should feel native while
  understanding the cross-platform world developers work in: line endings, dotfiles, servers.
- **Few backends:** local disk, SFTP, WebDAV and archive contents. Network shares work as far as
  Windows maps them. Not a general FTP, S3 or cloud client.
- **We only write features we use**, because those are the ones we can test. What hasn't been
  used for real ships labelled *untried*, and nothing that writes files ships untried.
- **Not for everyone, on purpose.** When a choice is between serving one more audience and
  staying sharp for developers who live on the keyboard, it stays sharp. Performance isn't traded
  for minor features.
- **Calm and powerful**, understated until the power is needed.
  The best features stay out of the way until you reach for them. Fun as in the feeling of
  power, not as a joke. Not Corporate Memphis.
- **Fast, and when it can't be fast it still feels fast:** not lying, but hiding latency and
  helping you orient. Navigation speed matters more than copy speed.

## 1.2 Structural commitments

Getting one of these wrong is a rewrite, not a patch.

1. **One command registry.** Every action, built in or from `tools.json`, is a
   `CommandDefinition`. The F-key legend, context menus, key bindings and the palette are
   renderers over it. No ad-hoc `[RelayCommand]`s on viewmodels.
2. **No modal dialogs. Ever.** A decision (conflict, credentials, host key, rename) is an async
   request rendered inline and awaited; you can ignore it and keep browsing. Banned at build time.
3. **Everything is a location.** Search results, a flattened tree, a script's listing, the app's
   own pages: each is a pane of rows behind a URI scheme, with sorting, marks, tools and
   bookmarks for free.
4. **Listings own their columns.** SFTP adds permissions, search adds "Matched in"; the grid
   binds to the listing's column set.
5. **View state is per location.** Sort, columns, scroll and selection are saved on leave and
   restored on arrival.
6. **Operations are reified.** Every mutation carries a description, an undo and its shell
   equivalent. The app can always tell you exactly what it did to your files.
7. **Input surfaces are designed once.** The address bar navigates; the command line executes in
   the current folder. Focus routing is settled once for every view.
8. **Design tokens, not per-view styling.** Spacing, type and colour come from `Theme/`.

## 1.3 Visual direction: dense but approachable

Density is the goal; clutter is what gets mistaken for it. Clarity comes from hierarchy and
spacing, not from removing information.

- **Near-monochrome chrome.** You own colour, through highlight rules. The app keeps one accent
  for focus and selection, red and amber for errors and warnings, and greys.
- **Space and elevation, not borders.** No 1px outline around every region.
- **Type:** Segoe UI Variable, tabular figures in number columns, monospace only where it means
  something. At most three type sizes.
- **Comfortable by default, dense on request:** ~24px rows, ~18px compact.
- **No icon toolbars.** Text or nothing, except the browser's own vocabulary: back, forward, up,
  bookmarks. Icons belong in the grid and in bookmarks.
- **The F-key bar is a legend, not a toolbar.** Hold a modifier and it shows that whole layer of
  the keymap; it teaches the app without a tooltip.
- **Five persistent chrome regions:** pane toolbar, tab strip, grid, pane footer, F-key legend.
  Everything else is transient. New permanent space comes out of one of the five.
- **A pane describes its location; the window describes its operations.** Item count, watching,
  branch: the pane's footer. Jobs and the activity log: the window's.
- **Nothing is stated twice**, and the title bar and scrollbars belong to the palette.
- **Filtering is declared, never silent.** When rows are hidden, the pane says how many and which
  key brings them back. **Defaults are never invisible** either.
- **Plain language, with the next action.** Errors say what happened and what to do (Retry, Skip,
  Open log), and name the cause when it can be found: "in use by a PowerShell tab in Windows
  Terminal", not "the file is in use".
- **Activity says what it waits on:** a verb and, when known, a count. Never a bare "Loading…".
- **Completion is boring. Red is rare.** No celebration for what you watched happen; red is for
  failures and what can't be taken back.
- **Nothing moves under the user's hand.**
- **Light and dark are both first class**, both WCAG AA, following the Windows setting.

## 1.4 Declined on purpose

These are not gaps. Proposing one again needs a new argument.

- **No WebView preview, hex view or `IPreviewHandler`.** Each is a second rendering, theming and
  keyboard surface. Rich formats open in the application that owns them.
- **No theming engine, plugin marketplace, forty-format preview, cloud accounts or companion
  apps.** Each is an "everything for everyone" move.
- **No C# plugin DLLs.** Script locations give extensions a process boundary instead.
- **No built-in knowledge of any external service.** A script owns each integration (GitLab,
  issue trackers, …); the app owns listing, navigation, preview and copy.
- **No telemetry.** Usage counts and friction notes stay on the machine; sharing them is the
  user's act.
- **No tips, skeleton placeholders or motion on every keystroke.** The legend already teaches the
  keys; streamed rows beat shimmer; a gliding cursor costs speed at 26k rows.

## 1.5 Budgets

"Feels fast" is kept by numbers. They live in `Services/Budgets.cs`; every timing line in the log
ends with `within budget` or `over budget: …`. Changing a number is a product decision.

| Moment | Budget |
| --- | --- |
| Launch: placeholder window | 300 ms |
| Launch: both panes' rows on screen | 1500 ms |
| Local folder: rows on screen | 250 ms |
| Anywhere else (SFTP, WebDAV, archives, search, scripts) | 1000 ms |
| Decoration (branch, highlights, icons) after the rows | 1000 ms |
| Context menu, command palette, quick filter | 100 ms |

Copy and transfer throughput are not budgeted, on purpose (§1.1).

## 1.6 Files are never lost

The one promise that outranks speed. **No step removes the last copy of anything.**

- Nothing is opened for writing at a name that holds content the user may want. New content is
  written beside the target, checked and swapped in.
- What it replaces goes to the Recycle Bin, or aside under a recognisable name until the swap has
  succeeded where there is no bin.
- A move's source is removed only once its copy is known to be whole.
- A cancel, failure, crash or pulled cable at any point leaves every byte somewhere the user can
  find it: at the source, the target or the Recycle Bin.
- Permanent deletion happens only when asked for exactly (Shift+Del), never as clean-up.

The build bans the deleting and overwriting APIs (`BannedSymbols.Files.txt`). Each use that has to
stay is marked `// §1.6: why it can't lose anything`, and changes to anything that touches files
get an independent review before they are called done.
