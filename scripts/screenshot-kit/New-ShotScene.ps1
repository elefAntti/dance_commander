<#
.SYNOPSIS
  Builds the neutral files the site's screenshots are taken on: C:\src\orbit, a small repository
  with git changes, build output, secrets and an archive, and C:\src\scratch beside it.

.DESCRIPTION
  Nothing here is anyone's real work, so a screenshot of it shows no names, paths or hosts. The
  folder is outside the user profile on purpose: an address bar reading C:\Users\<name>\... gives
  the name away.

  Refuses to touch C:\src\orbit unless this script made it (the .dc-shot-scene marker), so it can't
  overwrite a real checkout that happens to have the same name. Run it again to rebuild the scene.

  -BigFile adds a 16 GB file of zeros in C:\src\scratch\images, for a copy that runs long enough to
  close the window during it. It takes no time to make; remove it afterwards.
#>
param(
    [string] $Root = 'C:\src',
    [switch] $BigFile
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$orbit = Join-Path $Root 'orbit'
$scratch = Join-Path $Root 'scratch'
foreach ($dir in $orbit, $scratch) {
    if ((Test-Path $dir) -and -not (Test-Path (Join-Path $dir '.dc-shot-scene'))) {
        throw "$dir exists and wasn't made by this script; not touching it."
    }
}
foreach ($dir in $orbit, $scratch) {
    if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

function Put([string] $relative, [string] $text, [string] $under = $orbit) {
    $path = Join-Path $under $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

function Junk([string] $relative, [int] $bytes, [string] $under = $orbit) {
    $path = Join-Path $under $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $data = [byte[]]::new($bytes)
    [System.Random]::new($bytes).NextBytes($data)
    [System.IO.File]::WriteAllBytes($path, $data)
}

$commitDay = [DateTimeOffset]::Now.Date.AddDays(-12).AddHours(9)
$commitCount = 0
function Commit([string] $message) {
    $script:commitCount++
    $when = $commitDay.AddDays($commitCount).AddMinutes((Get-Random -Minimum 0 -Maximum 400)).ToString('yyyy-MM-ddTHH:mm:ss', [cultureinfo]::InvariantCulture)
    $env:GIT_AUTHOR_DATE = $when; $env:GIT_COMMITTER_DATE = $when
    git -C $orbit add -A | Out-Null
    git -C $orbit commit -q -m $message
    Remove-Item Env:GIT_AUTHOR_DATE, Env:GIT_COMMITTER_DATE
}

New-Item -ItemType Directory -Force -Path $orbit, $scratch | Out-Null
git -C $orbit init -q -b main
git -C $orbit config user.name 'Mira Example'
git -C $orbit config user.email 'mira@example.com'
git -C $orbit config core.autocrlf false
git -C $orbit remote add origin 'https://gitlab.example.com/space/orbit.git'

Put '.gitignore' "bin/`nobj/`n*.user`n/release/`n/data/`ndeploy/secrets.env`n*.secrets.json`n*.pfx`n*.dmp`nsrc/**/*.zip`n.dc-shot-scene`n"
Put 'README.md' "# Orbit`n`nCollects telemetry from the fleet and exports it in batches.`n`n    orbit export --since 1h`n"
Put 'LICENSE' "MIT License`n`nCopyright (c) 2026 Orbit contributors`n"
Put 'Orbit.sln' "Microsoft Visual Studio Solution File, Format Version 12.00`n# Visual Studio Version 17`n"
Put 'Directory.Build.props' "<Project>`n  <PropertyGroup>`n    <TargetFramework>net8.0</TargetFramework>`n    <Nullable>enable</Nullable>`n  </PropertyGroup>`n</Project>`n"
foreach ($p in 'Orbit.Core', 'Orbit.Export', 'Orbit.Cli') {
    Put "src\$p\$p.csproj" "<Project Sdk=""Microsoft.NET.Sdk"">`n</Project>`n"
}
Put 'src\Orbit.Core\Sample.cs' "namespace Orbit.Core;`n`npublic sealed record Sample(string Source, DateTimeOffset At, double Value);`n"
Put 'src\Orbit.Core\Telemetry.cs' "namespace Orbit.Core;`n`npublic sealed class Telemetry`n{`n    private readonly List<Sample> _samples = new();`n`n    public void Add(Sample sample) => _samples.Add(sample);`n}`n"
Put 'src\Orbit.Export\RetryPolicy.cs' "namespace Orbit.Export;`n`npublic sealed class RetryPolicy`n{`n    public TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));`n}`n"
Put 'src\Orbit.Cli\Program.cs' "using Orbit.Core;`n`nConsole.WriteLine(""orbit 1.3"");`n"
Put 'tests\Orbit.Tests\TelemetryTests.cs' "namespace Orbit.Tests;`n`npublic class TelemetryTests { }`n"
Put 'docs\design.md' "# Design`n`nSamples are kept in memory until a batch is full or a minute old.`n"
Put 'docs\install.md' "# Install`n`nUnzip the release and put orbit.exe on your PATH.`n"
Put 'docs\monitor-design.md' "# Monitor design`n"
Put 'docs\monthly-dashboard.md' "# Monthly dashboard`n"
Put 'docs\monitoring-data.csv' "source,at,value`nunit-7,2026-10-01T09:00:00Z,0.42`n"
Put 'docs\mode-diagram.svg' "<svg xmlns=""http://www.w3.org/2000/svg""/>`n"
Put 'docs\release-notes.md' "# Release notes`n`n## 1.3.0`n`n- Retries back off.`n"
Commit 'Start the solution'

# Tabs for indentation and alignment, and line endings that changed hands halfway: the preview's
# header counts both kinds.
$changelog = "Orbit changelog`r`n===============`r`n`r`n1.3.0`t2026-09-28`r`n`t-`tRetries back off exponentially`r`n`t-`tConfig reads ORBIT_* variables`r`n`r`n" +
    "1.2.0`t2026-08-14`n`t-`tExport in batches of 500`n`t-`tFixed clock skew between units`n`n" +
    "1.1.0`t2026-07-02`n`t-`tFirst release on the new runner`n"
$path = Join-Path $orbit 'notes\changelog.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
[System.IO.File]::WriteAllText($path, $changelog, [System.Text.UTF8Encoding]::new($false))
Put 'notes\notes.txt' "Ask about the retention period for raw samples.`nRunner 2 needs a bigger disk.`n"
Commit 'Keep a changelog'

Put 'src\Orbit.Export\RetryPolicy.cs' "namespace Orbit.Export;`n`npublic sealed class RetryPolicy`n{`n    public int Attempts { get; init; } = 5;`n`n    public TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));`n}`n"
Commit 'Give up after five attempts'
Put 'deploy\deploy.ps1' "param([string] `$Environment = 'staging')`nWrite-Output ""Deploying to `$Environment""`n"
Put 'deploy\staging.json' "{ ""endpoint"": ""https://staging.example.com"" }`n"
Put 'config\credentials.example.json' "{ ""user"": """", ""token"": """" }`n"
Commit 'Add a deploy script'
Put 'docs\install.md' "# Install`n`nUnzip the release and put orbit.exe on your PATH.`nOn Linux, use the tarball.`n"
Commit 'Document the Linux install'

git -C $orbit switch -q -c feature/telemetry
Put 'src\Orbit.Core\Telemetry.cs' "namespace Orbit.Core;`n`npublic sealed class Telemetry`n{`n    private readonly List<Sample> _samples = new();`n`n    public event Action<Sample>? Added;`n`n    public void Add(Sample sample) { _samples.Add(sample); Added?.Invoke(sample); }`n}`n"
Commit 'Raise an event per sample'
Put 'src\Orbit.Export\Batcher.cs' "namespace Orbit.Export;`n`npublic sealed class Batcher { }`n"
Commit 'Batch samples by size and age'

# The working tree's own changes: modified, added (staged), untracked; the folders above them roll
# the letters up.
Put 'src\Orbit.Cli\Program.cs' "using Orbit.Core;`nusing Orbit.Export;`n`nConsole.WriteLine(""orbit 1.4"");`n"
Put 'docs\design.md' "# Design`n`nSamples are kept in memory until a batch is full or a minute old.`nExports go to OpenTelemetry.`n"
Put 'src\Orbit.Export\OtlpExporter.cs' "namespace Orbit.Export;`n`npublic sealed class OtlpExporter { }`n"
git -C $orbit add 'src/Orbit.Export/OtlpExporter.cs'
Put 'notes\todo.md' "- [ ] Retention period`n- [ ] Runner disk`n"

# What the highlighting rules have to show: build output, secrets, an archive, a big file.
Junk 'src\Orbit.Core\bin\Debug\net8.0\Orbit.Core.dll' 48000
Junk 'src\Orbit.Core\obj\project.assets.json' 9000
Junk 'src\Orbit.Cli\bin\Debug\net8.0\orbit.dll' 21000
Put 'deploy\secrets.env' "ORBIT_TOKEN=not-a-real-token`n"
# src\Orbit.Cli is where every example rule shows at once: the highlighting shot.
Junk 'src\Orbit.Cli\obj\project.assets.json' 7000
Put 'src\Orbit.Cli\appsettings.json' "{ ""Export"": { ""BatchSize"": 500 } }`n"
Put 'src\Orbit.Cli\appsettings.secrets.json' "{ ""Token"": ""not-a-real-token"" }`n"
Junk 'src\Orbit.Cli\signing.pfx' 2600
fsutil file createnew (Join-Path $orbit 'src\Orbit.Cli\crash-2026-10-24.dmp') 125829120 | Out-Null
Junk 'data\telemetry-dump.bin' 1000
fsutil file createnew (Join-Path $orbit 'data\samples-2026-10.bin') 157286400 | Out-Null

# A release archive with something inside, to step into.
$release = Join-Path $orbit 'release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "orbit-1.3.0-win-x64"
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
Junk 'orbit.exe' 140000 $staging
Junk 'Orbit.Core.dll' 48000 $staging
Put 'orbit.json' "{ ""endpoint"": ""https://telemetry.example.com"" }`n" $staging
Put 'README.md' "# Orbit 1.3.0`n" $staging
Compress-Archive -Path $staging -DestinationPath (Join-Path $release 'orbit-1.3.0-win-x64.zip')
Remove-Item -LiteralPath $staging -Recurse -Force
Copy-Item (Join-Path $release 'orbit-1.3.0-win-x64.zip') (Join-Path $orbit 'src\Orbit.Cli\orbit-1.4.0-preview.zip')

# Scratch: somewhere to copy to, and a folder for a terminal to stand in.
Put 'old-build\build.log' "Build succeeded.`n" $scratch
Put 'old-build\orbit.dll' "MZ" $scratch
Put 'inbox\orbit-1.3.0-win-x64.zip' "" $scratch
Copy-Item (Join-Path $release 'orbit-1.3.0-win-x64.zip') (Join-Path $scratch 'inbox\orbit-1.3.0-win-x64.zip') -Force
Put 'inbox\README.md' "# Orbit`n" $scratch
if ($BigFile) {
    New-Item -ItemType Directory -Force -Path (Join-Path $scratch 'images') | Out-Null
    fsutil file createnew (Join-Path $scratch 'images\orbit-test-vm.vhdx') 17179869184 | Out-Null
}

# Made a second ago, every file would be "today" (bold, sorted first by the example rules) and every
# Modified column would read the same minute. Spread them over the last two weeks instead, with only
# the working tree's own changes from today.
$random = [System.Random]::new(7)
$today = 'src\Orbit.Cli\Program.cs', 'docs\design.md', 'src\Orbit.Export\OtlpExporter.cs', 'notes\todo.md'
foreach ($dir in $orbit, $scratch) {
    Get-ChildItem $dir -Recurse -Force | Where-Object { $_.FullName -notmatch '\\\.git(\\|$)' } |
        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
            $relative = $_.FullName.Substring($dir.Length + 1)
            $_.LastWriteTime = if ($dir -eq $orbit -and $today -contains $relative) {
                [DateTime]::Now.AddMinutes(-$random.Next(20, 300))
            } else {
                [DateTime]::Now.Date.AddDays(-$random.Next(2, 14)).AddMinutes($random.Next(480, 1080))
            }
        }
}

foreach ($dir in $orbit, $scratch) { Put '.dc-shot-scene' "Made by scripts/screenshot-kit/New-ShotScene.ps1`n" $dir }
Write-Output "Scene ready: $orbit (branch feature/telemetry), $scratch"
git -C $orbit status --short
