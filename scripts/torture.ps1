<#
.SYNOPSIS
  The torture run (PLAN.md Version 1, Safety): the file operations against real disks and servers.

.DESCRIPTION
  Runs the tests in category Torture (tests/DanceCommander.Tests/TortureTests.cs) after setting up
  what they need:

  - a file held open by another process, read-only files, paths past 260 characters and the
    longest name a file can have: nothing to set up, all under %TEMP%;
  - an SFTP server: the atmoz/sftp container, started and killed by the tests themselves on port
    2232 (needs Docker running; the image is pulled here);
  - a WebDAV server: wsgidav from the venv scripts/webdav-test-server.ps1 makes, started and
    killed by the tests on port 8128, so a server on 8127 is left alone;
  - a full volume, and a FAT32 one (a USB stick's 4 GB limit): 64 MB VHDs, made, mounted and
    removed here. Mounting one needs an elevated shell; without one those cases are skipped, and
    the run says so.

  Run before each release, not on every build: it takes a few minutes and moves a few hundred
  megabytes. It touches nothing outside %TEMP%, its own VHD, the two servers it starts and the
  Recycle Bin, whose items it takes back out by undo. The report is in temp/torture/.

.PARAMETER NoVolume
  Skip the full-volume cases even when elevated.
#>
param(
    [switch]$NoVolume
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo "temp\torture"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# WebDAV: the same venv the development server uses.
$venv = Join-Path $repo "temp\webdav-venv"
$wsgidav = Join-Path $venv "Scripts\wsgidav.exe"
if (-not (Test-Path $wsgidav)) {
    Write-Host "Creating the wsgidav virtual environment under temp/ ..."
    python -m venv $venv
    & (Join-Path $venv "Scripts\python.exe") -m pip install --quiet --disable-pip-version-check wsgidav cheroot
}
$env:DC_TORTURE_WSGIDAV = $wsgidav

# SFTP: the container's image, pulled once.
$image = "atmoz/sftp:alpine"
docker image inspect $image *> $null
if ($LASTEXITCODE -ne 0) {
    docker pull $image
    if ($LASTEXITCODE -ne 0) { throw "Couldn't pull $image; is Docker running?" }
}
$env:DC_TORTURE_SFTP_IMAGE = $image

# A full volume and a FAT32 stick: small VHDs, which only an elevated shell can mount.
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$mounted = @()

# Explorer, the indexer or Defender often has a new drive open for a moment, and a detach then
# fails: the first run left the VHD mounted without a word, and the next one stopped on it.
function Detach-Vhd([string]$vhd) {
    for ($try = 0; $try -lt 10 -and (Get-DiskImage -ImagePath $vhd -ErrorAction SilentlyContinue).Attached; $try++) {
        if ($try -gt 0) { Start-Sleep -Seconds 2 }
        Dismount-DiskImage -ImagePath $vhd -ErrorAction SilentlyContinue | Out-Null
    }
    return -not (Get-DiskImage -ImagePath $vhd -ErrorAction SilentlyContinue).Attached
}

function Mount-Vhd([string]$vhd, [string]$fs, [string]$label) {
    if (Test-Path $vhd) {
        if (-not (Detach-Vhd $vhd)) {
            throw "A VHD from an earlier run is still attached and won't let go: $vhd. Close what has its drive open, or detach it in Disk Management, and run again."
        }
        Remove-Item $vhd
    }
    $letter = (Get-ChildItem function:[d-z]: -Name | Where-Object { -not (Test-Path $_) } | Select-Object -Last 1).TrimEnd(':')
    @"
create vdisk file="$vhd" maximum=64 type=expandable
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=$fs quick label=$label
assign letter=$letter
"@ | diskpart | Out-Null
    if (-not (Test-Path "${letter}:\")) { throw "The VHD $vhd didn't mount at ${letter}:" }
    return $letter
}

function Dismount-Vhd([string]$vhd, [string]$letter) {
    if (Detach-Vhd $vhd) {
        Remove-Item $vhd -ErrorAction SilentlyContinue
    }
    else {
        Write-Warning "The VHD is still mounted at ${letter}: ($vhd). The next elevated run tries again, or detach it in Disk Management."
    }
}

if ($NoVolume) {
    Write-Host "Full-volume and FAT32 cases: skipped (-NoVolume)."
}
elseif (-not $elevated) {
    Write-Host "Full-volume and FAT32 cases: skipped. Mounting a VHD needs an elevated shell; run this again as administrator for them."
}
else {
    $vhd = Join-Path $env:TEMP "dc-torture-small.vhdx"
    $letter = Mount-Vhd $vhd ntfs DCTORTURE
    $mounted += , @($vhd, $letter)
    $env:DC_TORTURE_SMALL_VOLUME = "${letter}:\"
    Write-Host "Full volume: a 64 MB VHD at ${letter}:"

    # A stick's file system: the 4 GB limit is checked before anything is written, so the volume
    # can be small. The 5 GB sources are made on the system disk by setting a length, not writing.
    $vhd = Join-Path $env:TEMP "dc-torture-fat32.vhdx"
    $letter = Mount-Vhd $vhd fat32 DCFAT32
    $mounted += , @($vhd, $letter)
    $env:DC_TORTURE_FAT_VOLUME = "${letter}:\"
    Write-Host "FAT32: a 64 MB VHD at ${letter}:"
}

$env:DC_TORTURE = "1"
try {
    dotnet test (Join-Path $repo "tests\DanceCommander.Tests") --filter "Category=Torture" `
        --logger "console;verbosity=detailed" --logger "trx;LogFileName=torture.trx" --results-directory $out
    $result = $LASTEXITCODE
}
finally {
    Remove-Item Env:DC_TORTURE, Env:DC_TORTURE_SMALL_VOLUME, Env:DC_TORTURE_FAT_VOLUME -ErrorAction SilentlyContinue
    foreach ($m in $mounted) { Dismount-Vhd $m[0] $m[1] }
}

Write-Host ""
Write-Host "Report: $out\torture.trx (each case's output says what was asked, logged and left behind)."
exit $result
