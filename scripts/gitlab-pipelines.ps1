<#
.SYNOPSIS
  A GitLab CI location for Dance Commander: pipelines -> jobs -> artifacts.zip and job.log.

.DESCRIPTION
  A script location (ARCHITECTURE.md 6.16). Dance Commander knows nothing about GitLab; it asks this
  script two things and does the rest -- browsing, preview, copying, and opening artifacts.zip as an
  archive:

    list <crumbs>   print the rows one level down, one tab-separated line each:
                    kind (d|f), name, size, modified (ISO 8601), crumb
    read <crumbs>   write a file's bytes to stdout

  The levels:

    (root)                       the branch's open merge requests, its pipelines, newest first, and
                                 an "(all branches)" folder
    mr-<iid>                     the merge request's pipelines
    <pipeline>                   that pipeline's jobs
    <pipeline>/<job>             artifacts.zip (when the job kept any) and job.log
    all/..., mr-<iid>/...        the same below those

  Merge request pipelines run on refs/merge-requests/<iid>/head, not on the branch, which is why the
  merge request has a folder of its own: filtering by branch alone misses exactly those.

  Review comments are not here: gitlab-review-download.ps1 is a tool that writes them to a file.

  Which project: from the git remote the location's repository tracks (${remoteUrl}), so every
  checkout of a repository gets its own project without configuration. HTTPS and SSH remotes both
  work. GitLab's API is assumed at https://<host>/api/v4; set DC_GITLAB_API to override, e.g. for a
  GitLab behind a different host name or a non-standard port.

  The token arrives in DC_SECRET_GITLAB_TOKEN, never on the command line: declare "gitlab-token" in
  the listing's "secrets" and Dance Commander asks for it once. A personal access token with the
  read_api scope is enough. Nothing here prints it -- stderr is shown in the app and its log verbatim.

.EXAMPLE
  In tools.json:

    "listings": [
      {
        "id": "gitlab",
        "title": "Pipelines",
        "exec": "pwsh",
        "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/gitlab-pipelines.ps1",
                 "${remoteUrl}", "${branch}"],
        "script": true,
        "secrets": ["gitlab-token"],
        "inRepository": true,
        "sort": "modified"
      }
    ]

  Then "Pipelines" is on the context menu of any folder inside a repository, and bookmarkable as
  from:C:/src/app?gitlab.

.EXAMPLE
  From a prompt, to see what it would do:

    $env:DC_SECRET_GITLAB_TOKEN = '...'
    pwsh -File gitlab-pipelines.ps1 git@gitlab.example.com:group/app.git main describe ""
    pwsh -File gitlab-pipelines.ps1 git@gitlab.example.com:group/app.git main list ""
    pwsh -File gitlab-pipelines.ps1 git@gitlab.example.com:group/app.git main list "4711"
#>
param(
    # ${remoteUrl}: the remote the branch tracks, else origin, exactly as written in .git/config.
    [Parameter(Position = 0)] [string] $RemoteUrl = '',

    # ${branch}: empty when HEAD is detached; the root then shows every branch's pipelines.
    [Parameter(Position = 1)] [string] $Branch = '',

    # Added by Dance Commander: list, read -- or describe, for checking the setup by hand.
    [Parameter(Position = 2)] [string] $Verb = '',

    # Added by Dance Commander: the crumbs of the level asked about, joined with '/'; empty at the root.
    [Parameter(Position = 3)] [string] $Crumbs = ''
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# How far back the lists go. A pipeline list is a "what happened lately", not an archive.
$PipelineCount = 30
$JobPageSize = 100

function Fail([string] $message, [int] $code = 1) {
    [Console]::Error.WriteLine($message)
    exit $code
}

# ---- which GitLab, which project -----------------------------------------------------------------

<#
  git@host:group/sub/app.git       -> host, group/sub/app
  ssh://git@host:2222/group/app.git -> host, group/app
  https://host/group/app.git        -> host, group/app  (https://user@host/... too)
#>
function Resolve-Project([string] $remote) {
    $remote = $remote.Trim()
    if ($remote -match '^(?:ssh|https?|git)://(?:[^@/]+@)?(?<host>[^/:]+)(?::\d+)?/(?<path>.+?)(?:\.git)?/?$') {
        return @{ Host = $Matches.host; Path = $Matches.path }
    }
    if ($remote -match '^(?:[^@]+@)?(?<host>[^:/]+):(?<path>[^/].*?)(?:\.git)?/?$') {
        return @{ Host = $Matches.host; Path = $Matches.path }
    }
    return $null
}

if ($Verb -notin @('list', 'read', 'describe')) {
    Fail "gitlab-pipelines.ps1: unknown verb '$Verb' (expected list, read or describe)." 2
}

$project = Resolve-Project $RemoteUrl
if ($null -eq $project) {
    if ($RemoteUrl -eq '') {
        Fail "This repository has no remote to find a GitLab project from."
    }
    Fail "Cannot tell which GitLab project '$RemoteUrl' is."
}

$api = if ($env:DC_GITLAB_API) { $env:DC_GITLAB_API.TrimEnd('/') } else { "https://$($project.Host)/api/v4" }
$projectId = [Uri]::EscapeDataString($project.Path)

if ($Verb -eq 'describe') {
    "API:     $api"
    "Project: $($project.Path)"
    "Branch:  $(if ($Branch) { $Branch } else { '(detached: all branches)' })"
    "Token:   $(if ($env:DC_SECRET_GITLAB_TOKEN) { 'present' } else { 'missing' })"
    exit 0
}

$token = $env:DC_SECRET_GITLAB_TOKEN
if (-not $token) {
    Fail "No GitLab token: declare `"secrets`": [`"gitlab-token`"] for this listing."
}

# ---- talking to the API --------------------------------------------------------------------------

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromMinutes(30)
$http.DefaultRequestHeaders.Add('PRIVATE-TOKEN', $token)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('DanceCommander-gitlab-pipelines/1')

# The response, headers read, or a failure in words -- GitLab's status codes mean specific things here.
function Send([string] $path) {
    $uri = "$api/projects/$projectId$path"
    try {
        $response = $http.GetAsync($uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    }
    catch {
        Fail "Could not reach $api ($($_.Exception.GetBaseException().Message))."
    }

    if ($response.IsSuccessStatusCode) { return $response }

    $status = [int] $response.StatusCode
    switch ($status) {
        401 { Fail "GitLab did not accept the token (401). It may have expired; remove it from Credential Manager (script:gitlab-token) to be asked again." }
        403 { Fail "The token may not read $($project.Path) (403). It needs the read_api scope." }
        404 { Fail "GitLab has no $($project.Path)$($path -replace '\?.*$', '') (404) -- or the token cannot see it." }
        default { Fail "GitLab answered $status $($response.ReasonPhrase) for $path." }
    }
}

function Get-Json([string] $path) {
    $response = Send $path
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    # Unwrapped here and re-collected with @() by callers: ConvertFrom-Json hands an array over as
    # one object in Windows PowerShell 5.1 and element by element in 7.
    $data = $text | ConvertFrom-Json
    return $data
}

# ---- rows ----------------------------------------------------------------------------------------

# Rows go out as UTF-8 regardless of the console's code page, so a branch called "fix/äö" survives.
$out = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), [System.Text.UTF8Encoding]::new($false))
$out.NewLine = "`n"

function Clean([string] $text) {
    # Tabs and line breaks would split a row; nothing else needs escaping.
    if ($null -eq $text) { return '' }
    return ($text -replace '[\t\r\n]+', ' ').Trim()
}

function Row([string] $kind, [string] $name, $size, $modified, [string] $crumb) {
    $sizeText = if ($null -ne $size -and "$size" -ne '') { "$size" } else { '' }
    $when = if ($modified) { ([DateTimeOffset] $modified).ToString('o') } else { '' }
    $out.WriteLine("$kind`t$(Clean $name)`t$sizeText`t$when`t$crumb")
}

# A merge request pipeline runs on refs/merge-requests/<iid>/head, which names the merge request
# and not the branch -- and the branch is what you are looking for in a list of everyone's
# pipelines. The branches of all the merge requests in a list come from one request.
function MergeRequestBranches($pipelines) {
    $iids = @($pipelines | ForEach-Object {
        if ($_.ref -match '^refs/merge-requests/(\d+)/(?:head|merge)$') { $Matches[1] }
    } | Select-Object -Unique)
    $branches = @{}
    if ($iids.Count -eq 0) { return $branches }

    $query = '?state=all&per_page=100&' + (($iids | ForEach-Object { "iids[]=$_" }) -join '&')
    foreach ($mr in @(Get-Json "/merge_requests$query" | Where-Object { $null -ne $_ })) {
        $branches["$($mr.iid)"] = $mr.source_branch
    }
    return $branches
}

function Ref([string] $ref, $branches) {
    if ($ref -match '^refs/merge-requests/(\d+)/(?:head|merge)$') {
        $iid = $Matches[1]
        if ($branches.ContainsKey($iid)) { return $branches[$iid] }
        return "!$iid"
    }
    return $ref
}

function PipelineRows($pipelines, [bool] $showRef) {
    # An empty answer passed as an argument arrives as a single $null, not as no items.
    $pipelines = @($pipelines | Where-Object { $null -ne $_ })
    $branches = if ($showRef) { MergeRequestBranches $pipelines } else { @{} }
    foreach ($p in $pipelines) {
        $sha = if ($p.sha) { $p.sha.Substring(0, [Math]::Min(8, $p.sha.Length)) } else { '' }
        $name = "#$($p.id) $($p.status)"
        if ($showRef) { $name += " - $(Ref $p.ref $branches)" }
        if ($sha) { $name += " - $sha" }
        $when = if ($p.PSObject.Properties['updated_at'] -and $p.updated_at) { $p.updated_at } else { $p.created_at }
        Row 'd' $name $null $when "$($p.id)"
    }
}

function Pipelines([bool] $allBranches) {
    $query = "?per_page=$PipelineCount&order_by=id&sort=desc"
    if (-not $allBranches -and $Branch) { $query += "&ref=$([Uri]::EscapeDataString($Branch))" }
    PipelineRows (Get-Json "/pipelines$query") ($allBranches -or -not $Branch)
}

# The open merge requests whose source is this branch -- normally one.
function MergeRequests() {
    if (-not $Branch) { return }
    $query = "?state=opened&source_branch=$([Uri]::EscapeDataString($Branch))&per_page=20"
    foreach ($mr in @(Get-Json "/merge_requests$query")) {
        Row 'd' "!$($mr.iid) $($mr.title)" $null $mr.updated_at "mr-$($mr.iid)"
    }
}

function MergeRequestLevel([string] $iid) {
    PipelineRows (Get-Json "/merge_requests/$iid/pipelines?per_page=$PipelineCount") $false
}

function Jobs([string] $pipelineId) {
    foreach ($j in @(Get-Json "/pipelines/$pipelineId/jobs?per_page=$JobPageSize")) {
        Row 'd' "$($j.stage) - $($j.name) - $($j.status)" $null ($(if ($j.finished_at) { $j.finished_at } else { $j.created_at })) "$($j.id)"
    }
}

function JobFiles([string] $jobId) {
    $job = Get-Json "/jobs/$jobId"

    # An expired archive is still named on the job; listing it would offer a file that cannot be read.
    $expired = $job.PSObject.Properties['artifacts_expire_at'] -and $job.artifacts_expire_at -and
        ([DateTimeOffset] $job.artifacts_expire_at) -lt [DateTimeOffset]::UtcNow
    if ($job.PSObject.Properties['artifacts_file'] -and $job.artifacts_file -and -not $expired) {
        Row 'f' 'artifacts.zip' $job.artifacts_file.size $job.finished_at 'artifacts.zip'
    }

    # The log's size is not known without downloading it; the column stays blank.
    Row 'f' 'job.log' $null ($(if ($job.finished_at) { $job.finished_at } else { $job.created_at })) 'job.log'
}

# ---- reads ---------------------------------------------------------------------------------------

function Stream([string] $path) {
    $response = Send $path
    $stdout = [Console]::OpenStandardOutput()
    $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult().CopyTo($stdout)
    $stdout.Flush()
}

# ---- the levels ----------------------------------------------------------------------------------

$parts = @($Crumbs.Split('/', [StringSplitOptions]::RemoveEmptyEntries))

foreach ($part in $parts) {
    # Ids, "all", "mr-5107", "artifacts.zip", "job.log" -- never "." or "..", which only a
    # hand-typed address has.
    if ($part -notmatch '^[A-Za-z0-9_-][A-Za-z0-9._-]*$') { Fail "Not a crumb this script printed: '$part'." 2 }
}

# Where the pipelines come from: the branch (no scope crumb), every ref ("all"), or one merge
# request ("mr-5107"). Below that the levels are the same: pipeline, job, file.
$scope = 'branch'
$mergeRequest = $null
if ($parts.Count -gt 0 -and $parts[0] -eq 'all') { $scope = 'all' }
elseif ($parts.Count -gt 0 -and $parts[0] -match '^mr-(\d+)$') { $scope = 'mr'; $mergeRequest = $Matches[1] }
# @(...) around the whole if: an if expression unrolls a one-element array into a bare string.
$rest = @(if ($scope -eq 'branch') { $parts } else { $parts | Select-Object -Skip 1 })

try {
    if ($Verb -eq 'list') {
        switch ($rest.Count) {
            0 {
                switch ($scope) {
                    'branch' {
                        MergeRequests
                        Pipelines $false
                        if ($Branch) { Row 'd' '(all branches)' $null $null 'all' }
                    }
                    'all' { Pipelines $true }
                    'mr' { MergeRequestLevel $mergeRequest }
                }
            }
            1 { Jobs $rest[0] }
            2 { JobFiles $rest[1] }
            default { Fail "Nothing below $Crumbs." 3 }
        }
        exit 0
    }

    # read
    if ($rest.Count -ne 3) { Fail "Not a file: $Crumbs." 3 }
    switch ($rest[2]) {
        'artifacts.zip' { $out.Flush(); Stream "/jobs/$($rest[1])/artifacts" }
        'job.log'       { $out.Flush(); Stream "/jobs/$($rest[1])/trace" }
        default         { Fail "No file called $($rest[2])." 3 }
    }
    exit 0
}
finally {
    $out.Flush()
    $http.Dispose()
}
