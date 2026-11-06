# Dance Commander — Roadmap

Where it is going, in broad strokes. Ideas without a place yet are in [WISHLIST.md](WISHLIST.md);
the principles everything here answers to are in [DESIGN.md](DESIGN.md).

## 0.9 — a preview

Safe with your files, installable, and a fair picture of where it is going. Not feature complete.
Everything that writes, moves, deletes or undoes files has been checked in the running app, as has
everything that turns text into a command line. Features nobody has used for real are marked
*untried* in the [user's README](../USER_README.md).

## 1.0

- **Feedback from the first users**, as friction notes (*Note friction*, kept locally and shared by
  hand) and from watching people use it.
- **Untried features tried, or left out**, especially the ones that write files.
- **A gentler end for unexpected errors** where nothing was being written: the error shown in the
  pane and the window kept.
- **Distribution:** winget or Scoop besides the GitHub release, and a signed build (SignPath for
  open source, once there is a release to point at).

## After 1.0

Roughly in order of pull:

- **Jobs as a location** (`about:jobs`): a scheduler, pause and resume, retrying one item, and a
  journal written ahead of a multi-step operation so a crash mid-move is offered back on the next
  launch.
- **Remote listings, remembered and fetched ahead:** SFTP and WebDAV folders shown at once from
  memory and refreshed behind, across sessions, never acted on until confirmed.
- **Settings, the rest:** Explorer verbs, shell extensions, saved connections, custom schemes.
- **Sessions and workspaces:** several windows restored, a tab torn off into a window of its
  own, named layouts, the folders open in Explorer brought in, and the taskbar jump list.
- **The shelf and collections:** a place to stick things, files or not, while you decide where
  they belong.
- **Visibility levels** in place of plain highlight colours: hidden, normal, highlighted, per
  folder; git-ignored entries dimmed, never hidden.
- **The text preview as an editor:** a caret and selection in F3, and small files edited in
  place behind F4, remote ones too.
- **The command line, further:** a long-running PowerShell, `sudo` on a server, placeholder help.
- **A git time machine:** versions of a file and the tree at a commit, browsed and copied out like
  an archive. It reads history and never makes it.
- **The preview for a folder of photos:** next and previous without leaving the preview, turning
  by hand, metadata on request.
