<#
.SYNOPSIS
  Opens the checked-out branch's merge request in the browser.

.DESCRIPTION
  A Dance Commander tool (tools.json). Finds the branch's open merge request in the project the
  remote points at and opens its page; several open ones are all opened. With none open, the most
  recently updated one from this branch is opened instead (merged or closed), and the output says
  which state it is in. With none at all, it says so.

  The token: see gitlab-common.ps1. read_api is enough.

.EXAMPLE
  In tools.json:

    {
      "id": "gitlab-open-merge-request",
      "title": "Open merge request",
      "group": "GitLab",
      "exec": "pwsh",
      "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/gitlab-open-merge-request.ps1",
               "${remoteUrl}", "${branch}"],
      "inRepository": true,
      "launch": "job"
    }
#>
param(
    # ${remoteUrl}: the remote the branch tracks, else origin.
    [Parameter(Position = 0)] [string] $RemoteUrl = '',

    # ${branch}: whose merge request to open.
    [Parameter(Position = 1)] [string] $Branch = ''
)

. (Join-Path $PSScriptRoot 'gitlab-common.ps1')

# Several open merge requests from one branch is unusual; more than this is a mistake somewhere, and
# a wall of browser tabs would not help find it.
$MaxOpened = 5

if (-not $Branch) { Fail "HEAD is detached: no branch to find a merge request for." }
if (-not $RemoteUrl) { Fail "No git remote to find the GitLab project from." }
$project = Resolve-Project $RemoteUrl
# The remote is quoted without any user:password@ it carries.
if ($null -eq $project) { Fail "Cannot tell which GitLab project '$($RemoteUrl -replace '//[^/@]*@', '//')' is." }
Connect-GitLab $project.Host $project.Path 'DanceCommander-gitlab-open-merge-request/1'

# web_url comes from the server and goes to ShellExecute, which would run a path as readily as it
# opens a page: only a web address is opened.
function Open-MergeRequest($mr) {
    $url = $null
    if (-not (Has $mr 'web_url') -or
        -not [Uri]::TryCreate("$($mr.web_url)", [UriKind]::Absolute, [ref] $url) -or
        ($url.Scheme -ne 'https' -and $url.Scheme -ne 'http')) {
        Fail "GitLab gave no web address for !$($mr.iid)."
    }
    Start-Process $url.AbsoluteUri
}

# source_branch matches by name only, so a fork's branch of the same name would count too.
function Is-FromThisProject($mr) {
    return (Has $mr 'source_project_id') -and (Has $mr 'target_project_id') -and
        $mr.source_project_id -eq $mr.target_project_id
}

$open = @(Find-OpenMergeRequests $Branch | Where-Object { Is-FromThisProject $_ })
if ($open.Count -gt 0) {
    $shown = @($open | Sort-Object { [int] $_.iid } | Select-Object -First $MaxOpened)
    foreach ($mr in $shown) { Open-MergeRequest $mr }
    $names = ($shown | ForEach-Object { "!$($_.iid)" }) -join ', '
    $more = if ($open.Count -gt $shown.Count) { " ($($open.Count - $shown.Count) more not opened)" } else { '' }
    Write-Output "$names opened in the browser$more"
    exit 0
}

$query = "?source_branch=$([Uri]::EscapeDataString($Branch))&order_by=updated_at&sort=desc&per_page=20"
$latest = @(Get-GitLab "/merge_requests$query" | Where-Object { $null -ne $_ -and (Is-FromThisProject $_) })
if ($latest.Count -eq 0) { Fail "$Branch has no merge request in $($project.Path)." }

$mr = $latest[0]
Open-MergeRequest $mr
Write-Output "$Branch has no open merge request. !$($mr.iid), $($mr.state), opened in the browser"
