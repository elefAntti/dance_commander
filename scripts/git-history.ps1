<#
.SYNOPSIS
  A git history location for Dance Commander: the files that used to be in this folder.

.DESCRIPTION
  A script location (ARCHITECTURE.md 6.16). Every file git remembers below the folder the location
  was opened from that is no longer there -- deleted, or moved somewhere else -- one row each, with
  the commit that took it away. F3 shows the last content it had; copying it out (F5) restores it,
  through the transfer engine like any other copy, so a file already at the target name is asked
  about rather than overwritten. Nothing here writes: not the work tree, not the index, not a ref.

    list ""         the rows, one tab-separated line each:
                    name, size, crumb, when, was in, change, by, commit
    read <crumb>    the file's last content, as git would check it out

  The content goes through `git cat-file --filters`, the conversions a checkout applies: line
  endings under core.autocrlf, .gitattributes eol, LFS. A raw blob would hand a Windows checkout
  LF-only text and an LFS pointer instead of the file. That needs the path, so the crumb carries it:
  <blob id>.<repository-relative path, percent-encoded>, which keeps to the crumb alphabet.

  A tracked file missing from the disk comes first, "deleted, not committed", with the index's
  content. Then HEAD's history, newest change first: a file deleted on another branch is that
  branch's. A path tracked again is never shown with an older deletion's content.
  Deletions made in a merge commit itself (resolving a conflict by deleting) are not seen, because
  `git log` shows no diff for merges; deleting on a branch and merging it is seen.

.EXAMPLE
  In tools.json:

    "listings": [
      {
        "id": "git-gone",
        "title": "Deleted files",
        "exec": "pwsh",
        "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/git-history.ps1",
                 "${repoRoot}"],
        "script": true,
        "inRepository": true,
        "extract": "^(?<name>[^\\t\\n]+)\\t(?<size>\\d*)\\t(?<crumb>[^\\t\\n]+)\\t(?<when>[^\\t\\n]*)\\t(?<folder>[^\\t\\n]*)\\t(?<change>[^\\t\\n]*)\\t(?<by>[^\\t\\n]*)\\t(?<commit>[^\\t\\r\\n]*)\\r?$",
        "columns": [
          { "group": "folder", "header": "Was in", "width": 200 },
          { "group": "change", "header": "Change", "width": 140 },
          { "group": "when", "header": "When", "width": 130 },
          { "group": "by", "header": "By", "width": 110 },
          { "group": "commit", "header": "Commit", "width": 240, "flexible": true }
        ],
        "showModified": false
      }
    ]

  Then "Deleted files" is on the context menu of any folder inside a repository, listing what used
  to be below that folder, and bookmarkable as from:C:/src/app?git-gone.

.EXAMPLE
  From a prompt, in a folder of a repository:

    pwsh -File git-history.ps1 list ""
#>
param(
    # ${repoRoot}: the top of the repository, which git's paths are relative to. Passed in rather
    # than asked of git: one process fewer on a listing that starts PowerShell as it is.
    [Parameter(Position = 0)] [string] $RepoRoot = '',

    # Added by Dance Commander: list or read.
    [Parameter(Position = 1)] [string] $Verb = '',

    # Added by Dance Commander: the row's crumb for read; empty at the root.
    [Parameter(Position = 2)] [string] $Crumbs = ''
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

function Fail([string] $message, [int] $code = 1) {
    [Console]::Error.WriteLine($message)
    exit $code
}

if ($Verb -notin @('list', 'read')) {
    Fail "git-history.ps1: unknown verb '$Verb' (expected list or read)." 2
}
if (-not $RepoRoot) { Fail 'This folder is not in a git repository.' }
$top = $RepoRoot

# ---- running git ---------------------------------------------------------------------------------

<#
  git through Process rather than `& git`: PowerShell decodes a native command's output with the
  console's code page, which mangles a file called "säännöt.md", and before 7.4 it re-encodes a
  pipe as text, which corrupts a binary file on its way to stdout. The arguments go as a list, so
  a path is one argument whatever it contains.

  By its full path, found the way PowerShell finds commands: Process.Start with a bare "git" asks
  Windows, whose search tries the current directory first -- the folder being browsed, so a
  repository carrying a git.exe of its own would have it run.
#>
$gitExe = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $gitExe) { Fail 'git is not on PATH.' }

function Git([string[]] $arguments, [System.IO.Stream] $copyTo = $null, [string] $in = $null) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($gitExe)
    # Reading never needs the index lock, and taking it would collide with the user's terminal.
    $info.ArgumentList.Add('--no-optional-locks')
    foreach ($a in $arguments) { $info.ArgumentList.Add($a) }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $null -ne $in
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    # A partial clone would otherwise go to the network for every blob a size is asked of.
    $info.Environment['GIT_NO_LAZY_FETCH'] = '1'

    $process = [System.Diagnostics.Process]::Start($info)
    $stderr = $process.StandardError.ReadToEndAsync()
    if ($null -ne $in) {
        $writer = [System.IO.StreamWriter]::new($process.StandardInput.BaseStream, [System.Text.UTF8Encoding]::new($false))
        $writer.Write($in)
        $writer.Close()
    }

    $text = $null
    if ($null -ne $copyTo) { $process.StandardOutput.BaseStream.CopyTo($copyTo) }
    else { $text = $process.StandardOutput.ReadToEnd() }
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        # git's own words: it knows why it failed.
        $why = @($stderr.Result -split "`r?`n" | Where-Object { $_.Trim() }) | Select-Object -First 1
        Fail $(if ($why) { $why } else { "git $($arguments[0]) exited with code $($process.ExitCode)." })
    }
    return $text
}

# ---- read ----------------------------------------------------------------------------------------

if ($Verb -eq 'read') {
    # Only the shape this script prints; anything else was typed into the address bar by hand.
    if ($Crumbs -notmatch '^(?<blob>[0-9a-f]{40}|[0-9a-f]{64})\.(?<path>[A-Za-z0-9._~%-]+)$') {
        Fail "Not a crumb this script printed: '$Crumbs'." 2
    }
    $blob = $Matches.blob
    $path = [Uri]::UnescapeDataString($Matches.path)

    # From the top of the repository, because the path is relative to it: it only chooses which
    # .gitattributes rules apply, never which bytes are read -- the blob id does that.
    $stdout = [Console]::OpenStandardOutput()
    Git @('-C', $top, 'cat-file', '--filters', "--path=$path", $blob) $stdout | Out-Null
    $stdout.Flush()
    exit 0
}

# ---- list ----------------------------------------------------------------------------------------

if ($Crumbs -ne '') { Fail "Nothing below ${Crumbs}: the rows are files." 3 }

$here = (Get-Location).ProviderPath

$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$rows = [System.Collections.Generic.List[object]]::new()

# A submodule's "blob" is a commit, and a symlink's is the text of its target; neither comes back
# as the file it was by copying bytes out.
function Restorable([string] $mode) { return $mode.TrimStart(':') -notin @('160000', '120000') }

<#
  First what git still tracks and the disk no longer has: deleted and not committed, the commonest
  accident of all. The content is the index's, which is HEAD's unless something was staged since;
  after `git rm` the index no longer has it, and HEAD's is the last. Everything tracked goes into
  $seen, missing or not, because an old deletion of a path that is tracked again is not its last
  content -- restoring that would hand back an older version as if it were the file.
#>
$tracked = @{}
foreach ($entry in (Git @('ls-tree', '-r', '-z', '--full-name', 'HEAD', '--', '.')).Split([char] 0)) {
    # mode SP type SP blob TAB path
    if ($entry -match '^(?<mode>\d+) blob (?<blob>[0-9a-f]+)\t(?<path>.+)$') { $tracked[$Matches.path] = $Matches }
}
foreach ($entry in (Git @('ls-files', '-s', '-z', '--full-name', '--', '.')).Split([char] 0)) {
    # mode SP blob SP stage TAB path; a conflict's stages 1-3 are not one content to restore.
    if ($entry -match '^(?<mode>\d+) (?<blob>[0-9a-f]+) 0\t(?<path>.+)$') { $tracked[$Matches.path] = $Matches }
}
foreach ($path in $tracked.Keys) {
    [void] $seen.Add($path)
    $entry = $tracked[$path]
    if (-not (Restorable $entry.mode)) { continue }
    if (Test-Path -LiteralPath (Join-Path $top $path)) { continue }
    $rows.Add([pscustomobject]@{ Path = $path; Blob = $entry.blob; To = $null; Commit = $null; When = $null; By = ''; Title = '' })
}

<#
  Then history. Per commit: hash \0 date \0 author \0 subject \0, then per change
  ":mode mode blob blob STATUS" \0 path \0, and a second path for a rename. Every field is ended by
  NUL, which commit text cannot contain, and read by position, so no subject or name can pass for a
  header or a change. -z so that a name with a quote, a tab or a non-ASCII letter arrives as it is
  rather than in git's quoted form. -M explicitly, whatever diff.renames says: a file that was only
  moved should say where it went, not that it was deleted.
#>
$log = Git @('log', '-M', '--diff-filter=DR', '--raw', '--no-abbrev', '-z',
    '--format=%H%x00%aI%x00%an%x00%s', '--', '.')
$tokens = $log.Split([char] 0)
$header = $null

$i = 0
while ($i -lt $tokens.Count) {
    $token = $tokens[$i].TrimStart("`n")

    if ($token -match '^(?<mode>:\d{6}) \d{6} (?<blob>[0-9a-f]{40,64}) [0-9a-f]{40,64} (?<status>[A-Z])\d*$') {
        $meta = $Matches
        $from = $tokens[$i + 1]
        $to = $null
        if ($meta.status -in @('R', 'C')) { $to = $tokens[$i + 2]; $i += 3 } else { $i += 2 }

        # Newest first, so the first time a path is met is the last time it went away -- its
        # last content. A path deleted, brought back and deleted again is one row, the latest.
        if ($null -eq $header -or -not $seen.Add($from)) { continue }
        if (-not (Restorable $meta.mode)) { continue }

        # Brought back since, and not added: it is there to be opened.
        if (Test-Path -LiteralPath (Join-Path $top $from)) { continue }

        $rows.Add([pscustomobject]@{
            Path = $from; Blob = $meta.blob; To = $to
            Commit = $header.Commit; When = $header.When; By = $header.By; Title = $header.Title
        })
    }
    elseif ($token -match '^[0-9a-f]{40}$|^[0-9a-f]{64}$' -and $i + 3 -lt $tokens.Count) {
        $when = [DateTimeOffset]::MinValue
        $header = @{
            Commit = $token
            When   = $(if ([DateTimeOffset]::TryParse($tokens[$i + 1], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $when)) { $when } else { $null })
            By     = $tokens[$i + 2]
            Title  = $tokens[$i + 3]
        }
        $i += 4
    }
    else { $i++ }
}

# Sizes from one process for all of them. The blob's size, before line endings are converted: a
# checkout only ever adds to it, and a copy that comes out bigger than listed is fine, smaller is not.
$sizes = @{}
if ($rows.Count -gt 0) {
    $answer = Git @('cat-file', '--batch-check') $null ((($rows | ForEach-Object Blob) -join "`n") + "`n")
    foreach ($line in $answer -split "`n") {
        if ($line -match '^(?<blob>[0-9a-f]+) blob (?<size>\d+)$') { $sizes[$Matches.blob] = $Matches.size }
    }
}

# Rows go out as UTF-8 regardless of the console's code page.
$out = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), [System.Text.UTF8Encoding]::new($false))
$out.NewLine = "`n"

function Clean([string] $text) {
    # Tabs and line breaks would split a row, and other control characters in commit text are
    # nothing anyone can read; nothing else needs escaping.
    if ($null -eq $text) { return '' }
    return ($text -replace '[\x00-\x1f\x7f]+', ' ').Trim()
}

# Where something is, said from the folder the location was opened from, the way Windows writes it.
function FromHere([string] $repoPath) {
    return [System.IO.Path]::GetRelativePath($here, (Join-Path $top $repoPath))
}

foreach ($row in $rows) {
    $name = $row.Path.Substring($row.Path.LastIndexOf('/') + 1)
    $folder = [System.IO.Path]::GetDirectoryName((FromHere $row.Path))
    $change = if ($row.To) { "moved to $(FromHere $row.To)" } elseif ($row.Commit) { 'deleted' } else { 'deleted, not committed' }
    $size = if ($sizes.ContainsKey($row.Blob)) { $sizes[$row.Blob] } else { '' }
    $crumb = "$($row.Blob).$([Uri]::EscapeDataString($row.Path))"
    $when = if ($row.When) { $row.When.ToLocalTime().ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture) } else { '' }
    $commit = if ($row.Commit) { "$($row.Commit.Substring(0, 8)) $($row.Title)" } else { '' }

    $out.WriteLine((@((Clean $name), $size, $crumb, $when, (Clean $folder), (Clean $change), (Clean $row.By), (Clean $commit)) -join "`t"))
}
$out.Flush()
exit 0
