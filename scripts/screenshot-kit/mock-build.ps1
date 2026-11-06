<#
.SYNOPSIS
  A make-believe build for the job-strip screenshot: prints what a dotnet build and test run would,
  slowly enough to catch it mid-run, and builds nothing.
#>
param([int] $Seconds = 90)

$projects = 'Orbit.Core', 'Orbit.Telemetry', 'Orbit.Export', 'Orbit.Cli', 'Orbit.Tests', 'Orbit.Integration'
$delay = [Math]::Max(100, $Seconds * 1000 / 60)

Write-Output 'MSBuild version 17.11.4+37f5ea0a1 for .NET'
Write-Output '  Determining projects to restore...'
Start-Sleep -Milliseconds $delay
foreach ($p in $projects) { Write-Output "  Restored C:\src\orbit\src\$p\$p.csproj (in $(Get-Random -Minimum 180 -Maximum 900) ms)." }
foreach ($p in $projects) {
    Start-Sleep -Milliseconds ($delay * 3)
    Write-Output "  $p -> C:\src\orbit\src\$p\bin\Debug\net8.0\$p.dll"
}
Write-Output ''
Write-Output 'Build succeeded.'
Write-Output '    0 Warning(s)'
Write-Output '    0 Error(s)'
Write-Output ''
Write-Output 'Test run for C:\src\orbit\src\Orbit.Tests\bin\Debug\net8.0\Orbit.Tests.dll (.NETCoreApp,Version=v8.0)'
Write-Output 'Starting test execution, please wait...'
$tests = 'Telemetry_sample_round_trips', 'Exporter_batches_by_size', 'Exporter_batches_by_age',
    'Retry_backs_off_exponentially', 'Retry_gives_up_after_five', 'Config_reads_environment',
    'Config_rejects_unknown_keys', 'Cli_prints_usage', 'Cli_export_since_one_hour', 'Clock_skew_is_tolerated'
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds $delay
    $t = $tests[$i % $tests.Count]
    Write-Output "  Passed Orbit.Tests.$t [$(Get-Random -Minimum 1 -Maximum 40) ms]"
}
Write-Output 'Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40'
