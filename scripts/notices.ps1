<#
.SYNOPSIS
  Writes THIRD-PARTY-NOTICES.txt: the licence of everything a published build ships.

.DESCRIPTION
  Run before each release, after a restore, and commit the result. The packages are the ones the
  application resolves (dotnet list package --include-transitive), read from the NuGet cache:
  each package's own licence file where it ships one, otherwise its declared licence with its
  copyright line. Packages with the same text (the Microsoft.Extensions family, the runtime's
  own) are listed together and the text given once. The .NET and Windows Desktop runtimes are
  added at the end, since the self-contained publish bundles them.

  The README's licence section says a published build must carry these notices; the app copies
  the file next to the executable and lists it on about:app.
#>
[CmdletBinding()]
param(
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $Out) { $Out = Join-Path $root 'THIRD-PARTY-NOTICES.txt' }
$project = Join-Path $root 'src\DanceCommander.App\DanceCommander.App.csproj'
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

# Build-time only: analysers never reach the user.
$notShipped = '^(Microsoft\.CodeAnalysis\.|Microsoft\.NET\.ILLink)'

# Packages that ship no licence text and declare it only by URL; checked by hand.
$known = @{
    'DotNet.Glob' = @{ License = 'MIT'; Copyright = 'Copyright (c) 2015 - present Darrell Tunnell' }
}

$mit = @'
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
'@
$apache = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'assets\licenses\Apache-2.0.txt')

$listing = dotnet list $project package --include-transitive --format json | Out-String | ConvertFrom-Json
$framework = $listing.projects[0].frameworks[0]
$packages = @($framework.topLevelPackages) + @($framework.transitivePackages) |
    Where-Object { $_.id -notmatch $notShipped } |
    Sort-Object id -Unique

function Read-Package($id, $version) {
    $dir = Join-Path $cache (Join-Path $id.ToLowerInvariant() $version)
    if (-not (Test-Path $dir)) { throw "$id $version is not in the NuGet cache ($dir): restore first." }

    [xml]$spec = Get-Content -Raw -Encoding UTF8 (Get-ChildItem $dir -Filter '*.nuspec' | Select-Object -First 1).FullName
    $meta = $spec.package.metadata
    $expression = if ($meta.license -and $meta.license.type -eq 'expression') { $meta.license.'#text' } else { $null }
    # A package that declares no copyright line gets its authors named, not a line made up for it.
    $copyright = if ($meta.copyright) { $meta.copyright } else { "by $($meta.authors)" }
    if ($known.ContainsKey($id)) {
        $expression = $known[$id].License
        $copyright = $known[$id].Copyright
    }

    # The package's own licence text, where it ships one, says more than a template can.
    $file = Get-ChildItem $dir -File | Where-Object { $_.Name -match '^licen[cs]e(\.(txt|md))?$' } | Select-Object -First 1
    $text = if ($file) { (Get-Content -Raw -Encoding UTF8 $file.FullName).Trim() }
        elseif ($expression -eq 'MIT') { "MIT License`n`n$copyright`n`n$mit".Trim() }
        elseif ($expression -eq 'Apache-2.0') { $apache.Trim() }
        else { throw "$id $version has no licence text and no known licence ($expression): add it to `$known." }

    # And the notices of what the package itself is built from, which travel with it.
    $notices = Get-ChildItem $dir -File | Where-Object { $_.Name -match '^third-?party-?notices\.txt$' } | Select-Object -First 1
    if ($notices) { $text += "`n`n" + (Get-Content -Raw -Encoding UTF8 $notices.FullName).Trim() }

    [pscustomobject]@{ Name = "$id $version"; License = $expression; Copyright = $copyright; Text = $text }
}

$read = foreach ($p in $packages) { Read-Package $p.id $p.resolvedVersion }

# The runtimes the self-contained publish carries, at the version this machine's SDK bundles: the
# SDK's own number, not the newest pack in the cache, which anything else may have put there
# (review, 2026-10-16). The desktop runtime ships at the same version.
$bundled = (& dotnet msbuild $project -getProperty:BundledNETCoreAppPackageVersion).Trim()
if (-not $bundled) { throw 'Could not read the runtime version the SDK bundles.' }
$runtimes = foreach ($pack in 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') {
    $dir = Join-Path $cache (Join-Path $pack.ToLowerInvariant() $bundled)
    if (-not (Test-Path $dir)) { throw "$pack $bundled is not in the NuGet cache: publish once first." }
    $versions = @(Get-Item $dir)
    $license = Get-ChildItem $dir -File | Where-Object { $_.Name -match '^licen[cs]e(\.txt)?$' } | Select-Object -First 1
    $notices = Get-ChildItem $dir -File | Where-Object { $_.Name -match '^third-party-notices\.txt$' } | Select-Object -First 1
    [pscustomobject]@{
        Name = "$pack $($versions[0].Name)"
        License = 'MIT'
        Copyright = ''
        Text = ((Get-Content -Raw -Encoding UTF8 $license.FullName).Trim() +
            $(if ($notices) { "`n`n" + (Get-Content -Raw -Encoding UTF8 $notices.FullName).Trim() } else { '' }))
    }
}

$rule = '=' * 100
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('Dance Commander: third-party notices')
$lines.Add('')
$lines.Add('Dance Commander is MIT licensed (LICENSE.txt). It ships the software below, each under its own')
$lines.Add('licence. Generated by scripts/notices.ps1; do not edit by hand.')
$lines.Add('')

# In the order of each group's first package: Group-Object's own order differs between Windows
# PowerShell and PowerShell 7, and the release build compares this file with a fresh one.
foreach ($group in @($read) + @($runtimes) | Group-Object Text | Sort-Object { $_.Group[0].Name }) {
    $lines.Add($rule)
    foreach ($item in $group.Group) {
        $lines.Add($(if ($item.Copyright -and $item.Text -notmatch [regex]::Escape($item.Copyright)) { "$($item.Name) ($($item.License)), $($item.Copyright)" } else { "$($item.Name) ($($item.License))" }))
    }
    $lines.Add($rule)
    $lines.Add('')
    $lines.Add($group.Name)
    $lines.Add('')
}

# CRLF, as Notepad and every other Windows reader expects of a .txt beside an .exe.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Out), (($lines -join "`n") -replace "`r?`n", "`r`n"), $utf8)
Write-Host "Wrote ${Out}:$(@($read).Count) packages and $(@($runtimes).Count) runtimes, $(@($read + $runtimes | Group-Object Text).Count) licence texts."
