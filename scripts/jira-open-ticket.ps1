<#
.SYNOPSIS
  Opens the Jira ticket the checked-out branch is named after, in the browser.

.DESCRIPTION
  A Dance Commander tool (tools.json). The ticket key is the first one in the branch name:
  feature/ABC-123-fix-login and ABC-123_fix both give ABC-123.

  Without -Projects, a key has to be written the way Jira writes it, in capitals: otherwise
  fix-2-crashes would be ticket FIX-2. With -Projects, only those project keys count, in either
  case, so abc-123-fix is ABC-123. Give them where branch names carry acronyms: without them,
  support-UTF-8-names is ticket UTF-8.

  Jira's address: -JiraUrl, else DC_JIRA_URL. The script only opens a page, so it needs no token.

.EXAMPLE
  In tools.json:

    {
      "id": "jira-open-ticket",
      "title": "Open Jira ticket",
      "group": "Jira",
      "exec": "pwsh",
      "args": ["-NoProfile", "-File", "C:/src/dance_commander/scripts/jira-open-ticket.ps1",
               "${branch}", "https://jira.example.com", "ABC,DEF"],
      "inRepository": true,
      "launch": "job"
    }

.EXAMPLE
  From a prompt:

    pwsh -File jira-open-ticket.ps1 (git branch --show-current) https://jira.example.com
#>
param(
    # ${branch}: the name to find the ticket key in.
    [Parameter(Position = 0)] [string] $Branch = '',

    # Jira's address, such as https://example.atlassian.net. DC_JIRA_URL when not given.
    [Parameter(Position = 1)] [string] $JiraUrl = '',

    # Optional: the project keys to look for, comma-separated ("ABC,DEF").
    [Parameter(Position = 2)] [string] $Projects = ''
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

function Fail([string] $message, [int] $code = 1) {
    [Console]::Error.WriteLine($message)
    exit $code
}

if (-not $Branch) { Fail "HEAD is detached: no branch to read a ticket key from." }

if (-not $JiraUrl) { $JiraUrl = "$env:DC_JIRA_URL" }
if (-not $JiraUrl) { Fail "No Jira address. Give it as the second argument or set DC_JIRA_URL." }
$base = $null
if (-not [Uri]::TryCreate($JiraUrl.Trim(), [UriKind]::Absolute, [ref] $base) -or
    ($base.Scheme -ne 'https' -and $base.Scheme -ne 'http')) {
    Fail "'$JiraUrl' is not a web address."
}

# Not preceded by a letter or digit, so v2ABC-1 is not ABC-1; not followed by a digit, so the whole
# number is taken.
$keys = @($Projects -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($k in $keys) {
    if ($k -cnotmatch '^[A-Za-z][A-Za-z0-9_]*$') { Fail "'$k' is not a Jira project key." }
}
if ($keys.Count -gt 0) {
    # Either case spelt out letter by letter rather than IgnoreCase, which would also let a Kelvin
    # sign stand for K and open a ticket that isn't there.
    $alternatives = foreach ($k in $keys) {
        -join ($k.ToCharArray() | ForEach-Object {
            if ([char]::IsLetter($_)) { "[$([char]::ToLowerInvariant($_))$([char]::ToUpperInvariant($_))]" } else { "$_" }
        })
    }
    $pattern = '(?<![\p{L}\p{Nd}])(?<key>(?:' + ($alternatives -join '|') + ')-[0-9]+)(?![0-9])'
}
else {
    $pattern = '(?<![\p{L}\p{Nd}])(?<key>[A-Z][A-Z0-9_]+-[0-9]+)(?![0-9])'
}
$match = [regex]::Match($Branch, $pattern)
if (-not $match.Success) {
    $which = if ($keys.Count -gt 0) { " for $($keys -join ', ')" } else { '' }
    Fail "$Branch has no Jira ticket key$which in its name."
}

$key = $match.Groups['key'].Value.ToUpperInvariant()
$url = "$($base.GetLeftPart([UriPartial]::Path).TrimEnd('/'))/browse/$key"
Start-Process $url
Write-Output "$key opened in the browser"
