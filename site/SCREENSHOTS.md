# Screenshots for the site

Each page has slots for these. Until a file exists, the page shows a dashed placeholder naming it;
save a PNG under `site/screenshots/` with the name below and it appears, no HTML to touch.

Take them from the real window, in two-pane mode unless noted, at 100% scaling, and crop to the
window (or to the pane, where that is what the shot is about). Use neutral content: no customer
names, no company paths, no real hosts or tokens. A throwaway repository such as `C:\src\orbit`
and a test server read better than a home folder.

| File | Page | What it should show |
| --- | --- | --- |
| `window-tour.png` | Getting started | The whole window at rest: a repository on the left with tabs, something else on the right, legend at the bottom |
| `legend-ctrl.png` | Getting started | The bottom of the window while Ctrl is held: the legend showing the Ctrl layer (crop to the legend and a little of the panes) |
| `inline-question.png` | Getting started | An inline question in one pane, e.g. "… is in use" naming the program, with Retry and Cancel |
| `settings.png` | Getting started | Settings (Ctrl+,) on General or Appearance: the four pages, the search box, one setting changed from its default |
| `palette.png` | Keyboard | The command palette open, sections visible (selection, this folder, window), keys at the right |
| `address-completion.png` | Getting around | The address bar mid-typing, completion selected after the caret, suggestion list open |
| `quick-filter.png` | Getting around | A quick filter running ("mon d" or similar), the filter bar with "Esc to clear", rows narrowed |
| `about-computer.png` | Getting around | `about:computer` with drives (free space) and known folders |
| `back-menu.png` | Getting around | The Back button held down, listing the places behind |
| `everywhere-search.png` | Getting around | A word searched from the address bar: nearby matches first, the Where column showing |
| `conflict-question.png` | Working with files | The "already exists" question during a copy: Keep both / Replace / Skip / Cancel |
| `activity-log.png` | Working with files | The Activity popup with a few operations and their shell equivalents |
| `preview-text.png` | Working with files | F3 on a text file with tabs and mixed line endings: header with encoding and the CRLF/LF counts |
| `in-use-question.png` | Working with files | Del on a folder a terminal is standing in: the question naming the program, with Retry |
| `close-with-jobs.png` | Working with files | Closing the window during a long copy: Keep open / Close when finished / Cancel and close |
| `sftp-hostkey.png` | Servers and archives | Connecting to a new SFTP host: the host key question with its SHA-256 fingerprint |
| `archive-inside.png` | Servers and archives | A pane inside a zip: the `…zip!/…` address and its rows |
| `shelf.png` | Marks, shelf, collections | `about:shelf` with files from several folders, the Where column, "Shelf N" at the foot |
| `git-status.png` | Git | A repository with M / A / ? letters on files and on the folders above them, branch in the footer |
| `worktrees.png` | Git | The Worktrees listing with several worktrees and their branches |
| `job-strip.png` | Tools | A tool running as a job: output streaming in the pane, the job in the strip with Stop |
| `command-line.png` | Tools | Ctrl+E with a command typed, or its output shown in the pane |
| `settings-tools.png` | Tools | The Tools page of Settings: an on/off switch per program, ideally one "not found on this computer" |
| `needs-terminal.png` | Tools | Typing `vim notes.txt` on the command line: the inline "needs a terminal" question with Open in Windows Terminal |
| `pipeline.png` | Landing page, Listings and scripts | A script location (GitLab pipelines): a pipeline's jobs, or a job's artifacts.zip and job.log |
| `highlighting.png` | Highlight rules | A listing where the example rules show: a dimmed `bin`/`obj`, an archive, a "secret" file in red |

The landing page's hero is drawn in HTML (`index.html`, `.window`), not a screenshot, so it stays
sharp at any size and shows no one's real files. Replace it only with something better.

## To retake (review of 2026-11-04)

`settings-tools.png`, `about-computer.png` and `conflict-question.png` were retaken the same day.

Real working folders rather than demo content (the `C:\src\orbit` setup the good ones use):
- `window-tour.png` (the hero): the server pane shows test junk (`foo`, `shelll_test`,
  `upload-me.txt`) and the left pane the real repository.
- `back-menu.png`: the same places repeated in the menu, and "dan _co" left in the address bar.
- `git-status.png`: a stray empty `NewFile.md`.
- `shelf.png`, `palette.png`, `address-completion.png`, `worktrees.png`, `archive-inside.png`:
  real paths. Not secret, but busier than demo content. The last suggestion in
  `address-completion.png` is cut off.

Small:
- `sftp-hostkey.png`: the address is `sftp://demo@localhost:2222/config` but the rows are local files.
- `everywhere-search.png`: the footer text is cut off.
- `job-strip.png`: the first output line is clipped and the test lines repeat.
