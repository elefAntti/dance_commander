<#
.SYNOPSIS
  What the GitLab review scripts share: which project, the token, and talking to the API.

.DESCRIPTION
  Dot-sourced by gitlab-review-download.ps1 and gitlab-review-upload.ps1; not run on its own.

  The token: DC_SECRET_GITLAB_TOKEN or GITLAB_TOKEN when set, else the one Dance Commander stored
  for the Pipelines listing (Credential Manager, "DanceCommander:script:gitlab-token"). Tools in
  tools.json get no "secrets" of their own, and the token must never be on a command line, so the
  scripts read the stored one themselves. Nothing here prints it. Uploading prefers a token of its
  own (gitlab-review-upload.ps1 says why).
#>

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function Fail([string] $message, [int] $code = 1) {
    [Console]::Error.WriteLine($message)
    exit $code
}

function Has([object] $o, [string] $name) {
    return $null -ne $o -and $o.PSObject.Properties[$name] -and $null -ne $o.$name
}

# ---- which GitLab, which project -----------------------------------------------------------------

<#
  git@host:group/sub/app.git        -> host, group/sub/app
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

<#
  A merge request as the browser shows it:
  https://gitlab.example.com/group/app/-/merge_requests/5107  -> host, group/app, 5107
#>
function Resolve-MergeRequestUrl([string] $url) {
    if ($url.Trim() -match '^https?://(?<host>[^/:]+)(?::\d+)?/(?<path>.+?)/-/merge_requests/(?<iid>\d+)') {
        return @{ Host = $Matches.host; Path = $Matches.path; Iid = $Matches.iid }
    }
    return $null
}

# The branch checked out in $dir and the remote it tracks (else origin), the way ${remoteUrl} and
# ${branch} are worked out for tools. Empty strings when $dir is not in a repository.
function Read-GitContext([string] $dir) {
    $branch = (git -C $dir symbolic-ref --quiet --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $branch = '' }
    $remoteName = ''
    if ($branch) { $remoteName = (git -C $dir config "branch.$branch.remote" 2>$null) }
    if (-not $remoteName) { $remoteName = 'origin' }
    $remoteUrl = (git -C $dir remote get-url $remoteName 2>$null)
    if ($LASTEXITCODE -ne 0) { $remoteUrl = '' }
    return @{ Branch = "$branch".Trim(); RemoteUrl = "$remoteUrl".Trim() }
}

# ---- the token -----------------------------------------------------------------------------------

function Read-StoredCredential([string] $target) {
    if (-not ('DcGitLabCredential' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DcGitLabCredential
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment;
        public long LastWritten; public uint CredentialBlobSize; public IntPtr CredentialBlob;
        public uint Persist; public uint AttributeCount; public IntPtr Attributes;
        public IntPtr TargetAlias; public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    public static string Read(string target)
    {
        IntPtr handle;
        if (!CredRead(target, 1 /* generic */, 0, out handle)) return null;
        try
        {
            var credential = (CREDENTIAL)Marshal.PtrToStructure(handle, typeof(CREDENTIAL));
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            // Dance Commander writes the secret as UTF-16.
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(handle); }
    }
}
'@
    }
    return [DcGitLabCredential]::Read($target)
}

function Get-GitLabToken {
    if ($env:DC_SECRET_GITLAB_TOKEN) { return $env:DC_SECRET_GITLAB_TOKEN }
    if ($env:GITLAB_TOKEN) { return $env:GITLAB_TOKEN }
    $stored = Read-StoredCredential 'DanceCommander:script:gitlab-token'
    if ($stored) { return $stored }
    Fail ("No GitLab token. Open the Pipelines listing once so Dance Commander asks for it and stores it, " +
        "or set GITLAB_TOKEN.")
}

# ---- talking to the API --------------------------------------------------------------------------

# Sets $script:GitLab: the API root, the project, and an HttpClient carrying the token.
# $token: the one to use; Get-GitLabToken's when not given.
function Connect-GitLab([string] $gitlabHost, [string] $projectPath, [string] $userAgent, [string] $token = '') {
    if (-not $token) { $token = Get-GitLabToken }
    $api = if ($env:DC_GITLAB_API) { $env:DC_GITLAB_API.TrimEnd('/') } else { "https://$gitlabHost/api/v4" }
    $http = [System.Net.Http.HttpClient]::new()
    $http.Timeout = [TimeSpan]::FromMinutes(5)
    $http.DefaultRequestHeaders.Add('PRIVATE-TOKEN', $token)
    $http.DefaultRequestHeaders.UserAgent.ParseAdd($userAgent)
    $script:GitLab = @{
        Api       = $api
        Path      = $projectPath
        ProjectId = [Uri]::EscapeDataString($projectPath)
        Http      = $http
    }
}

# GitLab's own words for a failure, when it gave any: {"message": ...} or {"error": ...}.
function Describe-Failure($response) {
    $text = ''
    try { $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } catch { }
    try {
        $json = $text | ConvertFrom-Json
        if (Has $json 'message') { return ($json.message | ConvertTo-Json -Compress -Depth 5) }
        if (Has $json 'error') { return "$($json.error)" }
    } catch { }
    if ($text.Length -gt 300) { $text = $text.Substring(0, 300) + '...' }
    return $text
}

<#
  One request; the parsed answer, or the script stops with the failure in words. $path is below
  /projects/<id>; one starting with "~/" is from the API root (~/user). $body, when given, goes as JSON.
  With -AllowFailure a failed request returns $null and the script carries on.
#>
function Invoke-GitLab([string] $method, [string] $path, $body = $null, [switch] $AllowFailure) {
    $uri = if ($path.StartsWith('~/')) { "$($GitLab.Api)/$($path.Substring(2))" } else { "$($GitLab.Api)/projects/$($GitLab.ProjectId)$path" }
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), $uri)
    if ($null -ne $body) {
        $json = $body | ConvertTo-Json -Depth 10 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    }
    try {
        $response = $GitLab.Http.SendAsync($request).GetAwaiter().GetResult()
    }
    catch {
        Fail "Could not reach $($GitLab.Api) ($($_.Exception.GetBaseException().Message))."
    }

    if ($response.IsSuccessStatusCode) {
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $text) { return $null }
        # Unwrapped here and re-collected with @() by callers: ConvertFrom-Json hands an array over
        # element by element.
        return ($text | ConvertFrom-Json)
    }
    if ($AllowFailure) { return $null }

    $status = [int] $response.StatusCode
    $shown = $path -replace '\?.*$', ''
    $why = Describe-Failure $response
    switch ($status) {
        401 { Fail "GitLab did not accept the token (401). It may have expired; remove it from Credential Manager (DanceCommander:script:gitlab-token) to be asked again." }
        403 {
            if ($method -eq 'GET') { Fail "The token may not read $($GitLab.Path) (403). It needs the read_api scope." }
            Fail "GitLab refused $method $shown (403): the token needs the api scope to write comments, not just read_api. $why"
        }
        404 { Fail "GitLab has no $($GitLab.Path)$shown (404) -- or the token cannot see it." }
        default { Fail "GitLab answered $status $($response.ReasonPhrase) for $method ${shown}: $why" }
    }
}

function Get-GitLab([string] $path) { return Invoke-GitLab 'GET' $path }

function Get-AllPages([string] $path) {
    $results = [System.Collections.Generic.List[object]]::new()
    $page = 1
    while ($true) {
        $sep = if ($path.Contains('?')) { '&' } else { '?' }
        $batch = @(Get-GitLab "$path$($sep)page=$page&per_page=100" | Where-Object { $null -ne $_ })
        foreach ($item in $batch) { $results.Add($item) }
        if ($batch.Count -lt 100) { break }
        $page++
    }
    return , $results.ToArray()
}

# The open merge requests whose source is $branch -- normally one.
function Find-OpenMergeRequests([string] $branch) {
    $query = "?state=opened&source_branch=$([Uri]::EscapeDataString($branch))&per_page=20"
    # Streamed, not returned as one array: callers collect with @().
    Get-GitLab "/merge_requests$query" | Where-Object { $null -ne $_ }
}

$script:SourceCache = @{}

# A file at a revision, as lines; $null when it cannot be had (deleted, binary, too large).
function Get-SourceLines([string] $path, [string] $sha) {
    $key = "$sha`n$path"
    if ($script:SourceCache.ContainsKey($key)) { return , $script:SourceCache[$key] }

    $lines = $null
    $file = Invoke-GitLab 'GET' "/repository/files/$([Uri]::EscapeDataString($path))?ref=$sha" -AllowFailure
    if ($null -ne $file -and (Has $file 'content')) {
        $text = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($file.content))
        $lines = $text -split "`r?`n"
        # A final newline is the end of the last line, not an empty line after it.
        # (0..-1 would count down, not give nothing, hence the Count -eq 1 case.)
        if ($lines.Count -eq 1 -and $lines[0] -eq '') { $lines = @() }
        elseif ($lines.Count -gt 1 -and $lines[-1] -eq '') { $lines = $lines[0..($lines.Count - 2)] }
    }
    $script:SourceCache[$key] = $lines
    return , $lines
}

# Text out as UTF-8 whatever the console's code page, so names like "fix/äö" survive.
function New-Utf8Stdout {
    $out = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), [System.Text.UTF8Encoding]::new($false))
    $out.NewLine = "`n"
    $out.AutoFlush = $true
    return $out
}
