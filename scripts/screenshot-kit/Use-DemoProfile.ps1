<#
.SYNOPSIS
  Swaps %APPDATA%\DanceCommander for a neutral demo profile while screenshots are taken, and back.

.DESCRIPTION
  Everything the app remembers (tools, bookmarks, history, tabs, command lines, known hosts) lives
  in that one folder, and the real one names employers, hosts and home folders. So for screenshots
  the folder is renamed aside, whole, and a demo one takes its place; -Restore puts it back. Nothing
  is deleted: the demo profile is renamed to DanceCommander.demo-<time> on the way out.

  Only with Dance Commander closed: it writes history and view state as it exits, which would land
  in the wrong profile.

.EXAMPLE
  .\Use-DemoProfile.ps1               # swap in, highlight rules off as in a fresh install
  .\Use-DemoProfile.ps1 -Highlights   # swap in with them on, for highlighting.png
  .\Use-DemoProfile.ps1 -Restore      # swap back
#>
param([switch] $Restore, [switch] $Highlights)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$profileDir = Join-Path $env:APPDATA 'DanceCommander'
$realDir = Join-Path $env:APPDATA 'DanceCommander.real'

# The tabs (session.json) and the listing cache sit beside the installed app instead, and are
# rewritten on exit, so they are moved aside too. The app's own files there are left alone.
$appDir = Join-Path $env:LOCALAPPDATA 'DanceCommander'
$realLocal = Join-Path $env:LOCALAPPDATA 'DanceCommander.real-local'
$localState = 'session.json', 'listings.json', 'window.json'

if (Get-Process DanceCommander -ErrorAction SilentlyContinue) { throw 'Close Dance Commander first.' }

if ($Restore) {
    if (-not (Test-Path $realDir)) { throw "No $realDir to restore." }
    if (-not (Test-Path (Join-Path $profileDir '.demo-profile'))) { throw "$profileDir isn't the demo profile; not moving it." }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Rename-Item $profileDir "DanceCommander.demo-$stamp"
    Rename-Item $realDir 'DanceCommander'
    foreach ($name in $localState) {
        $demo = Join-Path $appDir $name
        if (Test-Path $demo) { Move-Item $demo (Join-Path $env:APPDATA "DanceCommander.demo-$stamp\local-$name") }
        if (Test-Path (Join-Path $realLocal $name)) { Move-Item (Join-Path $realLocal $name) $demo }
    }
    # Emptied by the moves above; left behind, it stops the next swap.
    if (-not (Get-ChildItem $realLocal -Force)) { Remove-Item $realLocal }
    Write-Output "Restored $profileDir and the session in $appDir."
    return
}

if (Test-Path $realDir) { throw "$realDir already exists: the demo profile is already in. Use -Restore first." }
if (Test-Path $realLocal) { throw "$realLocal already exists: a previous swap wasn't restored. Look in it first." }
New-Item -ItemType Directory -Path $realLocal | Out-Null
foreach ($name in $localState) {
    if (Test-Path (Join-Path $appDir $name)) { Move-Item (Join-Path $appDir $name) $realLocal }
}
Rename-Item $profileDir 'DanceCommander.real'
New-Item -ItemType Directory -Path $profileDir | Out-Null

function Put([string] $name, [string] $text) {
    [System.IO.File]::WriteAllText((Join-Path $profileDir $name), $text.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

$kit = $PSScriptRoot.Replace('\', '/')
$repoScripts = (Split-Path $PSScriptRoot).Replace('\', '/')

Put '.demo-profile' "Made by scripts/screenshot-kit/Use-DemoProfile.ps1; -Restore puts the real one back.`n"

# The user's own settings are neutral (shell choice, F10); keep them so the legend looks as it does.
Copy-Item (Join-Path $realDir 'settings.json') $profileDir

Put 'tools.json' @"
{
  "tools": [
    {
      "id": "build-orbit",
      "title": "Build and test",
      "group": "Build",
      "appliesTo": "directory|background",
      "exec": "pwsh",
      "args": ["-NoProfile", "-File", "$kit/mock-build.ps1"],
      "workingDirectory": "`${repoRoot}",
      "outputDirectory": "`${repoRoot}/src/Orbit.Cli/bin",
      "launch": "job",
      "inRepository": true,
      "keybinding": "Ctrl+Shift+F5"
    }
  ],
  "listings": [
    {
      "id": "gitlab",
      "title": "Pipelines",
      "exec": "pwsh",
      "args": ["-NoProfile", "-File", "$kit/mock-pipelines.ps1", "`${remoteUrl}", "`${branch}"],
      "script": true,
      "inRepository": true,
      "sort": "modified"
    },
    {
      "id": "git-gone",
      "title": "Deleted files",
      "exec": "pwsh",
      "args": ["-NoProfile", "-File", "$repoScripts/git-history.ps1", "`${repoRoot}"],
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
}
"@

# The examples from the Highlight rules page, as written there. Switched off unless -Highlights,
# as a fresh install ships them: every other shot shows the listing as a new user sees it.
$rules = @'
{
  "rules": [
    { "name": "Secrets",
      "match": { "regex": "(?i)(secret|credential|\\.pem$|\\.pfx$|id_rsa)" },
      "foreground": "#FF8B82", "bold": true },

    { "name": "Build output",
      "match": { "glob": "{bin,obj,node_modules,.vs,target}", "directoriesOnly": true },
      "foreground": "#7E868E", "italic": true },

    { "name": "Big files",
      "match": { "minSize": "100MB" },
      "background": "#3A2A00" },

    { "name": "Today",
      "match": { "newerThan": "12h", "filesOnly": true },
      "bold": true, "sortGroup": 1 }
  ]
}
'@
if (-not $Highlights) { $rules = $rules -replace '\{ "name": ', '{ "enabled": false, "name": ' }
Put 'highlighting.json' $rules

Put 'bookmarks.json' @'
{
  "bookmarks": [
    { "name": "Project", "uri": "file:///C:/src/orbit" },
    { "name": "Releases", "uri": "file:///C:/src/orbit/release" },
    { "name": "Scratch", "uri": "file:///C:/src/scratch" },
    { "name": "Test server", "uri": "sftp://demo@localhost:2222/config", "group": "Servers" }
  ]
}
'@

Write-Output "Demo profile in $profileDir; the real one is $realDir. Restore with -Restore."
