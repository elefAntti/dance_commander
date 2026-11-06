<#
.SYNOPSIS
  A throwaway SFTP server for the site's screenshots: sftp://demo@localhost:2222/config, password
  "demo". Its host key is new every time the container is made, so the host key question appears.

  -Stop removes the container. -Port puts it on another port when 2222 is taken (a dev container
  often has it); it needs PowerShell 7, since Windows PowerShell stops at docker's stderr.
#>
param([switch] $Stop, [int] $Port = 2222)

$ErrorActionPreference = 'Stop'
$name = 'dc-shots-sftp'

if ($Stop) { docker rm -f $name | Out-Null; return }

docker rm -f $name 2>$null | Out-Null
docker run -d --name $name -p 127.0.0.1:${Port}:2222 `
    -e USER_NAME=demo -e USER_PASSWORD=demo -e PASSWORD_ACCESS=true -e PUID=1000 -e PGID=1000 `
    lscr.io/linuxserver/openssh-server:latest | Out-Null

# Something to look at once connected.
Start-Sleep -Seconds 5
# No double quotes in the script: Windows PowerShell passes them to docker unescaped. And no
# carriage returns: a checkout can make this file CRLF, and sh would take each \r as part of a command.
$setup = @'
mkdir -p /config/deploy /config/orbit/logs /config/backups &&
printf 'endpoint=https://telemetry.example.com\n' > /config/deploy/orbit.conf &&
printf '#!/bin/sh\nsystemctl restart orbit\n' > /config/deploy/restart.sh && chmod +x /config/deploy/restart.sh &&
cd /config/orbit/logs &&
# Rotated logrotate-style: short names fit beside the Perms and Owner columns. The container runs
# on UTC, so times are given in UTC: 20:59 reads as 22:59 or 23:59 in Finland, either side of the change from summer time.
for d in 18 19 20 21 22 23 24 25 26 27; do
  n=$(( (d * 37) % 900 + 300 ));
  k=$((28 - d)); f=orbit.log.$k;
  for i in $(seq $n); do printf '2026-10-%s 09:%02d:%02d INFO orbit.telemetry sent batch %d (%d events)\n' $d $((i % 60)) $((i * 7 % 60)) $i $((i * 13 % 500)); done > $f;
  if [ $k -ge 5 ]; then gzip $f; f=$f.gz; fi;
  touch -d 2026-10-${d}T20:59:00 $f;
done &&
for i in $(seq 1200); do printf '2026-10-28 10:%02d:%02d INFO orbit.telemetry sent batch %d\n' $((i % 60)) $((i * 7 % 60)) $i; done > orbit.log &&
touch -d 2026-10-28T08:59:41 orbit.log &&
printf '2026-10-27 14:02:11 ERROR orbit.exporter timeout after 30 s\n' > error.log &&
touch -d 2026-10-27T12:02:11 error.log &&
printf 'Test server for screenshots.\n' > /config/README.md &&
chown -R 1000:1000 /config/deploy /config/orbit /config/backups /config/README.md
'@
docker exec $name sh -c ($setup -replace "`r", '')
Write-Output "sftp://demo@localhost:$Port/config (password demo)"
