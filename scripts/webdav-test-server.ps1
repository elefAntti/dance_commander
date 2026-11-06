<#
.SYNOPSIS
  Runs a local WebDAV server to develop and test the WebDAV provider against.

.DESCRIPTION
  WebDAV is the one provider that cannot be tested against the filesystem or a fixture, because
  the thing being tested is the conversation with a server. This starts a real one on localhost.

  wsgidav rather than a container: it installs into a throwaway virtual environment under temp/,
  starts in about a second, serves a directory you can inspect in Explorer while the tests run,
  and needs no daemon running in the background. The one thing it is not is a second
  implementation -- a provider that works here should still be tried against a real server
  (Nextcloud, IIS, Apache mod_dav) before anyone trusts it, because WebDAV servers disagree with
  each other far more than the specification suggests.

.PARAMETER Port
  Defaults to 8127, which is unlikely to collide with anything already running.

.PARAMETER Root
  Directory to serve. Defaults to temp/webdav-root, created and seeded if absent.

.PARAMETER Anonymous
  Serve without authentication. The default requires dance/commander, because authentication is
  part of what needs testing and a provider only ever tried anonymously will fail on first use.
#>
param(
    [int]$Port = 8127,
    [string]$Root = "",
    [switch]$Anonymous
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $repo "temp\webdav-venv"
$python = Join-Path $venv "Scripts\python.exe"
$wsgidav = Join-Path $venv "Scripts\wsgidav.exe"

if (-not (Test-Path $python)) {
    Write-Host "Creating the virtual environment under temp/ ..."
    python -m venv $venv
    & $python -m pip install --quiet --disable-pip-version-check wsgidav cheroot
}

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $repo "temp\webdav-root"
}

if (-not (Test-Path $Root)) {
    New-Item -ItemType Directory -Path $Root | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Root "documents") | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Root "documents\nested") | Out-Null

    # Seeded with the cases that break providers rather than with lorem ipsum: a space and a
    # non-ASCII character in a name, a file large enough that a range request means something,
    # and an archive, because browsing a zip over WebDAV is the thing the seekable-read path is for.
    Set-Content -Path (Join-Path $Root "readme.txt") -Value "Hello from wsgidav." -Encoding utf8
    Set-Content -Path (Join-Path $Root "a file with spaces.txt") -Value "Spaces in the name." -Encoding utf8
    # Built from character codes rather than typed literally: PowerShell reads a .ps1 with no
    # byte-order mark as ANSI, which turns a literal "ä" in this file into a double-encoded name
    # on disk -- and then the fixture tests the seeding script's encoding instead of the provider's.
    $nonAscii = "$([char]0xE4)$([char]0xE4)kk$([char]0xF6)set.md"
    Set-Content -Path (Join-Path $Root "documents\$nonAscii") -Value "# Not ASCII" -Encoding utf8
    Set-Content -Path (Join-Path $Root "documents\nested\deep.txt") -Value "Two levels down." -Encoding utf8

    $big = Join-Path $Root "large.bin"
    $bytes = New-Object byte[] (4 * 1024 * 1024)
    (New-Object Random 1).NextBytes($bytes)
    [IO.File]::WriteAllBytes($big, $bytes)

    Compress-Archive -Path (Join-Path $Root "readme.txt") -DestinationPath (Join-Path $Root "archive.zip") -Force
    Write-Host "Seeded $Root"
}

$config = Join-Path $repo "temp\wsgidav.yaml"

if ($Anonymous) {
    @"
host: 127.0.0.1
port: $Port
provider_mapping:
  "/": "$($Root -replace '\\','/')"
simple_dc:
  user_mapping:
    "*": true
verbose: 1
"@ | Set-Content -Path $config -Encoding utf8
}
else {
    @"
host: 127.0.0.1
port: $Port
provider_mapping:
  "/": "$($Root -replace '\\','/')"
simple_dc:
  user_mapping:
    "*":
      dance:
        password: commander
verbose: 1
"@ | Set-Content -Path $config -Encoding utf8
}

Write-Host ""
Write-Host "WebDAV on http://127.0.0.1:$Port/  serving $Root"
if (-not $Anonymous) { Write-Host "Credentials: dance / commander" }
Write-Host "Address to type into Dance Commander:  dav://127.0.0.1:$Port/"
Write-Host "Ctrl+C to stop."
Write-Host ""

& $wsgidav --config $config
