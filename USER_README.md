# Dance Commander

A two-pane file manager for Windows, for the keyboard. Two lists side by side, F5 copies from one
to the other, F6 moves. Local disks, SFTP and WebDAV servers, and the inside of archives all list
the same way. It never stops you with a dialog: questions appear in the pane, and everything it
does to your files can be undone with Ctrl+Z.

This file is for using it. `about:app` (or *About Dance Commander* on This computer) shows the
version, the licences, and where the settings and logs are.

Something marked *(untried)* exists, but nobody has used it for real yet. It may well work; if you
use it, say how it went ([When something is wrong](#when-something-is-wrong) says how).

---


## Starting

**The installer** (`DanceCommander-<version>-win-x64.msi`) is the one to use. It installs for you
alone, without asking for administrator rights, into `%LOCALAPPDATA%\Programs\Dance Commander`,
with a Start-menu entry. Ticked by default, it adds *Open in Dance Commander* to Explorer's menu
for folders and drives (on Windows 11, under *Show more options*). A newer installer replaces the
older version and keeps that choice. Uninstall it from *Installed apps*; your settings stay.

**The zip** is the same program, to try it without installing: unpack it into a folder of its own
and start `DanceCommander.exe`. It adds nothing to Explorer or the Start menu.

Either way, Windows may warn that it doesn't recognise the program: the build isn't signed yet.
*More info → Run anyway* starts it. Pin it to the taskbar once it's running.

It opens where you left off: the same folders, tabs and history. Starting it again while it runs
brings the running window forward, so a shortcut or "Open with" never opens a second one.

```
DanceCommander C:\work                 the active pane goes there
DanceCommander C:\work D:\backup       one place in each pane
DanceCommander /select,C:\work\a.txt   the folder, with the cursor on a.txt (Explorer's syntax)
DanceCommander --help                  everything else
```

---

## Keys

**The legend at the bottom teaches the keys.** At rest it shows the F-keys. Hold Ctrl, Shift or Alt
and it shows everything bound to that modifier. If there are more than fit, tap the modifier again
to see the next line, or scroll over the legend. Everything is also in the **command palette**
(Ctrl+Shift+P): type words from the name. **Ctrl+.** lists what can be done in this folder.

The keys the legend doesn't show:

| Key | Does |
|---|---|
| Enter | Open: a folder, an archive, or a file in its own program |
| Backspace | Up to the parent folder |
| Tab | The other pane |
| Insert, Space | Mark the row and move down. Arrows move without losing the marks |
| Ctrl+A, Ctrl+Shift+A, Ctrl+I | Mark all, unmark all, invert |
| Escape | Clears one thing at a time: the preview, a question, a notice, the filter, then the marks |
| Delete, Shift+Delete | To the Recycle Bin; or delete for good |
| Ctrl+C, Ctrl+X, Ctrl+V | As in Explorer. Pasting an image or text makes a new file of it |
| Typing | Filters the list as you type. Backspace and Escape take it back |
| Ctrl+R | Refresh |
| Ctrl+H | Show hidden files |
| Ctrl+Tab | Cycle through the tabs |
| Shift+F10, the Menu key | The right-click menu |
| Win+Shift+B | Show or hide the shelf window, from anywhere in Windows |

The ones worth learning early: **Ctrl+L** to type an address, **Ctrl+S** to search, **Ctrl+T** for a
new tab, **Alt+Left** for back, **F3** to preview, **Ctrl+E** to run a command in this folder,
**Ctrl+Z** to undo.

**Ctrl+,** opens the settings, where any command can be given a key of your own; it says when the
key is taken or is usually something else. A tool of your own can take a key too (see
[Tools](#tools-of-your-own)).

### Mouse

Drag files between the panes or onto a folder. While dragging, hold the pointer over another tab or
a folder for a moment and it opens. Drop from Explorer works, and text dropped on a pane becomes a
file. Dragging local files out to Explorer works too.

---

## Addresses

Ctrl+L, then type an address and press Enter:

| Address | Goes to |
|---|---|
| `C:\work`, `\\server\share\dir` | A folder |
| `%USERPROFILE%\Downloads` | Environment variables are expanded |
| `sftp://me@build01/var/log` | An SFTP server; `:2222` after the host for another port |
| `davs://me@cloud.example.com/remote.php/dav/files/me/` | A WebDAV server over HTTPS (`dav://` for plain HTTP) |
| `C:\dl\release.zip!/bin` | Inside an archive: `!/` separates the archive from the path in it |
| `flat:C:\work` | Every file below a folder in one list (Ctrl+Shift+F) |
| `about:computer` | Drives and the usual folders (Alt+Home, or Backspace from a drive) |
| `about:bookmarks`, `about:history` | Bookmarks (Ctrl+D to add one), and where you've been |
| `about:shelf`, `about:collections` | The shelf (Ctrl+B) and saved collections |
| `about:app` | Version, licences, settings and logs |

Anything without `\`, `/` or `:` is a bookmark name if one starts with it (`dow` → Downloads), and
otherwise a **search** of the current folder. `?` in front always searches: `?readme`.

**Searching** (Ctrl+S, or `?` in the address bar) goes through every folder below this one. All the
words must match. A word matches any name that contains it; a word with `*` or `?` is a pattern:
`?*.cs test`, `?report*.{pdf,docx}`. Typing in a list filters it by the same rules.

Archives open with Enter: zip, 7z, tar, and gz (alone or as tar.gz); rar, bz2 and xz too. They are read-only: copy out of them, or *Extract*. *Zip* (palette or right-click) makes a new zip
of the marked files.

---

## Servers

**SFTP.** Type `sftp://user@host/path`. The first time, the pane shows the server's key
fingerprint: *Trust this host* remembers it. Then it asks for the password, and *Remember this
password* keeps it in Windows Credential Manager. Only passwords for now: SSH keys and the agent
aren't supported yet. Ctrl+Shift+B on a server opens a terminal there.

**WebDAV** (Nextcloud; other servers *(untried)*). Type `davs://host/path`. It asks for a user
name and password when the server wants them. On a server inside your organisation (a name
without dots, or in this computer's domain) it tries your Windows sign-in first *(untried)*.
Over plain `dav://` it sends no password and no Windows sign-in, since anyone on the network
between could read or reuse them; on a network you trust, *Sign in to unencrypted WebDAV* in
Settings allows it. On Nextcloud, *Copy web link* puts a file's link in the web interface on the clipboard, and
large uploads go in pieces.

Saved passwords are in Windows Credential Manager under *Windows Credentials*, named
`DanceCommander:…`. Remove one there to be asked again.

---

## Files that are safe

Copies and moves land under a temporary name and only take the real name once complete. Nothing is
overwritten without asking, and the question offers to keep both. Delete goes to the Recycle Bin.
Every operation can be undone with Ctrl+Z, and says what it did in the shell's own words: the
*Activity* button shows the list.

Closing the window while something is copying asks first. A file Dance Commander was writing when
something went wrong is left under a name ending in `.part` or `.dc-upload-…`, never under the real
name.

---

## Settings

**Ctrl+,** opens the settings window: the colours, row height, text size, keys and tool packs.
What it changes is kept as JSON files in `%APPDATA%\DanceCommander` (the *Settings* row on
`about:app`), which can be edited by hand as well.
Comments are allowed. A change takes effect as soon as the file is saved. If a file has a mistake,
the pane says where and the app keeps using the previous version.

| File | Holds |
|---|---|
| `settings.json` | `appearance.scheme`: `"system"` (follows Windows), `"dark"`, `"light"`. `commandLine.shell`: `"auto"`, `"bash"`, `"pwsh"`, `"cmd"` |
| `tools.json` | Your own commands, and listings defined by a command |
| `highlighting.json` | Colours for rows that match a rule |
| `bookmarks.json`, `collections.json` | Bookmarks and collections, also edited from the app |
| `known_hosts` | The SFTP servers you've trusted |

```jsonc
// settings.json
{ "appearance": { "scheme": "light" }, "commandLine": { "shell": "pwsh" } }
```

The window's place, the open tabs and the logs are on this machine only, in
`%LOCALAPPDATA%\DanceCommander`.

---

## Tools of your own

`tools.json` adds commands to the palette, the right-click menu and, if you give them one, a key. It
comes with examples: Git Bash here, Windows Terminal here, VS Code, SHA-256 of a file.

```jsonc
{
  "tools": [
    {
      "id": "sha256",
      "title": "SHA-256",
      "appliesTo": "file|multi",       // file, directory, background, multi
      "exec": "certutil",
      "args": ["-hashfile", "${path}", "SHA256"],
      "launch": "capture",             // process, console, capture (output in the pane), job
      "forEachSelected": true,
      "keybinding": "Ctrl+Shift+K"
    }
  ]
}
```

Each entry in `args` stays one argument, spaces and all.

| Placeholder | Is |
|---|---|
| `${path}`, `${name}`, `${stem}`, `${ext}` | The file under the cursor |
| `${selection}` | Every marked file, one argument each |
| `${dir}`, `${otherPaneDir}` | This pane's folder, and the other's |
| `${repoRoot}`, `${branch}`, `${remoteUrl}` | The git repository; a tool that needs one is hidden outside it. `${remoteUrl}` |
| `${env:NAME}` | An environment variable |
| `${prompt:Label}` | Asks in the pane before running |

`when` limits where a tool appears: `{ "extensions": ["zip", "7z"] }`, `{ "glob": "*.log" }`,
`{ "filesOnly": true, "minSize": "100MB" }`, `{ "newerThan": "7d" }`. `schemes` says which kinds of
place it applies to (`["file"]` by default, local only), and a tool using `${otherPaneDir}` also
needs the other pane on screen and showing such a place. `"maxSelected": 3` keeps it off the menu
when more rows than that are marked. `"inConflict": true` offers it only where git
reports a merge conflict: on the conflicted files and folders holding them, or, for a tool about
the folder, anywhere in a repository that has one.

Programs found on this computer bring tools of their own, each set switched on or off in Settings:
Git Bash, Windows Terminal and VS Code; WSL, Beyond Compare (compare marked files, with the other
pane, or the two folders; git diff, and resolving conflicts), Fork and GitKraken (open the
repository). A tool of yours with the same `id` is used instead of the program's.

**F4 edits**. Text goes to whatever opens your `.txt` files, and anything else
to Windows' own "Edit" (Paint for an image). It never runs the file, so it's the safe key on a
script. A tool with `"edits": true` takes F4 for the files its `when` matches, ahead of both:
`"when": { "glob": "*.{html,py}" }` on a VS Code tool sends those to VS Code. Its `args` must name
the file (`${path}` or `${selection}`), and its `exec` can't be the file itself.

A tool's `exec` is found on PATH or in App Paths, never in the folder you're browsing; give a full
path for anything else. When it's a `.cmd` or `.bat` (VS Code's `code` is), names containing `&`,
`%` or `^` are refused, since cmd.exe would read them as commands. Point `exec` at `Code.exe` to
avoid that.

---

## The command line

Ctrl+E opens a line at the bottom of the pane. Enter runs it in this folder with Git Bash if it's
installed (or the shell in `settings.json`), and on an SFTP server, with the server's own shell.
Output stays in the pane. A command still running after 3 seconds becomes a job you can stop, and
ending a line with `&` makes it one straight away. Up and Down bring back earlier lines.
`${selection}` and the other placeholders work here too, quoted for you.

**Shift+Enter runs** the script or program under the cursor through this line: `bash ./build.sh`,
`pwsh -NoProfile -File ./deploy.ps1`, `py ./tool.py`, `./setup.exe`. That's what Enter can't do,
since it opens a file the way Explorer would: a `.bat` flashes a console, and a `.ps1` opens in
Notepad. The output stays in the pane, and Up brings the line back to run it again.
**Run with arguments…** (palette or right-click) puts the same line on the command line for you to
finish.

---

## When something is wrong

**Something slow, confusing or in the way:** *Note friction…* in the palette, while it's fresh. One
line in your own words, kept on this computer with the time, the kind of place you were in (a
folder, a server, an archive) and your last few commands, never a path. Nothing is sent anywhere.
When you have a few, *Copy friction report* puts them on the clipboard with the version and
whatever was slower than it should be this session. Read it over, then paste it into a new issue at
[github.com/elefAntti/dance_commander/issues](https://github.com/elefAntti/dance_commander/issues).
Issues there are public, and your notes are the one part that can name a file or a server.

**An error:** most errors no longer end the program. The pane says "Something went wrong, and
Dance Commander carried on", and *Copy error report* puts the details on the clipboard to send.
Those can name your files and servers, so read it first. *Show the error log* opens `errors.log`.

- The logs are in `%LOCALAPPDATA%\DanceCommander\logs` (the *Logs* row on `about:app`), one file a
  day for a week. A line saying `over budget` means something was slower than it should be.
- After a crash, the next start says so and points to the log.
- `about:app` gives the version and the commit to quote.
