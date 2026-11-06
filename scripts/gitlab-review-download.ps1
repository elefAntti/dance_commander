<#
.SYNOPSIS
  Downloads a merge request's open review threads into a Markdown file, with the code around each.

.DESCRIPTION
  A Dance Commander tool (tools.json). For the branch checked out, finds its open merge request and
  writes mr-<iid>-comments.md into the folder the pane shows: every open thread, the file and line it
  is on, the source around that line at the revision it was written against, and the conversation.
  Each thread names its discussion id, which a review file uses to reply to it
  (gitlab-review-upload.ps1; the format is in gitlab-review.md).

  A thread is open while any human, resolvable note in it is unresolved: "resolved" on the
  discussion alone can be false because a system note arrived after the humans had resolved it.
  Comments on the merge request that are not threads ("Looks good") cannot be resolved, so all of
  them are written, last, under "Comments".

  Nothing is overwritten: when the name is taken, the new file is "mr-<iid>-comments (2).md" and so on.

  The token: see gitlab-common.ps1. read_api is enough.

.EXAMPLE
  In tools.json:

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
    }

.EXAMPLE
  From a prompt, for any merge request:

    pwsh -File gitlab-review-download.ps1 "" "" . https://gitlab.example.com/group/app/-/merge_requests/5107
#>
param(
    # ${remoteUrl}: the remote the branch tracks, else origin.
    [Parameter(Position = 0)] [string] $RemoteUrl = '',

    # ${branch}: whose open merge request to download.
    [Parameter(Position = 1)] [string] $Branch = '',

    # ${dir}: where the file goes.
    [Parameter(Position = 2)] [string] $OutputDirectory = '.',

    # Optional: a merge request URL or number instead of the branch's.
    [Parameter(Position = 3)] [string] $MergeRequest = ''
)

. (Join-Path $PSScriptRoot 'gitlab-common.ps1')

# Lines of source shown above and below the line a thread is on.
$ContextLines = 4

# ---- which merge request -------------------------------------------------------------------------

$fromUrl = if ($MergeRequest) { Resolve-MergeRequestUrl $MergeRequest } else { $null }
if ($fromUrl) {
    $project = @{ Host = $fromUrl.Host; Path = $fromUrl.Path }
}
else {
    if (-not $RemoteUrl) { Fail "No git remote to find the GitLab project from; give a merge request URL." }
    $project = Resolve-Project $RemoteUrl
    if ($null -eq $project) { Fail "Cannot tell which GitLab project '$RemoteUrl' is." }
}
Connect-GitLab $project.Host $project.Path 'DanceCommander-gitlab-review-download/1'

$iids = @()
if ($fromUrl) { $iids = @($fromUrl.Iid) }
elseif ($MergeRequest -match '^!?(\d+)$') { $iids = @($Matches[1]) }
elseif ($MergeRequest) { Fail "'$MergeRequest' is neither a merge request URL nor a number." }
else {
    if (-not $Branch) { Fail "HEAD is detached: no branch to find a merge request for." }
    $iids = @(Find-OpenMergeRequests $Branch | ForEach-Object { "$($_.iid)" })
    if ($iids.Count -eq 0) { Fail "$Branch has no open merge request in $($project.Path)." }
}

# ---- one merge request as Markdown ---------------------------------------------------------------

$FenceLanguages = @{
    py = 'python'; cpp = 'cpp'; cc = 'cpp'; cxx = 'cpp'; c = 'c'; h = 'cpp'; hpp = 'cpp'; cs = 'csharp'
    ts = 'typescript'; tsx = 'typescript'; js = 'javascript'; jsx = 'javascript'; rs = 'rust'; go = 'go'
    java = 'java'; yaml = 'yaml'; yml = 'yaml'; json = 'json'; xml = 'xml'; sh = 'bash'; bash = 'bash'
    cmake = 'cmake'; md = 'markdown'; ps1 = 'powershell'; xaml = 'xml'
}

function Fence([string] $path) {
    $ext = if ($path -match '\.([^./\\]+)$') { $Matches[1].ToLowerInvariant() } else { '' }
    if ($FenceLanguages.ContainsKey($ext)) { return $FenceLanguages[$ext] }
    return $ext
}

# A fence longer than any run of backticks in the text, so a body with ``` in it cannot close it.
function FenceFor([string[]] $lines) {
    $longest = 2
    foreach ($l in $lines) {
        foreach ($m in [regex]::Matches($l, '`+')) { $longest = [Math]::Max($longest, $m.Length) }
    }
    return '`' * ($longest + 1)
}

function Is-Open($discussion) {
    $notes = @($discussion.notes)
    $human = @($notes | Where-Object { -not ((Has $_ 'system') -and $_.system) })
    if ($human.Count -eq 0) { return $false }
    $resolvable = @($human | Where-Object { (Has $_ 'resolvable') -and $_.resolvable })
    if ($resolvable.Count -eq 0) { return $false }
    return @($resolvable | Where-Object { -not ((Has $_ 'resolved') -and $_.resolved) }).Count -gt 0
}

function Write-MergeRequest([string] $iid) {
    $mr = Get-GitLab "/merge_requests/$iid"
    $currentHead = if ((Has $mr 'diff_refs') -and (Has $mr.diff_refs 'head_sha')) { $mr.diff_refs.head_sha } else { '' }

    $onCode = [System.Collections.Generic.List[object]]::new()
    $general = [System.Collections.Generic.List[object]]::new()
    $comments = [System.Collections.Generic.List[object]]::new()
    foreach ($d in (Get-AllPages "/merge_requests/$iid/discussions")) {
        # A comment on the merge request that nobody has replied to is an individual note. It has no
        # resolved state, so there is no telling whether it was dealt with: all of them are kept.
        # System notes ("added 1 commit") are individual notes too, and are left out.
        if ((Has $d 'individual_note') -and $d.individual_note) {
            if (@($d.notes | Where-Object { -not ((Has $_ 'system') -and $_.system) }).Count -gt 0) { $comments.Add($d) }
            continue
        }
        if (-not (Is-Open $d)) { continue }
        $first = @($d.notes)[0]
        if ((Has $first 'position') -and (Has $first.position 'position_type') -and $first.position.position_type -eq 'text') { $onCode.Add($d) }
        else { $general.Add($d) }
    }

    $md = [System.Text.StringBuilder]::new()
    function Line([string] $text = '') { [void] $md.Append($text).Append("`n") }

    # Images and attachments are linked relative to the project ("/uploads/..."), which only works
    # inside GitLab.
    $projectUrl = $mr.web_url -replace '/-/merge_requests/\d+$', ''
    $hostUrl = $projectUrl -replace '^(https?://[^/]+).*$', '$1'

    function Conversation($discussion) {
        foreach ($note in @($discussion.notes | Where-Object { -not ((Has $_ 'system') -and $_.system) })) {
            $date = ([DateTimeOffset] $note.created_at).ToLocalTime().ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
            $tag = if ((Has $note 'resolved') -and $note.resolved) { ' (resolved)' } else { '' }
            Line "**@$($note.author.username)** · $date$tag"
            Line ''
            $body = $note.body -replace '\]\(/uploads/', "]($projectUrl/uploads/" -replace '\]\(/-/', "]($hostUrl/-/"
            foreach ($bodyLine in ($body -split "`r?`n")) { Line ("> $bodyLine").TrimEnd() }
            Line ''
        }
        $firstNote = @($discussion.notes)[0]
        Line "Reply in a review file under ``## reply $($discussion.id)`` · [open in GitLab]($($mr.web_url)#note_$($firstNote.id))"
        Line ''
        Line '---'
        Line ''
    }

    $author = if (Has $mr 'author') { "@$($mr.author.username)" } else { 'unknown' }
    Line "# Review comments: !$iid $($mr.title)"
    Line ''
    Line "- **Merge request:** $($mr.web_url)"
    Line "- **Branch:** ``$($mr.source_branch)`` -> ``$($mr.target_branch)``"
    Line "- **Author:** $author"
    if ($currentHead) { Line "- **Head:** ``$($currentHead.Substring(0, 8))``" }
    Line "- **Downloaded:** $([DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture))"
    Line "- **Open threads:** $($onCode.Count + $general.Count) ($($onCode.Count) on code, $($general.Count) general)"
    Line "- **Comments:** $($comments.Count) (not threads, so never resolved)"
    Line ''
    Line '---'
    Line ''

    $n = 0
    foreach ($d in $onCode) {
        $n++
        $p = @($d.notes)[0].position
        # A thread on a removed line has only old_line; its code is in the old file at base_sha.
        $old = -not (Has $p 'new_line')
        $path = if ($old) { $p.old_path } else { $p.new_path }
        $line = if ($old) { [int] $p.old_line } else { [int] $p.new_line }
        $sha = if ($old) { $p.base_sha } else { $p.head_sha }
        $where = "${path}:$line$(if ($old) { ' (old)' })"

        Line "## $n. ``$where``"
        Line ''
        if ($old) {
            Line "On a line the merge request removes; the code is from ``$($sha.Substring(0, 8))``, before the change."
            Line ''
        }
        elseif ($currentHead -and $p.head_sha -ne $currentHead) {
            Line "Written against ``$($p.head_sha.Substring(0, 8))``, an earlier version: the code has changed since, and the line may have moved."
            Line ''
        }

        $source = Get-SourceLines $path $sha
        if ($null -ne $source -and $line -ge 1 -and $line -le $source.Count) {
            $first = [Math]::Max(1, $line - $ContextLines)
            $last = [Math]::Min($source.Count, $line + $ContextLines)
            $width = "$last".Length
            $shown = @(for ($i = $first; $i -le $last; $i++) {
                $marker = if ($i -eq $line) { '>' } else { ' ' }
                "$marker $("$i".PadLeft($width)) | $($source[$i - 1])"
            })
            $fence = FenceFor $shown
            Line ($fence + (Fence $path))
            foreach ($s in $shown) { Line $s }
            Line $fence
            Line ''
        }
        elseif ($null -eq $source) {
            Line "(The source of $path at $($sha.Substring(0, 8)) could not be fetched.)"
            Line ''
        }
        Conversation $d
    }

    if ($general.Count -gt 0) {
        Line '# General threads'
        Line ''
        foreach ($d in $general) {
            $n++
            Line "## $n. On the merge request"
            Line ''
            Conversation $d
        }
    }

    if ($comments.Count -gt 0) {
        Line '# Comments'
        Line ''
        foreach ($d in $comments) {
            $n++
            Line "## $n. Comment on the merge request"
            Line ''
            Conversation $d
        }
    }

    return $md.ToString()
}

# ---- out -----------------------------------------------------------------------------------------

function Free-Name([string] $dir, [string] $stem) {
    $candidate = Join-Path $dir "$stem.md"
    $k = 2
    while (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $dir "$stem ($k).md"
        $k++
    }
    return $candidate
}

$out = New-Utf8Stdout
$dir = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
try {
    foreach ($iid in $iids) {
        $text = Write-MergeRequest $iid
        $target = Free-Name $dir "mr-$iid-comments"
        # CreateNew: if something took the name since Free-Name looked, fail rather than overwrite.
        $stream = [System.IO.FileStream]::new($target, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
        try {
            $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($text)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }
        $out.WriteLine("Wrote $target")
    }
}
finally {
    $GitLab.Http.Dispose()
}
exit 0
