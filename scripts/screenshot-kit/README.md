# Screenshot kit

Everything needed to take the site's screenshots (`site/SCREENSHOTS.md`) without showing anyone's
real files, hosts or employer.

| File | What it does |
| --- | --- |
| `New-ShotScene.ps1` | Builds `C:\src\orbit` (a repository on `feature/telemetry` with M / A / ? changes, `bin`/`obj`, a secret, a release zip, a 150 MB file, a changelog with tabs and mixed line endings) and `C:\src\scratch`. `-BigFile` adds a 16 GB file for a long copy. |
| `Use-DemoProfile.ps1` | Renames `%APPDATA%\DanceCommander` aside and puts a demo profile in its place: the mock tools and listings, the Highlight rules page's examples, neutral bookmarks. `-Restore` swaps back. Dance Commander must be closed. |
| `mock-pipelines.ps1` | Stand-in for `scripts/gitlab-pipelines.ps1`: same levels, same row format, made-up pipelines, a real `artifacts.zip`. |
| `mock-build.ps1` | A build and test run that prints for about 90 s and builds nothing, for the job strip. |
| `Start-ShotServer.ps1` | A throwaway SFTP server in Docker: `sftp://demo@localhost:2222/config`, password `demo`. `-Stop` removes it. |
| `shot.ps1` | `Save-DcShot <name> [-Crop x,y,w,h] [-HoldKey 0x11]`: copies the window from the screen into `site/screenshots/` (never replacing a file there). Dot-source after `scripts/ui-harness.ps1`. |

## Order

1. Close Dance Commander. `New-ShotScene.ps1 -BigFile`, `Use-DemoProfile.ps1`, `Start-ShotServer.ps1`.
2. Launch it on the scene: `Start-Process "$env:LOCALAPPDATA\DanceCommander\DanceCommander.exe" -ArgumentList '--left','C:\src\orbit','--right','C:\src\scratch'`.
3. Take the shots.
4. Close it. `Use-DemoProfile.ps1 -Restore`, `Start-ShotServer.ps1 -Stop`, and delete
   `C:\src\scratch\images` (16 GB).

## Recipes

| Shot | How |
| --- | --- |
| `window-tour.png` | The hero. Move the window to 1460×900 first so the legend clears the taskbar. Right pane: `sftp://demo@localhost:2222/config/orbit/logs` (password, then Trust this host), then Ctrl+T to `C:\src\scratch` and Ctrl+PgUp back. Left pane: Ctrl+Enter on `docs` for a second tab, then Ctrl+L to `src\Orbit.Cli` in it, for git's letters. Shot without `-Highlights`, so the listing is as plain as a fresh install. |
| `pipeline.png` | In orbit, Pipelines from the palette (Ctrl+Shift+P). One pane at the root, the other at `from:C:\src\orbit?gitlab/1284` (the jobs). Into `package - zip - success` for artifacts.zip and job.log. |
| `highlighting.png` | Swap in with `Use-DemoProfile.ps1 -Highlights`; the rules are off otherwise. `src\Orbit.Cli`: every example rule at once. `Program.cs` (changed today) bold and first, `appsettings.secrets.json` and `signing.pfx` in red, `bin`/`obj` dimmed, the 120 MB dump shaded. |
| `job-strip.png` | Build and test (Ctrl+Shift+F5) in orbit, shot while the output streams. |
| `close-with-jobs.png` | F5 `C:\src\scratch\images\orbit-test-vm.vhdx` to the other pane, then Alt+F4. Cancel and close afterwards (the half-copied file goes with the cancel). |
| `in-use-question.png` | `Start-Process pwsh -WorkingDirectory C:\src\scratch\old-build`, then Del on `old-build`. Retry once the shot is taken, after closing that pwsh. |
| `sftp-hostkey.png` | Ctrl+L, `sftp://demo@localhost:2222/config`. |
| `preview-text.png` | F3 on `notes\changelog.txt`. |
| `quick-filter.png` | In `docs`, type `mon d`. |
| `command-line.png` | Ctrl+E, `git log --oneline -5`. |
| `needs-terminal.png` | Ctrl+E, `vim notes\notes.txt`. |
| `legend-ctrl.png` | `Save-DcShot … -HoldKey 0x11 -SettleMs 6700`: the Ctrl layer's first page is only the four sort keys; its second (Location, Command, tabs) turns up after about 6 s. Crop the bottom 150 px. |
| `activity-log.png` | After a few copies, renames and a new folder in scratch: Activity at the bottom right. The popup runs past the window's right edge, so it can't be taken from the window alone yet. |
| `settings-tools.png` | Ctrl+, then select Tools through UI Automation (its list item has SelectionItemPattern). Save with `-Crop` to the Settings window. The "Found:" lines show full paths; the user's name under `C:\Users` is fine to publish, a company name is not. |
| `conflict-question.png` | Copy `C:\src\orbit\README.md` into `C:\src\scratch` first, then F5 on orbit's README.md. Cancel the copy and remove the scratch copy afterwards. |
| `about-computer.png` | Not on a work machine: the known folders show their full paths, which can name the employer. |
| `everywhere-search.png` | In orbit, type `orbit`, Delete (drops the inline completion), Enter. Below the scene's rows come your own files with "orbit" in the name (fonts, here), so make the window short enough to end at `orbit-test-vm.vhdx`. Don't put a decoy in Downloads: the Folder column shows `…/<you>/Downloads`. |

## Things that bit

- The address bar completes inline and Enter accepts the completion: typing `C:\src\orbit\src`
  and Enter went to `C:\src\orbit\src\Orbit.Cli`. Press Delete before Enter.
- A word that is also a bookmark's name goes to the bookmark instead of searching, which is why the
  demo bookmark for orbit is called "Project".
- `Send-DcText` with a whole path in one burst lost characters in the address bar; type one key at
  a time.
- The Settings window is its own window: `Send-DcKeys` brings the main one forward, so keys meant
  for Settings land in the listing. Click into Settings, or use UI Automation. A search of the
  app's top-level windows doesn't always return Settings even while it is on screen (it found it
  once and not the next time); Win32 `EnumWindows` by process does, and a `WM_CLOSE` to it closes it.
- `session.json`, `listings.json` and `window.json` live beside the installed app in
  `%LOCALAPPDATA%\DanceCommander`, not in the roaming profile, and the app rewrites them on exit.
  `Use-DemoProfile.ps1` moves them aside too.
