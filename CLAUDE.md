# Dance Commander — working notes for agents

A bespoke two-pane file manager in C#/WPF. **Read [DESIGN.md](docs/DESIGN.md) before any feature or UI
work**: the vision, the structural commitments, the visual direction, what was declined on
purpose (§1.4 — don't propose those again without a new argument), and the time budgets (§1.5).
[ROADMAP.md](docs/ROADMAP.md) and [WISHLIST.md](docs/WISHLIST.md) are the public direction and ideas.
[ARCHITECTURE.md](docs/ARCHITECTURE.md) is reference for the area you touch.

`plans/` is private and never published: [PLAN.md](plans/PLAN.md) holds status, what's next and the
checks; [IMPLEMENTATION_LOG.md](plans/IMPLEMENTATION_LOG.md) says what was already tried and why it
went the way it did; [AUDIT.md](plans/AUDIT.md) records which code has been read for file-losing bugs,
the kinds found, and what to read next. Nothing outside `plans/` may name the company, its
projects, its tickets or its paths.

## Rules the build enforces

- No modal dialogs: `BannedSymbols.txt` makes `MessageBox`, `ShowDialog`, the file pickers and
  `InputBox` build errors. Decisions are async requests rendered inline (`DecisionViewModel`).
- No deleting or overwriting without a reason: `BannedSymbols.Files.txt` makes `File.Delete`,
  `Directory.Delete`, the overwriting `File.Move`/`File.Copy`, `File.Create`, `File.WriteAll*`,
  truncating `FileMode`s, `IFileSystemProvider.DeleteAsync` and SFTP's deletes build errors in
  `src/`. A use that has to stay sits in `#pragma warning disable RS0030 // §1.6: why it can't
  lose anything`; `grep "§1.6:"` is the review list. Prefer the safe form: two-argument
  `File.Move`/`File.Copy`, `FileMode.CreateNew`.
- `DesignRuleTests`: font sizes and colours come from `Theme/` tokens, at most three type sizes,
  and user actions are `CommandDefinition`s in the registry, not `[RelayCommand]`s on viewmodels.
- If a rule is in the way, say so and ask. Never loosen the rule or its test to get a change through.

## Rules nothing enforces yet

- New permanent screen space comes out of the five chrome regions (§1.3), never on top of them.
- Anything that mutates files is an operation with a description, an undo and a shell equivalent.
- **A change that can touch the user's files gets an independent review before it is called
  done**: `Transfer/`, `Operations/`, a provider's write/move/delete, undo, the Recycle Bin, or
  anything that turns outside text (names, script or command output) into a path or a command
  line. A fresh subagent gets only the diff and [AUDIT.md](plans/AUDIT.md) §2, not the reasoning behind
  it, and answers DESIGN.md §1.6's question: *where is the last copy of each file at every step,
  and what if it stops there?* It lists what it found safe as well as what it found. Its findings
  go to the user unfiltered, each with a verdict; a new kind of bug goes into AUDIT.md §2.

## Speed

- Each timing line in the log (`Startup:`, `Slow listing`, `Slow menu`) ends with the budget it
  broke. Use them, not intuition.
- **A/B against the previous build**, launches alternated under the same conditions. Load on
  this machine (Docker, WSL) shifts every number by half. Don't judge a single launch, and
  never the first one after a publish (Defender makes it 20 s or more).
- A mark can lie: check that it means what its name says before believing a big win.

## The app is in daily use

- The user runs the published copy as their file manager. `scripts/publish-local.bat` kills it.
  Don't publish or launch it unless the user has said it's free. The `debug-dance-commander`
  skill covers logs, config and driving the real window.
- What needs checking in the running app goes under "Written, not yet verified" in PLAN.md, with
  a *Serves:* line naming the §1 principle. Once the user confirms it, move it to IMPLEMENTATION_LOG.md.
- When the user says something felt slow, loud or in the way, add a dated line under
  "Friction" in PLAN.md §4.
- Keep good screenshots taken while testing in `temp/screenshots/` (git-ignored, with the user's own).

## Editing

- Comments explain *why*, in plain sentences, at the density of the surrounding code.
- A clean build has no warnings at all. Look for `warning`, not just `warning CS`: analyser
  warnings (`CA…`) slipped past a filter on compiler ones.
- Line endings are mixed (CRLF and LF), so preserve each file's own. Don't put Windows paths or
  C# escapes in a Python heredoc: `'C:\repo'` turns into a carriage return. Use the Edit tool.

## Microcopy

Applies to text the user sees in the app: titles, descriptions, status lines, banners, decisions
and errors. Comments, docs and commit messages follow *Editing* above.

The voice is that of a tool for a power user, on their side. It doesn't talk down, doesn't
perform friendliness (not Corporate Memphis: no exclamation marks, emoji, "we" or cheer) and
doesn't wear edginess or hacker culture as a costume. The user works in a corporate environment,
often with someone watching their screen: nothing in the app should embarrass them there.

- Concise, attentive, composed and precise.
- The default manner is restrained and unobtrusive, becoming more perceptible when
  acknowledgement, explanation, or careful presentation adds value.
- Say what it is or what it does for the user. Leave out how it works, but keep a fact the user
  would act on (how long logs are kept).
- Attentive: after an action, say what happened and the way back, since that is what the user
  may need next.
- Composed: when something stops, state the cause and where things stand, then the choices. No
  alarm words, and no reassurance about what obviously wasn't at risk (a copy never touches the
  originals).
- Don't repeat what is already on screen, such as the path the user is copying to. Name the
  actual cause: a full disk and a full quota on a server are different messages.
- One plain sentence is the norm. When a setting has an effect the user wouldn't guess, name it.

| Do not | Do |
|--------|----|
| A file a day, the last seven kept; the timing lines say which budget was broken | Daily log files, the last seven kept |
| The Modified column says Today and Yesterday instead of those dates. The time stays, lined up with the other rows'. | The Modified column says Today and Yesterday instead of those dates. |
| 3 files moved to the Recycle Bin | 3 files moved to the Recycle Bin · Ctrl+Z to undo |
| Copy to D:\Backup stopped: D: is full. 412 of 900 files copied; the originals are untouched. | Copy stopped: disk is full. 412 of 900 files copied. |
| Shows files marked hidden. | Shows files marked hidden. On network drives this includes names starting with a dot. |
| All done! 🎉 Your files are safely in their new home. | 3 files moved to D:\Archive · Ctrl+Z to undo |
| Nuked 3 files. | 3 files moved to the Recycle Bin · Ctrl+Z to undo |
