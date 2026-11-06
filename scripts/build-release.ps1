<#
.SYNOPSIS
  Builds a release: the plain zip and the MSI (PLAN.md §3, Packaging), from one publish.
.DESCRIPTION
  Into artifacts\release\<version>\:
    DanceCommander-<version>-win-x64.zip   to try it without installing; touches no registry
    DanceCommander-<version>-win-x64.msi   the recommended one: per user, Start menu, uninstall,
                                           the Explorer entry, a warm-up run (installer\Package.wxs)
  The version is Directory.Build.props'. The tests run first, and the build stops on uncommitted
  changes (about:app shows the commit) and on third-party notices that are out of date (run
  scripts\notices.ps1 and commit). Never into the folder the running app is published to
  (scripts\publish-local.bat), and never over an earlier build of the same version: it stops
  instead, so the version is bumped or the old folder moved away by hand.
.PARAMETER AllowDirty
  Build with uncommitted changes, for trying the packaging itself. Not for a release.
.PARAMETER SkipTests
  Leave the tests out, for the same reason.
#>
[CmdletBinding()]
param(
    [switch]$AllowDirty,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$app = Join-Path $root 'src\DanceCommander.App\DanceCommander.App.csproj'

$version = (& dotnet msbuild $app -getProperty:Version).Trim()
if (-not $version) { throw 'Could not read the version from Directory.Build.props.' }
$out = Join-Path $root "artifacts\release\$version"
$publish = Join-Path $out 'publish'
$name = "DanceCommander-$version-win-x64"
Write-Host "Dance Commander $version -> $out"

# A second MSI of one version is a different product to Windows Installer: it installs beside the
# first rather than replacing it (review, 2026-10-16). So one build per version.
if (Test-Path $out) {
    throw "$out is already there. Bump the version in Directory.Build.props; a second MSI of the same version would install beside the first."
}

if (-not $AllowDirty) {
    $changes = & git -C $root status --porcelain
    if ($changes) { throw "Uncommitted changes; commit them first (or -AllowDirty to try the packaging):`n$($changes -join "`n")" }
}

# The notices as they would be written now, beside the committed ones, compared. After a restore:
# the package list is read from the last one, and a package bumped since would pass against the
# old notices and then ship (review, 2026-10-16).
& dotnet restore $app
if ($LASTEXITCODE) { throw 'The restore failed.' }
$fresh = Join-Path ([IO.Path]::GetTempPath()) "dc-notices-$([Guid]::NewGuid().ToString('N')).txt"
& (Join-Path $PSScriptRoot 'notices.ps1') -Out $fresh
$committed = Get-Content (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Raw
if ((Get-Content $fresh -Raw) -ne $committed) {
    throw "THIRD-PARTY-NOTICES.txt is out of date: run scripts\notices.ps1 and commit it. The new text is in $fresh."
}
# Kept above, where it shows what changed; here it is the committed text again, made a moment ago.
Remove-Item $fresh

if (-not $SkipTests) {
    & dotnet test (Join-Path $root 'DanceCommander.sln') -c Release
    if ($LASTEXITCODE) { throw 'The tests failed.' }
}

# The same publish as scripts\publish-local.bat, into the release folder.
& dotnet publish $app -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE) { throw 'The publish failed.' }

# The MSI names its files one by one (installer\Package.wxs) and the zip takes the whole folder,
# so a file the publish gained would be in the zip and missing from the MSI. Stopped here instead.
$wxs = Get-Content (Join-Path $root 'installer\Package.wxs') -Raw
$inMsi = [regex]::Matches($wxs, 'Source="\$\(var\.PublishDir\)([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object
$published = Get-ChildItem $publish -File -Recurse | ForEach-Object { $_.FullName.Substring($publish.Length + 1) } | Sort-Object
$difference = Compare-Object @($inMsi) @($published)
if ($difference) {
    $lines = $difference | ForEach-Object { "  $($_.InputObject): only in the $(if ($_.SideIndicator -eq '<=') { 'MSI' } else { 'publish' })" }
    throw ("The MSI's files (installer\Package.wxs) and the publish differ:`n" + ($lines -join "`n"))
}

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $out "$name.zip")

# A forward slash to end PublishDir: a backslash before the closing quote would be read as an
# escaped quote by the argument rules, and the path would end in a quote.
$publishDir = ($publish -replace '\\', '/') + '/'
& dotnet build (Join-Path $root 'installer\DanceCommander.Installer.wixproj') -c Release -o $out `
    "-p:PublishDir=$publishDir" "-p:ProductVersion=$version"
if ($LASTEXITCODE) { throw 'The installer build failed.' }

Get-ChildItem $out -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
