<#
.SYNOPSIS
  A stand-in for gitlab-pipelines.ps1 that needs no GitLab: the same levels and the same rows, made
  up, so the site's pipeline screenshot shows no one's real project.

.DESCRIPTION
  Same protocol as scripts/gitlab-pipelines.ps1 (list / read <crumbs>), same row format and the same
  names ("#1287 running - 3f9a2c1e", "test - unit - success"), so the listing looks exactly as the
  real one does. Times are relative to now, so the Modified column always reads "a moment ago".

  artifacts.zip is a real zip built on the fly, so it opens as an archive in the pane.

.EXAMPLE
    pwsh -File mock-pipelines.ps1 https://gitlab.example.com/space/orbit.git feature/telemetry list ""
#>
param(
    [Parameter(Position = 0)] [string] $RemoteUrl = '',
    [Parameter(Position = 1)] [string] $Branch = '',
    [Parameter(Position = 2)] [string] $Verb = '',
    [Parameter(Position = 3)] [string] $Crumbs = ''
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$out = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), [System.Text.UTF8Encoding]::new($false))
$out.NewLine = "`n"
$now = [DateTimeOffset]::UtcNow

function Fail([string] $message, [int] $code = 1) {
    [Console]::Error.WriteLine($message)
    exit $code
}

function Row([string] $kind, [string] $name, $size, $minutesAgo, [string] $crumb) {
    $sizeText = if ($null -ne $size) { "$size" } else { '' }
    $when = if ($null -ne $minutesAgo) { $now.AddMinutes(-$minutesAgo).ToString('o') } else { '' }
    $out.WriteLine("$kind`t$name`t$sizeText`t$when`t$crumb")
}

# Newest first, as the real script sorts them.
$pipelines = @(
    @{ Id = 1287; Status = 'running'; Sha = '3f9a2c1e'; Ago = 4 }
    @{ Id = 1284; Status = 'success'; Sha = 'b71d09a4'; Ago = 52 }
    @{ Id = 1279; Status = 'failed';  Sha = 'e2c4518f'; Ago = 190 }
    @{ Id = 1272; Status = 'success'; Sha = '90af3d27'; Ago = 1460 }
    @{ Id = 1268; Status = 'canceled'; Sha = '5c0e7b16'; Ago = 1555 }
    @{ Id = 1261; Status = 'success'; Sha = 'd4419e83'; Ago = 2930 }
)

$allBranches = @(
    @{ Id = 1288; Status = 'running'; Sha = 'a0c3f5d2'; Ago = 2; Ref = 'main' }
    @{ Id = 1287; Status = 'running'; Sha = '3f9a2c1e'; Ago = 4; Ref = 'feature/telemetry' }
    @{ Id = 1286; Status = 'success'; Sha = '7e11b0c9'; Ago = 31; Ref = 'fix/retry-backoff' }
    @{ Id = 1285; Status = 'failed';  Sha = 'c93d2a70'; Ago = 47; Ref = 'docs/install' }
    @{ Id = 1284; Status = 'success'; Sha = 'b71d09a4'; Ago = 52; Ref = 'feature/telemetry' }
    @{ Id = 1283; Status = 'success'; Sha = '18f6e4b2'; Ago = 75; Ref = 'main' }
)

# stage, name, status, minutes after the pipeline started, has artifacts
$jobs = @(
    ,@('build',   'compile',     'success', 0, $true)
    ,@('build',   'lint',        'success', 1, $false)
    ,@('test',    'unit',        'success', 2, $true)
    ,@('test',    'integration', 'success', 3, $true)
    ,@('package', 'zip',         'success', 4, $true)
    ,@('deploy',  'staging',     'manual',  5, $false)
)

function PipelineRows($list, [bool] $showRef) {
    foreach ($p in $list) {
        $name = "#$($p.Id) $($p.Status)"
        if ($showRef) { $name += " - $($p.Ref)" }
        $name += " - $($p.Sha)"
        Row 'd' $name $null $p.Ago "$($p.Id)"
    }
}

function Pipeline([string] $id) {
    $p = @($pipelines + $allBranches | Where-Object { "$($_.Id)" -eq $id }) | Select-Object -First 1
    if ($null -eq $p) { Fail "No pipeline $id." 3 }
    return $p
}

# A running pipeline's later jobs haven't run yet, and a failed one failed at integration.
function JobStatus($p, [int] $index) {
    $status = $jobs[$index][2]
    if ($p.Status -eq 'running') { return @('success', 'success', 'success', 'running', 'created', 'manual')[$index] }
    if ($p.Status -eq 'failed' -and $index -eq 3) { return 'failed' }
    if ($p.Status -eq 'failed' -and $index -gt 3) { return 'skipped' }
    if ($p.Status -eq 'canceled' -and $index -ge 2) { return 'canceled' }
    return $status
}

function Jobs([string] $pipelineId) {
    $p = Pipeline $pipelineId
    for ($i = 0; $i -lt $jobs.Count; $i++) {
        $j = $jobs[$i]
        Row 'd' "$($j[0]) - $($j[1]) - $(JobStatus $p $i)" $null ([Math]::Max(0, $p.Ago - $j[3])) "$($p.Id)0$i"
    }
}

function JobOf([string] $jobId) {
    $p = Pipeline $jobId.Substring(0, $jobId.Length - 2)
    $i = [int] $jobId.Substring($jobId.Length - 1)
    return @{ Pipeline = $p; Index = $i; Job = $jobs[$i]; Status = (JobStatus $p $i) }
}

function JobFiles([string] $jobId) {
    $j = JobOf $jobId
    $ago = [Math]::Max(0, $j.Pipeline.Ago - $j.Job[3])
    if ($j.Job[4] -and $j.Status -in @('success', 'failed')) {
        Row 'f' 'artifacts.zip' (ArtifactBytes $j).Length $ago 'artifacts.zip'
    }
    # No size, as the real script gives none: it can't know without downloading the log.
    Row 'f' 'job.log' $null $ago 'job.log'
}

function JobLog($j) {
    $stage, $name = $j.Job[0], $j.Job[1]
    $lines = @(
        "Running with gitlab-runner 17.4.0 (b92ee590)"
        "  on orbit-runner-2 xT4kq9Lz, system ID: s_0f3c92a1b5e8"
        "Preparing the ""docker"" executor"
        "Using Docker executor with image mcr.microsoft.com/dotnet/sdk:8.0 ..."
        "Pulling docker image mcr.microsoft.com/dotnet/sdk:8.0 ..."
        "Preparing environment"
        "Running on runner-xt4kq9lz-project-118-concurrent-0 via orbit-runner-2..."
        "Getting source from Git repository"
        "Fetching changes with git depth set to 20..."
        "Checking out $($j.Pipeline.Sha) as detached HEAD (ref is $Branch)..."
        "Executing ""step_script"" stage of the job script"
    )
    $lines += switch ("$stage/$name") {
        'build/compile' {
            '$ dotnet restore Orbit.sln'
            '  Determining projects to restore...'
            '  Restored /builds/space/orbit/src/Orbit.Core/Orbit.Core.csproj (in 2.41 sec).'
            '  Restored /builds/space/orbit/src/Orbit.Cli/Orbit.Cli.csproj (in 2.43 sec).'
            '$ dotnet build Orbit.sln -c Release --no-restore'
            '  Orbit.Core -> /builds/space/orbit/src/Orbit.Core/bin/Release/net8.0/Orbit.Core.dll'
            '  Orbit.Cli -> /builds/space/orbit/src/Orbit.Cli/bin/Release/net8.0/orbit.dll'
            'Build succeeded.'
            '    0 Warning(s)'
            '    0 Error(s)'
        }
        'test/unit' {
            '$ dotnet test tests/Orbit.Tests --logger "junit;LogFilePath=test-results.xml"'
            'Passed!  - Failed:     0, Passed:   214, Skipped:     2, Total:   216, Duration: 6 s'
        }
        'test/integration' {
            '$ dotnet test tests/Orbit.Integration --logger "junit;LogFilePath=test-results.xml"'
            if ($j.Status -eq 'failed') {
                '  Failed Orbit.Integration.ExportTests.Exports_survive_a_dropped_connection [5 s]'
                '  Error Message:'
                '   Expected 120 samples, received 118.'
                'Failed!  - Failed:     1, Passed:    37, Skipped:     0, Total:    38, Duration: 41 s'
            } else {
                'Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 39 s'
            }
        }
        'package/zip' {
            '$ dotnet publish src/Orbit.Cli -c Release -r win-x64 -o out/orbit-1.4.0-win-x64'
            '$ cd out && zip -r ../orbit-1.4.0-win-x64.zip orbit-1.4.0-win-x64'
        }
        default { "$ ./ci/$name.sh" }
    }
    $lines += @(
        "Uploading artifacts for $(if ($j.Status -eq 'failed') { 'failed' } else { 'successful' }) job"
        'Cleaning up project directory and file based variables'
    )
    $lines += if ($j.Status -eq 'failed') { 'ERROR: Job failed: exit code 1' } else { 'Job succeeded' }
    return ($lines -join "`n") + "`n"
}

function ArtifactBytes($j) {
    $files = switch ("$($j.Job[0])/$($j.Job[1])") {
        'build/compile' { [ordered]@{
            'src/Orbit.Core/bin/Release/net8.0/Orbit.Core.dll' = 'MZ' + ('.' * 48000)
            'src/Orbit.Cli/bin/Release/net8.0/orbit.dll' = 'MZ' + ('.' * 21000)
            'build-info.json' = "{ ""version"": ""1.4.0-telemetry.17"", ""commit"": ""$($j.Pipeline.Sha)"" }`n" } }
        'package/zip' { [ordered]@{
            'orbit-1.4.0-win-x64/orbit.exe' = 'MZ' + ('.' * 140000)
            'orbit-1.4.0-win-x64/Orbit.Core.dll' = 'MZ' + ('.' * 48000)
            'orbit-1.4.0-win-x64/orbit.json' = "{ ""endpoint"": ""https://telemetry.example.com"" }`n"
            'orbit-1.4.0-win-x64/README.md' = "# Orbit 1.4.0`n`nRun ``orbit export --since 1h``.`n"
            'orbit-1.4.0-win-x64/LICENSE' = "MIT License`n" } }
        default { [ordered]@{
            'test-results.xml' = "<?xml version=""1.0""?>`n<testsuites tests=""216"" failures=""0"" />`n"
            'coverage/coverage.cobertura.xml' = "<?xml version=""1.0""?>`n<coverage line-rate=""0.83"" />`n" } }
    }
    $buffer = [System.IO.MemoryStream]::new()
    $zip = [System.IO.Compression.ZipArchive]::new($buffer, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    foreach ($name in $files.Keys) {
        $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Fastest)
        $entry.LastWriteTime = $now.AddMinutes(-($j.Pipeline.Ago - $j.Job[3]))
        $stream = $entry.Open()
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($files[$name])
        # Binaries as noise, so the zip is as big as a real one would be and not a few hundred bytes.
        if ($files[$name].StartsWith('MZ')) { [System.Random]::new($bytes.Length).NextBytes($bytes) }
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Dispose()
    }
    $zip.Dispose()
    return $buffer.ToArray()
}

if ($Verb -notin @('list', 'read')) { Fail "mock-pipelines.ps1: unknown verb '$Verb' (expected list or read)." 2 }

$parts = @($Crumbs.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
$scope = 'branch'
if ($parts.Count -gt 0 -and $parts[0] -eq 'all') { $scope = 'all' }
elseif ($parts.Count -gt 0 -and $parts[0] -eq 'mr-42') { $scope = 'mr' }
$rest = @(if ($scope -eq 'branch') { $parts } else { $parts | Select-Object -Skip 1 })

try {
    if ($Verb -eq 'list') {
        switch ($rest.Count) {
            0 {
                switch ($scope) {
                    'branch' {
                        Row 'd' '!42 Export telemetry to OpenTelemetry' $null 4 'mr-42'
                        PipelineRows $pipelines $false
                        Row 'd' '(all branches)' $null $null 'all'
                    }
                    'all' { PipelineRows $allBranches $true }
                    'mr' { PipelineRows @($pipelines | Select-Object -First 3) $false }
                }
            }
            1 { Jobs $rest[0] }
            2 { JobFiles $rest[1] }
            default { Fail "Nothing below $Crumbs." 3 }
        }
        exit 0
    }

    if ($rest.Count -ne 3) { Fail "Not a file: $Crumbs." 3 }
    $j = JobOf $rest[1]
    $bytes = switch ($rest[2]) {
        'artifacts.zip' { ArtifactBytes $j }
        'job.log' { [System.Text.Encoding]::UTF8.GetBytes((JobLog $j)) }
        default { Fail "No file called $($rest[2])." 3 }
    }
    $out.Flush()
    $stdout = [Console]::OpenStandardOutput()
    $stdout.Write($bytes, 0, $bytes.Length)
    $stdout.Flush()
    exit 0
}
finally {
    $out.Flush()
}
