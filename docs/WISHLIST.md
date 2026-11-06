# Dance Commander — Wish list

Ideas without a place on the [roadmap](ROADMAP.md) yet. Being here is not a promise: each one is
weighed against [DESIGN.md](DESIGN.md) first, and what was declined on purpose is in its §1.4.

## Small

- Ctrl+Shift+T reopens a closed tab. A tab menu: close, close others, rename, pin (a pinned tab
  opens a new tab instead of navigating away).
- Ctrl+S with a quick filter active carries the filter text into the search.
- New file and Duplicate as plain commands; a graphical way to add a bookmark.
- "Ungroup" a folder: its contents moved up and the folder removed, name conflicts asked about.
  Mostly for archives that hold a single folder; a "smart extract" may be the better answer.
- An optional "Today" / "Yesterday" for dates, keeping the columns aligned.
- A distinct icon for empty folders, if it costs nothing.
- `${otherPaneSelection}` for tools: the other pane's marked items, one argument each. A
  misspelt placeholder in `tools.json` should be named rather than expand to nothing.
- An administrator shell command.
- Run on a downloaded script that PowerShell refuses: name the cause and offer Unblock, never a
  quiet `-ExecutionPolicy Bypass`.
- Copying text out of command output by selecting it.
- Finished jobs say when they finished.

## Larger

- **Search that starts before the word is typed:** the walk begins at the first sign of intent,
  so the search filters what is already walked and a second search is instant.
- **Search inside files** (grep), working with the flat view for a recursive search.
- **Search on an SFTP server with `find`** instead of one readdir per folder, falling back to the
  walk where exec isn't allowed.
- **Redo** (Ctrl+Y), which needs operations that can go forward as well as back.
- **Look inside:** open any file as a folder if there is an archive in it (.docx, self-extracting
  .exe).
- **Comparing both sides of a conflict** before choosing: preview or metadata of each.
- **Free space and quota**, said before a copy that won't fit, and in the footer only when low.
- **Overlapping a file's commit with the next file's upload** on servers that join chunked
  uploads slowly.
- **WSL locations**, and mounting ISO files / ejecting removable media.
- **Git worktrees and branches** in bookmarks and address-bar completion; bookmarks that scale
  past a few dozen.
- **A zip of only the git-tracked files.**
- **Status badges from scripts:** a script location (for a merge request or an issue tracker)
  contributing an asynchronous badge to the pane.
- **A master password** for remembered server passwords, with cached listings encrypted under it.
- **The shelf, further:** opening what is on it, browser drops of images, links and text, calling
  it up by dragging to the screen edge, and dragging remote entries out to other programs.
- **Custom columns from tools:** a tool that prints a value becomes a column.
- **A docked output pane** for tools that capture output.

## Probably not

These cross a line from DESIGN.md, or cost more than they would be used. Here so they aren't
proposed as new.

- **Directory compare and sync.** It needs a comparison engine and wouldn't be built just to tick a
  box.
- **A multi-rename tool** with regex and counters, for the same reason.
- **Branch switching and other git actions:** that's a git client. They belong in `tools.json`.
- **Writing into archives:** zip only, if ever.
- **Elevating an operation** through a broker process. It is security-sensitive.
- **More providers** (FTP, S3, phones): only on real demand.
- **A quick view that follows the cursor:** it reads every file you pass, which is costly on a
  server.
