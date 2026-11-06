---
name: debug-dance-commander
description: How to investigate Dance Commander problems the user reports from daily use — slow startup, slow menus or listings, failing tools/jobs, odd UI state. Where the logs and config live, how to read the timing lines, and what not to touch while the app is running.
---

# Debugging Dance Commander

The user runs the published app as their daily file manager. **Do not build, run tests or
publish until they say so** — `scripts/publish-local.bat` kills the running `DanceCommander.exe`.
Write the fix, then list what needs checking under "Written, not yet built or verified" in
PLAN.md. Reading logs, config and docker state is always fine.

## Where things are

| What | Path |
| --- | --- |
| Log (Serilog, daily roll, `_NNN` suffix per restart) | `%LOCALAPPDATA%\DanceCommander\logs\dance-commander-YYYYMMDD*.log` |
| Published app (what the user is running) | `%LOCALAPPDATA%\DanceCommander\` ("Content root path" in the log) |
| User config: tools, bookmarks, highlighting, view state | `%APPDATA%\DanceCommander\*.json` |

In Git Bash: `cd "$LOCALAPPDATA/DanceCommander/logs"`, then `cut -c1-400` the newest file. Stack
traces are long; the first line and the one `at DanceCommander...` frame are usually enough.

## Reading the timing lines

The log is quiet unless something is over its budget (DESIGN.md §1.5, `Services/Budgets.cs`), so
every line in it means something. Each timing line ends with `over budget: <what> (<budget>)`,
naming the promise it broke, or `within budget`.

- **`Startup: a N ms, b N ms, …`**: one line per launch from `StartupClock`, in milliseconds since
  the *process* started, in the order they happened. Marks: `main` (Program.Main entered),
  `placeholder shown` (the Win32 window on its own thread), `services`, `shell`, `window built`,
  `shown`, `window drawn` (the placeholder dismissed), `N rows set` / `left rows` / `right rows`,
  and `rows on screen` (the line is written then; later marks are lost).
  - Compare against the fast launches earlier in the same file (roughly 130 ms placeholder and
    ~1000 ms rows on screen on this machine). If *every* mark is 3–5× slower, the machine was
    loaded (docker/WSL starting, a build running); look for what else differs before blaming code.
  - A mark missing from the line means it happened after `rows on screen`, or never.
- **`Slow listing: <uri>, N rows on screen after X ms, decorated after Y ms`**: X is provider and
  grid; Y−X is the decorators (git branch, highlight). A big Y−X on a repo folder is git.
  **`Slow listing at launch:`** is a pane's first listing, cold: the `Startup:` line already timed
  it, so it says nothing about the folder unless the same folder is slow later too.
- **`Slow menu: T ms to show, B ms of it building N items`**: B is our code (ContextMenuPlan and
  Render); T−B is WPF (templates, layout, the popup window). Small B with large T on the *first*
  menu of a session means the idle warm-up (`MainWindow.WarmMenus`) did not run or did not help:
  look for `Menus warmed in N ms` or `Warming the menus failed` after the `Startup:` line.
- `[WRN]`/`[ERR]` lines: grep for them first: `grep -n "WRN\|ERR" <log>`.

## Things learnt the hard way

- **WPF dispatcher priority decides "when".** `ContentRendered` is posted at Input priority, below
  the Normal-priority `await` continuations the first listings produce, so anything hung on it
  waits for all of them. Use `CompositionTarget.Rendering` (render ticks) when "the frame is up" is
  what you mean.
- **A ContextMenu can't have a parent**, so it can't be put into a Popup, and opening a real one
  takes keyboard focus. Warm or measure menu visuals some other way.
- Don't edit files through a Python heredoc when the text has Windows paths or C# escapes in it:
  `'C:\repo'` turns into a carriage return, and `'C:\x…'` is a syntax error. Use the Edit tool, or
  a raw string.
- A warm-up or other optimisation that catches its own exception fails silently. Check the log
  for its warning before assuming it works.

## Driving the real window

Only when the user has said the app is free to use (or has told you to test). `scripts/publish-local.bat`
kills the running copy.

- Publish, then launch **once** with PowerShell's `Start-Process`, not `cmd //c start` or `&` from
  Git Bash (those produced two instances, one of them without a log). Open it on a scratch folder in
  `%TEMP%`: `-ArgumentList '--left','<scratch>','--right','<scratch>\repo'`. Never on the user's own
  files, and never write captures into the folder under test.
- The first launch after a publish takes about 20 s before `main` (see the `Startup:` line). Wait for
  it; it is not a hang.
- `. .\scripts\ui-harness.ps1` gives: `Get-DcTexts` (every named element: the text on screen),
  `Get-DcRows` (rows with selected and focused), `Invoke-DcButton 'Copy error'` (no foreground
  needed), `Send-DcKeys '^b'` / `'{ESC}'`, `Send-DcDrag`, `Get-DcFocused`, `Get-DcLog`.
- Keys and pointer need the foreground. The harness takes it only after 20 s of user idle time,
  counts its own input as not the user's, and throws otherwise. **Never weaken that guard**: an
  earlier harness typed a path into the user's editor. If it throws, wait and retry. Don't go around it.
- UI Automation can't see colours or animations, and `PrintWindow` returns white for this window,
  so anything visual is for the user to look at. List it in PLAN.md.
- A context menu is a separate top-level window: find it from `RootElement` by process id, and press
  its items through `InvokePattern`.
- Typing shell commands into the app's command line through synthetic keys was refused by the
  session's safety classifier. Leave command-line checks to the user.
- Temporary tools: back up `tools.json` byte for byte, add the tool, and restore with `cp` then
  `cmp` when done.

## Tools and jobs (`tools.json`)

A job's failure text is the tail of what the process printed; read it as the tool's own error
before suspecting the job runner.

- A tool is **one process**: there is no step list. To chain steps, use
  `"exec": "pwsh", "args": ["-NoProfile", "-Command", "a; if ($LASTEXITCODE) { exit $LASTEXITCODE }; b; exit $LASTEXITCODE"]`.
  Not `cmd /c`: tools.json args are .NET-quoted, which cmd.exe misreads (see the command-line
  quoting notes in PLAN.md). Placeholders inside the string are expanded as-is, so single-quote them
  for pwsh.
- **devcontainer:** `devcontainer exec` never starts a container. "container … is not running"
  means it exists but stopped (after a reboot or a Docker restart). Check with
  `docker ps -a --filter "id=<first 12 hex>" --format "{{.ID}} {{.Status}} {{.Label \"devcontainer.local_folder\"}}"`.
  Run `devcontainer up` first, with the **same `--id-label`** that `exec` uses, so it finds that
  container instead of building a new one. The orbit release tool does exactly this.
- Back up `%APPDATA%\DanceCommander\tools.json` before editing it, and validate the JSON afterwards
  (`python -c "import json;json.load(open('tools.json',encoding='utf-8'))"`).
