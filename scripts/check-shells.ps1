<#
.SYNOPSIS
  Starts an SSH server with fish and zsh accounts, seeded with the awkward names the quoting
  checks need (PLAN.md, *Security and file-safety fixes from BUGS.md*; CHECKS_FOR_0.9.md).

.DESCRIPTION
  Builds scripts/check-shells into the image dc-check-shells (once), runs it on 127.0.0.1:2233
  and puts these files in each account's home, so a line run from Dance Commander's command line
  meets them:
    a\                    a file whose name ends in a backslash
    ; echo INJECTED #     a name that is a command if it is pasted unquoted
    -rf                   a name that is an option if it is pasted without ./
    =bash                 a name zsh expands to /bin/bash if unquoted
    keep.txt              something that must still be there afterwards

  Accounts (password "commander" for both):
    sftp://fishy@127.0.0.1:2233/home/fishy    login shell fish
    sftp://zshy@127.0.0.1:2233/home/zshy      login shell zsh

  It also makes %TEMP%\dc-check-cmd with folders %CD% and %USERPROFILE% and files
  report%20final%20.pdf and bang!name.txt, for the same checks in cmd.

  The host key is new each time the container is made, so the first connection asks to trust
  it; -Stop removes the container, and the entry it left in known_hosts can be removed by hand.

.PARAMETER Stop
  Removes the container.

.PARAMETER Seed
  Puts the files back (after a check removed some) without making the container again.
#>
param(
    [switch]$Stop,
    [switch]$Seed
)

# Not 'Stop': Windows PowerShell makes a native command's stderr a terminating error under it, and
# docker writes there for things that are fine (no such container yet). Exit codes are checked.
$name = 'dc-check-shells'
$port = 2233

if ($Stop) {
    if (docker ps -aq --filter "name=^$name`$") { docker rm -f $name | Out-Null }
    Write-Host "Removed $name."
    return
}

if (-not $Seed) {
    if (-not (docker images -q $name)) {
        docker build -t $name (Join-Path $PSScriptRoot 'check-shells')
        if ($LASTEXITCODE -ne 0) { throw "Building $name failed." }
    }
    if (docker ps -aq --filter "name=^$name`$") { docker rm -f $name | Out-Null }
    docker run -d --name $name -p "127.0.0.1:${port}:22" $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Starting $name failed." }
}

# Made by each account itself, so they belong to it. The names go in on stdin, one a line, and
# reach touch NUL-separated: Windows PowerShell splits native arguments that hold quotes, and a
# quoted "$n" in a script argument made "; echo INJECTED #" into four files.
# The pipe adds a line break of its own, so empty lines are dropped.
$names = "a\`n; echo INJECTED #`n-rf`n=bash`nkeep.txt`n"
foreach ($user in 'fishy', 'zshy') {
    $names | docker exec -i -u $user -w "/home/$user" $name sh -c 'tr -d \\r | sed /^$/d | tr \\n \\0 | xargs -0 touch --'
    if ($LASTEXITCODE -ne 0) { throw "Seeding /home/$user failed." }
}

# And for cmd (Ctrl+E to it): names cmd would read as variables or delayed expansion.
$local = Join-Path $env:TEMP 'dc-check-cmd'
New-Item -ItemType Directory -Force $local | Out-Null
foreach ($folder in '%CD%', '%USERPROFILE%') {
    New-Item -ItemType Directory -Force -Path (Join-Path $local $folder) | Out-Null
}
foreach ($file in 'report%20final%20.pdf', 'bang!name.txt', 'keep.txt') {
    $path = Join-Path $local $file
    if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType File -Path $path | Out-Null }
}
Write-Host "Local, for cmd: $local"

Write-Host "Ready on 127.0.0.1:${port} (password: commander):"
Write-Host "  sftp://fishy@127.0.0.1:${port}/home/fishy   (fish)"
Write-Host "  sftp://zshy@127.0.0.1:${port}/home/zshy     (zsh)"
Write-Host "See what is left with: docker exec $name ls -la /home/fishy /home/zshy"
