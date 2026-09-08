<#
.SYNOPSIS
    Runs a test suite several times over and names anything that did not pass every time.

.DESCRIPTION
    One green run proves nothing. A wall-clock assertion, a race between a worker and the thread watching
    it, or a test that depends on another having gone first will all sit green in the summary for weeks and
    then fail on a busy CI runner - so the way to find them is to run the suite until they show, rather
    than to wait for CI to do it in front of everybody.

    -Load starts that many CPU-burning background jobs first. That is the condition a hosted runner is
    actually in, and it is what turns "fails one run in fifty here" into "fails one run in eight" - which
    is the difference between a bug you can chase and one you cannot.

    IT IS CAPPED AT HALF THE MACHINE, and that cap is not a nicety. A number chosen on a 32-thread
    developer box means something else entirely on a 4-core runner: the nightly asked for 8 burners there,
    which is two per core, and the engine suite - ten seconds unloaded - did not finish ONE repeat in the
    two hours before the job timed out. Reproduced here at the same ratio, 64 burners on 32 processors:
    also unfinished after 300s. Past the point where the suite still gets a share of the machine you are
    not testing it under load, you are just not testing it.

.PARAMETER Suite
    core, app, ui, or all.

.PARAMETER Runs
    How many times to run it. Default 5.

.PARAMETER Load
    Background CPU burners to run alongside, capped at half the logical processors. Default 0.

.PARAMETER Coverage
    Collect coverage too, which slows execution and is itself a way to shake out timing assumptions.

.EXAMPLE
    pwsh -NoProfile -File scripts/Find-FlakyTests.ps1 -Suite core -Runs 20 -Load 24
#>
[CmdletBinding()]
param(
    [ValidateSet('core', 'app', 'ui', 'all')] [string] $Suite = 'all',
    [int] $Runs = 5,
    [int] $Load = 0,
    [switch] $Coverage,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repo 'artifacts/flake' }

# A test host left over from an earlier run holds the output assemblies open, MSBuild gives up on the copy
# with a warning rather than an error, and every run after that quietly exercises the PREVIOUS binary. That
# has produced a confident "the fix did not work" report before now, so the build is done here, once, and is
# fatal - never left to whatever happens to be on disk.
Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force
& dotnet build (Join-Path $repo 'Cascade.slnx') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE); the runs below would have used a stale binary." }

$projects = [ordered]@{
    core = 'tests/Cascade.Core.Tests/Cascade.Core.Tests.csproj'
    app  = 'tests/Cascade.AppTests/Cascade.AppTests.csproj'
    ui   = 'tests/Cascade.UiTests/Cascade.UiTests.csproj'
}
$chosen = if ($Suite -eq 'all') { $projects.Keys } else { @($Suite) }

if (Test-Path $ResultsDirectory) { Remove-Item $ResultsDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

# The UI suite drives a real window; keep it off the developer's desktop exactly as Run-UiTests.ps1 does.
$env:CASCADE_TEST_OFFSCREEN = '1'

$coverageTool = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-coverage.exe'
if ($Coverage -and -not (Test-Path $coverageTool)) {
    if (Get-Command dotnet-coverage -ErrorAction SilentlyContinue) { $coverageTool = 'dotnet-coverage' }
    else { throw 'dotnet-coverage is not installed. Run: dotnet tool install --global dotnet-coverage' }
}

$burners = @()
if ($Load -gt 0) {
    $room = [Math]::Max(1, [int]([Environment]::ProcessorCount / 2))
    if ($Load -gt $room) {
        Write-Host "Asked for $Load burners; this machine has $([Environment]::ProcessorCount) processors, so using $room." -ForegroundColor Yellow
        $Load = $room
    }
    Write-Host "Starting $Load background burners." -ForegroundColor Yellow
    $burners = 1..$Load | ForEach-Object { Start-Job { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalMinutes -lt 90) { } } }
}

try {
    foreach ($name in $chosen) {
        for ($run = 1; $run -le $Runs; $run++) {
            $log = Join-Path $ResultsDirectory "$name-$run.trx"
            $args = @(
                'test', (Join-Path $repo $projects[$name])
                '-c', $Configuration, '--no-build', '--nologo', '-v', 'q'
                '--logger', "trx;LogFileName=$name-$run.trx"
                '--results-directory', $ResultsDirectory
            )
            $started = Get-Date
            if ($Coverage) {
                # Not for the figure - nothing reads it. Instrumentation slows every suite by roughly a
                # third, which is the cheapest way to make this machine behave like a loaded CI runner,
                # and that is where the timing-sensitive checks give way first.
                & $coverageTool collect --settings (Join-Path $repo 'tests/coverage.runsettings') `
                    --output (Join-Path $ResultsDirectory "$name-$run.cobertura.xml") `
                    --output-format cobertura ('dotnet ' + ($args -join ' ')) | Out-Null
            }
            else {
                & dotnet @args | Out-Null
            }
            $seconds = ((Get-Date) - $started).TotalSeconds
            $verdict = if ($LASTEXITCODE -eq 0) { 'green' } else { 'RED' }
            Write-Host ("{0,-5} run {1,2}: {2,-5} {3,6:N1}s" -f $name, $run, $verdict, $seconds)
        }
    }
}
finally {
    if ($burners) { $burners | Stop-Job -PassThru | Remove-Job }
}

# Anything that was not "Passed" in every single run, with the first message it gave.
$outcomes = @{}
$messages = @{}
foreach ($trx in Get-ChildItem -Path $ResultsDirectory -Filter '*.trx') {
    [xml] $x = Get-Content -LiteralPath $trx.FullName
    foreach ($result in @($x.TestRun.Results.UnitTestResult)) {
        if (-not $result) { continue }
        $key = $result.testName
        if (-not $outcomes.ContainsKey($key)) { $outcomes[$key] = @{ Pass = 0; Fail = 0 } }
        if ($result.outcome -eq 'Passed') { $outcomes[$key].Pass++ }
        else {
            $outcomes[$key].Fail++
            if (-not $messages.ContainsKey($key)) { $messages[$key] = "$($result.outcome): $($result.Output.ErrorInfo.Message)" }
        }
    }
}

$unstable = $outcomes.Keys | Where-Object { $outcomes[$_].Fail -gt 0 } | Sort-Object
Write-Host ''
if (-not $unstable) {
    Write-Host ("{0} tests passed in every run." -f $outcomes.Count) -ForegroundColor Green
    exit 0
}

Write-Host "Not stable:" -ForegroundColor Red
foreach ($name in $unstable) {
    $o = $outcomes[$name]
    Write-Host ("  {0}  ({1} passed, {2} failed)" -f $name, $o.Pass, $o.Fail) -ForegroundColor Red
    ($messages[$name] -split "`r?`n" | Select-Object -First 4) | ForEach-Object { Write-Host "      $_" }
}
exit 1
