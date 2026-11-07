# Dance Commander

**A keyboard-first, two-pane file manager for Windows, from the era before file management was
something an OS tried to hide from you — rebuilt with 2026 ergonomics.**

Two panes, the F-key legend, Insert/Space marking and F5/F6 copy and move, as in Norton Commander.
Added to that: browser-style navigation (address bar, back and forward, tabs, bookmarks, history),
and the same listing for **local disks, SFTP, WebDAV and the inside of archives**, so a zip on a
server opens in place. It is git-aware (branch, worktrees, file status), watches folders live, and
is extended by **tools and scripts the user owns** (`tools.json`, script-backed locations such as
GitLab pipelines), not by plugins. It never shows a modal dialog, and it tells you what it did to
your files in the shell's own words, with undo.

It is a bespoke tool, and "not for everyone" is deliberate: calm and fast, staying out of the way
until you reach for it. Windows only, by choice: Linux has a wealth of good file managers and I think a file manager is something that should integrate with the platform. 

**Using it:** [USER_README.md](USER_README.md), which ships beside the executable as `README.md`.
This file is for building and changing it.

**PREVIEW** Dance Commander v0.9 is a preview version. It should be usable as your daily tool (that's how I use it), but may still lack some rather obvious functionality (ejecting drives, emtying the recycle bin) and have bugs. You should be able to get a good feel of the spirit of the project. Work continues on 1.0 and your feedback is valuable.

---

## Building and running

**Requirements:** Windows 10 2004 (build 19041) or later, x64; the **.NET 9 SDK** (developed with
9.0.306).

```bash
dotnet build DanceCommander.sln          # everything, Debug
dotnet test  DanceCommander.sln          # the xUnit suite in tests/
dotnet run --project src/DanceCommander.App
```

The app is single-instance per user: a second start hands its arguments to the running window.
Use `--new-window` to run a development build beside an installed one.

**Installing a build for daily use:**

```bat
scripts\publish-local.bat
```

It publishes a self-contained, single-file `DanceCommander.exe` to `%LOCALAPPDATA%\DanceCommander\`.
**It force-closes any running Dance Commander first**, so save what you are doing. Pin that exe to
the taskbar.

**Command line** (Explorer's syntax is accepted verbatim, so "show in folder" from other programs
works): `DanceCommander [PATH | LEFT RIGHT] [/select,PATH] [/n,PATH] [/e,PATH] [--one-pane]
[--two-panes] [--new-window] [--help]`. A PATH may be any address: `sftp://user@host/var/log`,
`flat:C:\src`.

**Where things live at run time:**

| What | Where |
|---|---|
| The installed app | `%LOCALAPPDATA%\DanceCommander\` |
| Logs (Serilog, daily files; quiet unless something is slow or wrong) | `%LOCALAPPDATA%\DanceCommander\logs\` |
| Window placement | `%LOCALAPPDATA%\DanceCommander\window.json` |
| User config: tools, bookmarks, history, collections, highlighting, view state (hand-editable JSONC, hot-reloaded) | `%APPDATA%\DanceCommander\*.json` |
| Passwords | Windows Credential Manager, never in a file |

**Test servers for the remote providers:** `scripts/webdav-test-server.ps1` runs a local wsgidav
server under `temp/` (git-ignored), seeded with the awkward cases. SFTP is tested against a real
server or a container.

---

## What is where

```
DanceCommander.sln
Directory.Build.props            shared settings: net9, x64, nullable, latest C#
src/
  DanceCommander.Core/           the model; no UI, no Windows UI types
  DanceCommander.Extensibility/  tools.json: definitions, placeholders, conditions, running, jobs
  DanceCommander.Providers.Local/    local disk, about: pages, git, watching, Recycle Bin
  DanceCommander.Providers.Sftp/     SFTP over SSH.NET: pooled connections, known hosts, remote commands
  DanceCommander.Providers.WebDav/   WebDAV over HttpClient
  DanceCommander.Providers.Archive/  zip/7z/tar/rar… contents via SharpCompress, nested anywhere
  DanceCommander.App/            WPF: startup, views, view models, theme, Windows integration
tests/DanceCommander.Tests/      xUnit + Shouldly, one file per area
scripts/                         publish, test servers, UI harness, the GitLab location and review scripts
assets/                          application icon and the script that generates it
```

**Core** (`src/DanceCommander.Core/`), by folder:

| Folder | Holds |
|---|---|
| `Fs/` | `FsUri` (every address, nesting included), `FsEntry`, `IFileSystemProvider`, the provider registry, the `about:` pages |
| `Listing/` | listing sources beyond folders: flatten, search, listings from a command, `ScriptProvider` (script-backed locations); filter and sort |
| `Transfer/` | copy and move across providers, conflict policy, drop and paste rules |
| `Jobs/` | background jobs (transfers, builds): state, progress, streamed output |
| `Operations/` | file operations as reified objects: activity log, undo stack, relocation feed |
| `Commands/` | the one command registry every menu, key and the palette render from |
| `Navigation/` | history, bookmarks, per-location view state |
| `Collections/` | collections and the shelf |
| `Config/` | `JsonConfigStore`: JSONC, hot reload, never overwrites a broken file |
| `Columns/`, `Decorations/`, `Matching/` | per-listing columns; row decorators (git, highlights); name and glob matching |
| `Preview/`, `Clipboard/`, `Decisions/`, `Security/`, `Startup/`, `Watching/` | content sniffing, clipboard formats, inline (non-modal) questions, the credential store, the command line, watcher contract |

**App** (`src/DanceCommander.App/`): `Program.cs` (runs before WPF loads: single-instance hand-off,
launch placeholder), `App.xaml.cs` (DI composition), `ViewModels/` (shell → pane → tab),
`Views/` (main window, pane, legend bar), `Commands/` (built-in commands), `Services/` (Windows
integration: clipboard, icons and thumbnails, PDF preview, notifications, window placement),
`Theme/` (design tokens, palette, control styles).

**Documents:**

| File | Read it for |
|---|---|
| [docs/DESIGN.md](docs/DESIGN.md) | What the app is for, its principles, what it declines; read before changing anything |
| [docs/ROADMAP.md](docs/ROADMAP.md), [docs/WISHLIST.md](docs/WISHLIST.md) | Where it is going, and ideas without a place yet |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Reference by section: contracts, feature designs, config, keyboard model |

Comments cite DESIGN.md as §1.1–§1.6. The plans, the implementation log, the bug list and the
safety audit (`PLAN.md`, the `*_PLAN.md` feature plans, `IMPLEMENTATION_LOG.md`, `BUGS.md`,
`AUDIT.md`) are the author's working notes, kept in `plans/`, which is not part of the published
source; comments that cite them refer to those notes.

---

## Software bill of materials

Third-party packages as resolved for `DanceCommander.App` (`dotnet list package
--include-transitive`, 2026-09-30). Licences are as declared by each package on NuGet; confirm
before redistributing. For a machine-readable SBOM, run `dotnet CycloneDX DanceCommander.sln` (the
`CycloneDX` .NET tool) rather than maintaining this table by hand.

**Shipped in the application:**

| Package | Version | Licence | Used for | Referenced by |
|---|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT | view-model source generators | App |
| Microsoft.Extensions.Hosting (and its `Configuration.*`, `DependencyInjection.*`, `Logging.*`, `Options.*`, `FileProviders.*`, `Diagnostics.*`, `Primitives` dependencies) | 9.0.11 | MIT | DI container, hosting, logging abstractions | App; Core and Extensibility use `Logging.Abstractions` |
| System.Diagnostics.EventLog | 9.0.11 | MIT | transitive, from Hosting | App |
| Serilog | 4.2.0 | Apache-2.0 | logging | App (transitive) |
| Serilog.Extensions.Hosting | 9.0.0 | Apache-2.0 | Serilog in the host | App |
| Serilog.Extensions.Logging | 9.0.0 | Apache-2.0 | transitive | App |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | the rolling log file | App |
| SSH.NET | 2026.0.0 | MIT | SFTP and remote commands | Providers.Sftp |
| BouncyCastle.Cryptography | 2.7.0 | MIT | transitive, from SSH.NET | Providers.Sftp |
| SharpCompress | 0.50.4 | MIT | reading archives, writing zips | Providers.Archive |
| DotNet.Glob | 3.1.3 | MIT | glob matching in tools and rules | Core |

**Build and test only (not shipped):**

| Package | Version | Licence |
|---|---|---|
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT |
| Shouldly | 4.3.0 | BSD-3-Clause |
| coverlet.collector | 6.0.2 | MIT |
| Microsoft.NET.ILLink.Tasks | 9.0.10 | MIT (auto-referenced by the SDK for publish) |

**Platform, not redistributed by us:** the .NET 9 runtime (MIT; bundled into the self-contained
publish), WPF, and Windows APIs used through P/Invoke and WinRT (`shell32`, `kernel32`
`CopyFileEx`, DWM, `Windows.Data.Pdf`, the Windows Credential Manager).

**External programs the default tools call, only if installed:** git, VS Code (`code`), Windows
Terminal (`wt`), Git Bash, the devcontainer CLI. None is required to run the application.

---

## Licence

[MIT](LICENSE). The third-party packages above keep their own licences; a published build must
carry their notices alongside the executable. [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)
is those notices, written by `scripts/notices.ps1` from the restored packages: run it before a
release, after any package change, and commit the result. The build copies it and the licence
beside the executable, and `about:app` lists both.
