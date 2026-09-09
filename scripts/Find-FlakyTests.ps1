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

.PARAMETER TimeoutMinutes
    How long one run may take before it is treated as wedged. Past that, scripts/Invoke-WithHangDump.ps1
    takes the managed stacks and a dump of everything under it, prints the stacks, kills it and carries on
    with the next run. A repeat that never finishes used to take the whole step's budget with it and leave
    nothing to look at - which is how a nightly spent eighty-eight minutes saying nothing at all.

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
    [string] $ResultsDirectory,
    [double] $TimeoutMinutes = 20
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

# Where a run that never finished leaves its stacks and dumps. Beside the results, not among them: the
# summary below reads every .trx it finds, and a wedged run does not write one.
$watchdog = Join-Path $PSScriptRoot 'Invoke-WithHangDump.ps1'
$stuck = Join-Path $ResultsDirectory 'stuck'

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
        # Only the UI suite: it drives a real window, and this keeps it off the developer's desktop exactly
        # as Run-UiTests.ps1 does. Setting it for the others repeats them in a configuration nothing else
        # runs - the app checks read it too, and it makes their window 1600px wide whatever size a check
        # asked for, which is how a check that assumed maximising widens the window failed here and nowhere
        # else. A flake hunt has to repeat what CI runs, or the flakes it finds are its own.
        $env:CASCADE_TEST_OFFSCREEN = if ($name -eq 'ui') { '1' } else { $null }

        for ($run = 1; $run -le $Runs; $run++) {
            $log = Join-Path $ResultsDirectory "$name-$run.trx"
            # --blame-hang answers the case this hunt is most likely to meet: one repeat in twenty where a
            # test never returns. It names that test and dumps the host from the inside, which the watchdog
            # below cannot do - and the watchdog covers what blame cannot, which is everything that goes
            # wrong before there is a test host to arm it.
            $args = @(
                'test', (Join-Path $repo $projects[$name])
                '-c', $Configuration, '--no-build', '--nologo', '-v', 'q'
                '--logger', "trx;LogFileName=$name-$run.trx"
                '--results-directory', $ResultsDirectory
                '--blame-hang', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini'
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
                & $watchdog -Name "$name-$run" -TimeoutMinutes $TimeoutMinutes `
                            -ReportDirectory $stuck -Quiet -Command (@('dotnet') + $args)
            }
            $seconds = ((Get-Date) - $started).TotalSeconds
            $verdict = if ($LASTEXITCODE -eq 0) { 'green' } else { 'RED' }
            Write-Host ("{0,-5} run {1,2}: {2,-5} {3,6:N1}s" -f $name, $run, $verdict, $seconds)

            # A wedge has already been diagnosed by the time we get here - stacks printed, dump written -
            # and the repeats after it would each cost another whole timeout for nothing. Stop this suite
            # and let the next one have the step's remaining budget, which is what the wedge was taking.
            if ($LASTEXITCODE -eq 124) {
                Write-Host ("{0,-5} not repeated further: run {1} never finished." -f $name, $run) -ForegroundColor Red
                break
            }
        }
    }
}
finally {
    if ($burners) { $burners | Stop-Job -PassThru | Remove-Job }
}

# Anything that was not "Passed" in every single run, with the first message it gave.
# A SKIP IS NOT AN INSTABILITY. A test that decides at run time it has nothing to run against reports
# NotExecuted, which is not "Passed" - and counting that as a failure made this report every run red for a
# test that was behaving exactly as intended. Skips are counted and named separately, because a test
# skipped in every run is worth seeing; it is just not a flake.
$outcomes = @{}
$messages = @{}
foreach ($trx in Get-ChildItem -Path $ResultsDirectory -Filter '*.trx') {
    [xml] $x = Get-Content -LiteralPath $trx.FullName
    foreach ($result in @($x.TestRun.Results.UnitTestResult)) {
        if (-not $result) { continue }
        $key = $result.testName
        if (-not $outcomes.ContainsKey($key)) { $outcomes[$key] = @{ Pass = 0; Fail = 0; Skip = 0 } }
        if ($result.outcome -eq 'Passed') { $outcomes[$key].Pass++ }
        elseif ($result.outcome -eq 'NotExecuted') { $outcomes[$key].Skip++ }
        else {
            $outcomes[$key].Fail++
            if (-not $messages.ContainsKey($key)) { $messages[$key] = "$($result.outcome): $($result.Output.ErrorInfo.Message)" }
        }
    }
}

$unstable = $outcomes.Keys | Where-Object { $outcomes[$_].Fail -gt 0 } | Sort-Object
$skipped = $outcomes.Keys | Where-Object { $outcomes[$_].Skip -gt 0 -and $outcomes[$_].Fail -eq 0 } | Sort-Object
Write-Host ''
foreach ($name in $skipped) {
    Write-Host ("  skipped in {0} of {1} runs: {2}" -f $outcomes[$name].Skip,
                ($outcomes[$name].Skip + $outcomes[$name].Pass), $name) -ForegroundColor Yellow
}

# A run that wedged writes no trx at all, so the tally above cannot see it: every test in it is simply
# absent, and absent reads as "passed in every run it appeared in". Say so, loudly, and go red - a hunt for
# instability that reports green because one repeat never came back is worse than no hunt.
$wedged = @(Get-ChildItem -Path $stuck -Filter '*-hang.txt' -ErrorAction SilentlyContinue)
foreach ($report in $wedged) {
    Write-Host ("  never finished: {0}" -f $report.BaseName.Replace('-hang', '')) -ForegroundColor Red
    Write-Host ("      stacks and dumps beside {0}" -f $report.FullName) -ForegroundColor DarkRed
}

if (-not $unstable -and -not $wedged) {
    Write-Host ("{0} tests passed in every run." -f ($outcomes.Count - $skipped.Count)) -ForegroundColor Green
    exit 0
}
if (-not $unstable) { exit 1 }

Write-Host "Not stable:" -ForegroundColor Red
foreach ($name in $unstable) {
    $o = $outcomes[$name]
    Write-Host ("  {0}  ({1} passed, {2} failed)" -f $name, $o.Pass, $o.Fail) -ForegroundColor Red
    ($messages[$name] -split "`r?`n" | Select-Object -First 4) | ForEach-Object { Write-Host "      $_" }
}
exit 1
