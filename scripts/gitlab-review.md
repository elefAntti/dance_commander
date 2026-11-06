# GitLab review files

Two Dance Commander tools for merge request reviews, and the file format between them.

- **Download review comments** (`gitlab-review-download.ps1`) writes the open threads of the
  branch's merge request to `mr-<iid>-comments.md`, with the code around each one.
- **Check review file** and **Upload review to GitLab** (`gitlab-review-upload.ps1`) read a review
  file you wrote, `*.review.md`, and post its comments on the merge request.

The setup for `tools.json` is at the end.

---

## A review file

A review file is Markdown, so it reads well in any editor. Give it a name ending in `.review.md`;
that's what puts the two tools on its right-click menu.

````markdown
# Review of the parser change

mr: https://gitlab.example.com/group/app/-/merge_requests/5107

Anything up here is a note to yourself and is not posted.

## src/Parser/Lexer.cs:42
This allocates on every token. Could it reuse the buffer from `Reset`?

## src/Parser/Lexer.cs:17 (old)
The removed check guarded against an empty input. Is that handled somewhere else now?

## general
Looks good overall. The tests in `LexerTests` cover the new cases well.

## reply 2d578100 (resolve)
Agreed, done in 4f1c2a9.
````

### The parts

**Settings**, before the first comment, one per line:

| Setting | Means | When left out |
|---|---|---|
| `mr: <url>` | The merge request, as the browser shows it. It also names the GitLab server and the project, so the file can live anywhere. | The open merge request of the branch checked out where the file is. |
| `mr: 5107` | The merge request by number, in the project of the repository the file is in. `!5107` works too. | |
| `mode: draft` | Comments become drafts: yours alone until you press **Submit review** on the merge request in GitLab. You can still edit or delete them there. | This is the default. |
| `mode: publish` | Comments are posted for everyone straight away, each one notifying as it arrives. | |

Other text before the first comment, like the title, is ignored.

**Comments**: each begins with a `##` heading saying where it goes, and runs until the next one.

| Heading | Puts the comment |
|---|---|
| `## path/to/file.cs:42` | On line 42 of the file as the merge request has it. |
| `## path/to/file.cs:17 (old)` | On line 17 of the file *before* the change, for a line the merge request removes. |
| `## general` | On the merge request as a whole, as a new thread. |
| `## reply <id>` | In an existing thread, the way the downloaded comments file shows it (see below). |
| `## reply <id> (resolve)` | The same, and marks the thread resolved. |

- Paths are from the repository's root, with `/`. Backticks around the path are fine, and so is a
  `3. ` number in front, so headings copied from a downloaded comments file work as they are.
- A thread id can be shortened, like a git hash, as long as it's still unique on the merge request.
- The comment itself is GitLab Markdown: code blocks, ` ```suggestion ` blocks, lists, `@mentions`.
  For a heading inside a comment, use `###` or deeper; a `##` line is always read as the start of
  the next comment, except inside a code block.
- `<!-- HTML comments -->` are removed before posting, so you can keep notes beside a comment.

### Which lines can take a comment

GitLab attaches comments to the merge request's diff, so:

- The file must be one the merge request changes. A remark about any other file goes under
  `## general`, naming the file.
- Any line of a changed file works, not just the changed ones: GitLab shows a comment on an
  unchanged line the way it does when you expand the diff around it.
- A file that is new has no `(old)` lines, and a deleted one has only `(old)` lines.
- Line numbers are those of the merge request's latest version. If the branch gets new commits after
  you wrote the file, run **Check** again to see whether the lines still say what you commented on.

---

## Checking and uploading

**Check review file** reads the file, finds the merge request and places every comment, then prints
what would be posted, with the code on each comment's line so you can see it landed where you meant.
It posts nothing.

**Upload review to GitLab** does the same checks first. If any comment has a problem (a heading it
can't read, a file the merge request doesn't change, a line past the end of the file, a thread that
isn't there), it lists every problem and posts **nothing**. Otherwise it posts the comments in order.

Uploading the same file twice is safe: a comment that is already on the merge request, or among your
drafts, at the same place with the same text, is skipped. If an upload stops halfway (the network,
a token that expired), fix the cause and upload again; only the rest is posted. The review file itself
is never changed.

### The token

Comments appear under the name of the token's owner, so uploading needs a **personal** access token
of yours with the **api** scope. It's looked for in this order:

1. The environment variable `GITLAB_REVIEW_TOKEN`.
2. Windows Credential Manager, `DanceCommander:script:gitlab-review-token`. To store it, run this
   in a terminal; it asks for the token rather than taking it on the command line:

   ```
   cmdkey /generic:DanceCommander:script:gitlab-review-token /user:gitlab /pass
   ```

3. The token the Pipelines listing uses (`DanceCommander:script:gitlab-token`).

A project or group access token belongs to a bot, so the upload refuses it: the comments would come
from the bot, not from you. A check still runs with it, and says so at the end.

Downloading only reads, so it's happy with the Pipelines listing's token (`read_api` is enough).

---

## A downloaded comments file

**Download review comments**, run on a folder inside a repository, finds the open merge request of
the branch checked out there and writes `mr-<iid>-comments.md` into that folder. It never overwrites:
if the name is taken, it writes `mr-<iid>-comments (2).md`, and so on.

The file has the merge request's title, link, branches and author, then one section per open
thread:

````markdown
## 1. `src/Parser/Lexer.cs:42`

```csharp
  38 |         ...
> 42 |         var token = new Token(text);
  46 |         ...
```

**@reviewer** · 2026-10-21 09:03

> This allocates on every token.

Reply in a review file under `## reply 2d578100574dd091673d5a38f21f7904cf34d057` · [open in GitLab](...)
````

- The heading uses the same `path:line` form as a review file, and `(old)` for a thread on a
  removed line, whose code then comes from before the change.
- A thread written against an earlier version of the merge request says so: the code shown is the
  version the reviewer saw, and the line may have moved since.
- Threads not on a line of code come last, under **General threads**.
- A thread counts as open while any of its comments is unresolved.
- Comments on the merge request that aren't threads (a plain **Comment** rather than **Start a
  thread**) have no resolved state, so every one of them is written, under **Comments** at the end.
  Replying to one in a review file makes it a thread.
- Images and attachments link to GitLab, so they open from outside it too.

To answer the threads, start a review file and copy the `## reply <id>` lines into it.

---

## Setting up the tools

In `%APPDATA%\DanceCommander\tools.json`, under `"tools"` (adjust the script path to where this
folder is):

```json
{
  "id": "gitlab-review-download",
  "title": "Download review comments",
  "group": "GitLab",
  "appliesTo": "directory|background",
  "exec": "pwsh",
  "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/gitlab-review-download.ps1",
           "${remoteUrl}", "${branch}", "${dir}"],
  "inRepository": true,
  "launch": "job"
},
{
  "id": "gitlab-review-check",
  "title": "Check review file",
  "group": "GitLab",
  "appliesTo": "file",
  "when": { "glob": "*.review.md" },
  "exec": "pwsh",
  "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/gitlab-review-upload.ps1",
           "check", "${path}"],
  "launch": "capture"
},
{
  "id": "gitlab-review-upload",
  "title": "Upload review to GitLab",
  "group": "GitLab",
  "appliesTo": "file",
  "when": { "glob": "*.review.md" },
  "exec": "pwsh",
  "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/gitlab-review-upload.ps1",
           "upload", "${path}"],
  "launch": "job"
}
```

From a prompt, the same scripts take a merge request directly:

```
pwsh -File gitlab-review-download.ps1 "" "" . https://gitlab.example.com/group/app/-/merge_requests/5107
pwsh -File gitlab-review-upload.ps1 check my-change.review.md
```

`DC_GITLAB_API` overrides the API address (`https://<host>/api/v4` by default) for a GitLab behind
another name or port.
