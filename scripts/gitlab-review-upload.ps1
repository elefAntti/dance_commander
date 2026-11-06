<#
.SYNOPSIS
  Checks a review file, or uploads its comments to a GitLab merge request.

.DESCRIPTION
  A Dance Commander tool (tools.json) for *.review.md files. The format is in gitlab-review.md, next
  to this script.

    check  <file>   read the file, find the merge request, place every comment on its line, and
                    print what would be posted -- the line's code beside each comment. Posts nothing.
    upload <file>   the same checks, then post. Nothing is posted unless every comment passes.

  By default the comments become drafts: yours alone until you press "Submit review" in GitLab, where
  you can still edit or delete them. "mode: publish" in the file posts them straight away.

  Uploading twice is safe: a comment already on the merge request (or among your drafts) with the
  same place and text is skipped, so after a failure halfway the second run posts only the rest.
  The file itself is never written to.

  The token. Comments appear under the token's owner, so uploading wants a personal access token
  with the api scope: GITLAB_REVIEW_TOKEN, else the Credential Manager entry
  "DanceCommander:script:gitlab-review-token" (store it with
  cmdkey /generic:DanceCommander:script:gitlab-review-token /user:gitlab /pass
  which asks for it rather than taking it on the command line), else the general token
  (gitlab-common.ps1). A token belonging to a bot -- project and group access tokens are -- is
  refused, since the comments would not be from you.

.EXAMPLE
  In tools.json:

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
#>
param(
    [Parameter(Position = 0)] [string] $Verb = '',
    [Parameter(Position = 1)] [string] $ReviewFile = ''
)

. (Join-Path $PSScriptRoot 'gitlab-common.ps1')

if ($Verb -notin @('check', 'upload')) { Fail "gitlab-review-upload.ps1: unknown verb '$Verb' (expected check or upload)." 2 }
if (-not $ReviewFile -or -not (Test-Path -LiteralPath $ReviewFile -PathType Leaf)) { Fail "No review file '$ReviewFile'." 2 }
$ReviewFile = (Resolve-Path -LiteralPath $ReviewFile).ProviderPath

$out = New-Utf8Stdout
function Say([string] $text = '') { $out.WriteLine($text) }

# ---- reading the file ----------------------------------------------------------------------------

<#
  The file, as { Settings; Entries; Errors }. Each entry: Kind (code, general, reply), At (its
  heading's line number), and Path, Line, Old for code; Discussion, Resolve for reply; Body.
#>
function Read-ReviewFile([string] $file) {
    $text = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
    $lines = $text -split "`r?`n"

    $settings = @{}
    $entries = [System.Collections.Generic.List[object]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()
    $current = $null
    $body = $null
    $fence = $null   # the opening fence while inside a fenced code block: "## " there is code, not a heading

    function Close-Entry {
        if ($null -eq $current) { return }
        # HTML comments are notes to yourself, never posted.
        $t = [regex]::Replace(($body -join "`n"), '(?s)<!--.*?-->', '')
        $t = $t.Trim("`n", "`r", ' ', "`t")
        if (-not $t) { $errors.Add("Line $($current.At): '## $($current.Heading)' has no comment under it.") }
        $current.Body = $t
        $entries.Add($current)
    }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $number = $i + 1

        if ($line -match '^\s{0,3}(`{3,}|~{3,})') {
            $marker = $Matches[1]
            if ($null -eq $fence) { $fence = $marker }
            elseif ($marker[0] -eq $fence[0] -and $marker.Length -ge $fence.Length -and $line.Trim() -eq $marker) { $fence = $null }
        }
        elseif ($null -eq $fence -and $line -match '^##\s+(.*?)\s*#*\s*$') {
            $heading = $Matches[1]
            # Copied from a downloaded comments file: "3. `src/a.cs:42`" -- the number and the
            # backticks go.
            $h = ($heading -replace '^\d+\.\s+', '') -replace '`', ''
            $entry = $null
            if ($h -match '^general$') {
                $entry = [pscustomobject]@{ Kind = 'general'; At = $number; Heading = $heading; Body = '' }
            }
            elseif ($h -match '^reply\s+([0-9a-fA-F]{8,64})(\s+\(resolve\))?$') {
                $entry = [pscustomobject]@{ Kind = 'reply'; At = $number; Heading = $heading; Discussion = $Matches[1].ToLowerInvariant(); Resolve = [bool] $Matches[2]; Body = '' }
            }
            elseif ($h -match '^(.+?):(\d+)(\s+\(old\))?$') {
                $entry = [pscustomobject]@{ Kind = 'code'; At = $number; Heading = $heading; Path = $Matches[1].Trim().Replace('\', '/').TrimStart('/'); Line = [int] $Matches[2]; Old = [bool] $Matches[3]; Body = '' }
                if ($entry.Line -lt 1) { $errors.Add("Line ${number}: line numbers start at 1.") }
            }
            else {
                $errors.Add("Line ${number}: '## $heading' is not a comment heading (path:line, general, or reply <id>). For a heading inside a comment, use ###.")
            }
            if ($null -ne $entry) {
                Close-Entry
                $current = $entry
                $body = [System.Collections.Generic.List[string]]::new()
                continue
            }
        }

        if ($null -ne $current) {
            $body.Add($line)
        }
        elseif ($line -match '^\s*(mr|mode)\s*:\s*(.+?)\s*$') {
            # Before the first comment: settings, and anything else is notes that are not posted.
            $settings[$Matches[1].ToLowerInvariant()] = $Matches[2]
        }
    }
    Close-Entry

    if ($null -ne $fence) { $errors.Add("A code block opened with $fence is never closed; everything after it was read as code.") }
    if ($entries.Count -eq 0 -and $errors.Count -eq 0) { $errors.Add("No comments: each starts with a '## path:line', '## general' or '## reply <id>' heading.") }

    $mode = if ($settings.ContainsKey('mode')) { $settings['mode'].ToLowerInvariant() } else { 'draft' }
    if ($mode -notin @('draft', 'publish')) { $errors.Add("mode: '$($settings['mode'])' -- expected draft or publish.") }

    return [pscustomobject]@{ Mr = $settings['mr']; Mode = $mode; Entries = $entries.ToArray(); Errors = $errors }
}

$review = Read-ReviewFile $ReviewFile
if ($review.Errors.Count -gt 0) {
    foreach ($e in $review.Errors) { [Console]::Error.WriteLine($e) }
    Fail "$($review.Errors.Count) problem(s) in $(Split-Path -Leaf $ReviewFile); nothing was posted."
}

# ---- which merge request, and as whom ------------------------------------------------------------

$iid = $null
$fromUrl = if ($review.Mr) { Resolve-MergeRequestUrl $review.Mr } else { $null }
if ($fromUrl) {
    $project = @{ Host = $fromUrl.Host; Path = $fromUrl.Path }
    $iid = $fromUrl.Iid
}
else {
    # The repository the file is in names the project, and, without "mr:", its branch the merge request.
    $git = Read-GitContext (Split-Path -Parent $ReviewFile)
    if (-not $git.RemoteUrl) { Fail "The review file is not in a repository with a remote: give the merge request's URL as 'mr:'." }
    $project = Resolve-Project $git.RemoteUrl
    if ($null -eq $project) { Fail "Cannot tell which GitLab project '$($git.RemoteUrl)' is: give the merge request's URL as 'mr:'." }
    if ($review.Mr) {
        if ($review.Mr -notmatch '^!?(\d+)$') { Fail "mr: '$($review.Mr)' is neither a merge request URL nor a number." }
        $iid = $Matches[1]
    }
}

$token = if ($env:GITLAB_REVIEW_TOKEN) { $env:GITLAB_REVIEW_TOKEN } else { Read-StoredCredential 'DanceCommander:script:gitlab-review-token' }
Connect-GitLab $project.Host $project.Path 'DanceCommander-gitlab-review-upload/1' $token

if (-not $iid) {
    if (-not $git.Branch) { Fail "HEAD is detached: no branch to find the merge request from. Give it as 'mr:'." }
    $open = @(Find-OpenMergeRequests $git.Branch)
    if ($open.Count -eq 0) { Fail "$($git.Branch) has no open merge request in $($project.Path). Give it as 'mr:'." }
    if ($open.Count -gt 1) { Fail "$($git.Branch) has $($open.Count) open merge requests ($((@($open | ForEach-Object { "!$($_.iid)" })) -join ', ')). Say which with 'mr:'." }
    $iid = "$($open[0].iid)"
}

$me = Get-GitLab '~/user'
$isBot = (Has $me 'bot') -and $me.bot
$botProblem = "The token belongs to @$($me.username), a bot (a project or group access token): the comments would be from it, not you. " +
    "Store a personal access token with the api scope as DanceCommander:script:gitlab-review-token " +
    "(cmdkey /generic:DanceCommander:script:gitlab-review-token /user:gitlab /pass) or set GITLAB_REVIEW_TOKEN."
# A check still runs with it, to show where every comment would go.
if ($isBot -and $Verb -eq 'upload') { Fail $botProblem }

$mr = Get-GitLab "/merge_requests/$iid"
if (-not (Has $mr 'diff_refs') -or -not (Has $mr.diff_refs 'head_sha')) { Fail "!$iid has no diff yet; GitLab is still preparing it. Try again in a moment." }
$refs = $mr.diff_refs

# ---- placing each comment ------------------------------------------------------------------------

$diffs = @{}
foreach ($d in (Get-AllPages "/merge_requests/$iid/diffs")) {
    $diffs["new:$($d.new_path)"] = $d
    $diffs["old:$($d.old_path)"] = $d
}

<#
  A file's diff as its hunks: each with its ranges and its lines, a line being Old and New numbers
  ($null on the side it is not on).
#>
function Read-Hunks([string] $diff) {
    $hunks = [System.Collections.Generic.List[object]]::new()
    $hunk = $null
    $o = 0; $n = 0
    foreach ($l in ($diff -split "`n")) {
        if ($l -match '^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@') {
            $hunk = [pscustomobject]@{
                OldStart = [int] $Matches[1]; OldCount = $(if ($Matches[2]) { [int] $Matches[2] } else { 1 })
                NewStart = [int] $Matches[3]; NewCount = $(if ($Matches[4]) { [int] $Matches[4] } else { 1 })
                Lines    = [System.Collections.Generic.List[object]]::new()
            }
            $hunks.Add($hunk)
            $o = $hunk.OldStart; $n = $hunk.NewStart
            continue
        }
        if ($null -eq $hunk -or $l.Length -eq 0) { continue }
        switch ($l[0]) {
            '+' { $hunk.Lines.Add(@{ Old = $null; New = $n }); $n++ }
            '-' { $hunk.Lines.Add(@{ Old = $o; New = $null }); $o++ }
            ' ' { $hunk.Lines.Add(@{ Old = $o; New = $n }); $o++; $n++ }
            # "\ No newline at end of file" and anything else is not a line of either file.
        }
    }
    return , $hunks.ToArray()
}

<#
  The (Old, New) pair GitLab wants for a line: a changed line has one side, an unchanged one both.
  Outside the hunks a line is unchanged, and its other number is shifted by what the hunks before it
  added or removed -- which is what lets a comment go on a line GitLab only shows when expanded.
#>
function Place-Line($hunks, [int] $number, [bool] $old) {
    $mine = if ($old) { 'Old' } else { 'New' }
    foreach ($h in $hunks) {
        foreach ($l in $h.Lines) {
            if ($l[$mine] -eq $number) { return @{ Old = $l.Old; New = $l.New } }
        }
    }
    $shift = 0
    foreach ($h in $hunks) {
        $start = if ($old) { $h.OldStart } else { $h.NewStart }
        $count = if ($old) { $h.OldCount } else { $h.NewCount }
        # A hunk of count 0 sits after line $start; one with lines ends at $start + $count - 1.
        if ($start + [Math]::Max($count, 1) -le $number) { $shift += $h.NewCount - $h.OldCount }
    }
    if ($old) { return @{ Old = $number; New = $number + $shift } }
    return @{ Old = $number - $shift; New = $number }
}

$problems = [System.Collections.Generic.List[string]]::new()
$discussions = Get-AllPages "/merge_requests/$iid/discussions"
$discussionById = @{}
foreach ($d in $discussions) { $discussionById[$d.id] = $d }

foreach ($e in $review.Entries) {
    $e | Add-Member -NotePropertyName Position -NotePropertyValue $null
    $e | Add-Member -NotePropertyName Code -NotePropertyValue $null
    $where = "Line $($e.At) ($($e.Heading))"

    if ($e.Kind -eq 'reply') {
        if (-not $discussionById.ContainsKey($e.Discussion)) {
            # Short ids, the way git shortens a sha.
            $matching = @($discussionById.Keys | Where-Object { $_.StartsWith($e.Discussion) })
            if ($matching.Count -eq 1) { $e.Discussion = $matching[0] }
            else { $problems.Add("${where}: !$iid has $(if ($matching.Count) { 'several threads' } else { 'no thread' }) with id $($e.Discussion)."); continue }
        }
        continue
    }
    if ($e.Kind -ne 'code') { continue }

    $diff = $diffs["$(if ($e.Old) { 'old' } else { 'new' }):$($e.Path)"]
    if ($null -eq $diff) {
        $problems.Add("${where}: !$iid does not change $($e.Path), and GitLab only puts comments on files it changes. Write it under '## general' and name the file there.")
        continue
    }
    if ($e.Old -and $diff.new_file) { $problems.Add("${where}: $($e.Path) is new in !$iid; it has no old lines."); continue }
    if (-not $e.Old -and $diff.deleted_file) { $problems.Add("${where}: $($e.Path) is deleted by !$iid; comment on its old lines with '(old)'."); continue }
    if (-not $diff.diff) { $problems.Add("${where}: GitLab does not show the diff of $($e.Path) (too large or collapsed), so a comment cannot be placed on it. Use '## general'."); continue }

    $sha = if ($e.Old) { $refs.base_sha } else { $refs.head_sha }
    $source = Get-SourceLines $e.Path $sha
    if ($null -eq $source) { $problems.Add("${where}: could not read $($e.Path) at $($sha.Substring(0, 8)) to find line $($e.Line)."); continue }
    if ($e.Line -gt $source.Count) {
        $side = if ($e.Old) { 'before the change' } else { 'in the merge request' }
        $problems.Add("${where}: $($e.Path) has $($source.Count) lines $side; there is no line $($e.Line).")
        continue
    }
    $e.Code = $source[$e.Line - 1]

    $placed = Place-Line (Read-Hunks $diff.diff) $e.Line $e.Old
    $position = [ordered]@{
        position_type = 'text'
        base_sha      = $refs.base_sha
        start_sha     = $refs.start_sha
        head_sha      = $refs.head_sha
        old_path      = $diff.old_path
        new_path      = $diff.new_path
    }
    if ($null -ne $placed.Old) { $position.old_line = $placed.Old }
    if ($null -ne $placed.New) { $position.new_line = $placed.New }
    $e.Position = $position
}

if ($problems.Count -gt 0) {
    foreach ($p in $problems) { [Console]::Error.WriteLine($p) }
    Fail "$($problems.Count) comment(s) cannot be placed on !$iid; nothing was posted."
}

# ---- what is already there -----------------------------------------------------------------------

function Same-Text([string] $a, [string] $b) {
    return ($a -replace "`r", '').Trim() -eq ($b -replace "`r", '').Trim()
}

function Same-Position($p, $q) {
    if ($null -eq $p -or $null -eq $q) { return $false }
    $pl = if (Has $p 'new_line') { $p.new_line } else { $null }
    $ql = if ($q.Contains('new_line')) { $q.new_line } else { $null }
    $po = if (Has $p 'old_line') { $p.old_line } else { $null }
    $qo = if ($q.Contains('old_line')) { $q.old_line } else { $null }
    return $p.new_path -eq $q.new_path -and "$pl" -eq "$ql" -and "$po" -eq "$qo"
}

# My comments already on the merge request, and my drafts, as (kind, place, text).
$mine = [System.Collections.Generic.List[object]]::new()
foreach ($d in $discussions) {
    $notes = @($d.notes)
    for ($k = 0; $k -lt $notes.Count; $k++) {
        $note = $notes[$k]
        if ($note.author.id -ne $me.id -or ((Has $note 'system') -and $note.system)) { continue }
        $position = if ($k -eq 0 -and (Has $note 'position')) { $note.position } else { $null }
        $mine.Add(@{ Discussion = $d.id; First = ($k -eq 0); Position = $position; Text = $note.body })
    }
}
foreach ($draft in @(Get-GitLab "/merge_requests/$iid/draft_notes" | Where-Object { $null -ne $_ })) {
    $position = if ((Has $draft 'position') -and (Has $draft.position 'new_path')) { $draft.position } else { $null }
    $reply = if (Has $draft 'discussion_id') { $draft.discussion_id } else { $null }
    $mine.Add(@{ Discussion = $reply; First = ($null -eq $reply); Position = $position; Text = $draft.note; Draft = $true })
}

function Already-There($e) {
    foreach ($m in $mine) {
        if (-not (Same-Text $m.Text $e.Body)) { continue }
        switch ($e.Kind) {
            'code' { if ($m.First -and (Same-Position $m.Position $e.Position)) { return $true } }
            'general' { if ($m.First -and $null -eq $m.Position) { return $true } }
            'reply' { if (-not $m.First -and $m.Discussion -eq $e.Discussion) { return $true } }
        }
    }
    return $false
}

# ---- saying what will happen ---------------------------------------------------------------------

function Label($e) {
    switch ($e.Kind) {
        'code' { return "$($e.Path):$($e.Line)$(if ($e.Old) { ' (old)' })" }
        'general' { return 'general' }
        'reply' {
            $first = @($discussionById[$e.Discussion].notes)[0]
            $quote = ($first.body -split "`r?`n")[0]
            if ($quote.Length -gt 60) { $quote = $quote.Substring(0, 57) + '...' }
            return "reply to @$($first.author.username) `"$quote`"$(if ($e.Resolve) { ', and resolve' })"
        }
    }
}

Say "Merge request: !$iid $($mr.title)"
Say "               $($mr.web_url)"
Say "As:            @$($me.username)"
Say "Mode:          $(if ($review.Mode -eq 'draft') { 'draft -- yours alone until you press Submit review in GitLab' } else { 'publish -- posted for everyone straight away' })"
Say ''

$toPost = [System.Collections.Generic.List[object]]::new()
$skipped = 0
$n = 0
foreach ($e in $review.Entries) {
    $n++
    $there = Already-There $e
    if ($there) { $skipped++ } else { $toPost.Add($e) }
    Say "$n. $(Label $e)$(if ($there) { '  -- already on GitLab, skipped' })"
    if ($null -ne $e.Code) { Say "     $($e.Line) | $($e.Code.TrimEnd())" }
    foreach ($bodyLine in ($e.Body -split "`n")) { Say "   > $bodyLine" }
    Say ''
}

if ($Verb -eq 'check') {
    Say "$($toPost.Count) to post$(if ($skipped) { ", $skipped already there" }). This was a check: nothing was posted."
    if ($isBot) { Say ''; Say "Uploading would be refused. $botProblem" }
    $GitLab.Http.Dispose()
    exit 0
}

# ---- posting -------------------------------------------------------------------------------------

$posted = 0
try {
    foreach ($e in $toPost) {
        if ($review.Mode -eq 'draft') {
            $request = [ordered]@{ note = $e.Body }
            if ($e.Kind -eq 'code') { $request.position = $e.Position }
            if ($e.Kind -eq 'reply') {
                $request.in_reply_to_discussion_id = $e.Discussion
                if ($e.Resolve) { $request.resolve_discussion = $true }
            }
            [void] (Invoke-GitLab 'POST' "/merge_requests/$iid/draft_notes" $request)
        }
        else {
            switch ($e.Kind) {
                'code' { [void] (Invoke-GitLab 'POST' "/merge_requests/$iid/discussions" ([ordered]@{ body = $e.Body; position = $e.Position })) }
                'general' { [void] (Invoke-GitLab 'POST' "/merge_requests/$iid/discussions" @{ body = $e.Body }) }
                'reply' {
                    [void] (Invoke-GitLab 'POST' "/merge_requests/$iid/discussions/$($e.Discussion)/notes" @{ body = $e.Body })
                    if ($e.Resolve) { [void] (Invoke-GitLab 'PUT' "/merge_requests/$iid/discussions/$($e.Discussion)?resolved=true") }
                }
            }
        }
        $posted++
        Say "Posted $(Label $e)"
    }
}
finally {
    # Invoke-GitLab stops the script on a failure; this still says how far it got.
    if ($posted -lt $toPost.Count) {
        [Console]::Error.WriteLine("Posted $posted of $($toPost.Count). Uploading again posts only the rest.")
    }
    $GitLab.Http.Dispose()
}

Say ''
if ($review.Mode -eq 'draft') {
    Say "$posted draft(s) added$(if ($skipped) { ", $skipped already there" }). Open $($mr.web_url) and press Submit review to publish them."
}
else {
    Say "$posted comment(s) posted$(if ($skipped) { ", $skipped already there" }): $($mr.web_url)"
}
exit 0
