<#
.SYNOPSIS
    Runs every Cascade test suite, in the order that makes a failure name its own cause, and reports what
    each one cost.

.DESCRIPTION
    Three suites, cheapest and most specific first:

      core  the engine. No window, no desktop, fully parallel - about seven seconds for 800 tests.
      app   the WinForms half: real controls, dialogs and windows, built on a hidden STA thread in process.
      ui    the shipped executable, driven through UI Automation. One process launch per test.

    With -Coverage a fourth line appears, `exe`: the handful of UI checks that open the published bundle,
    which the measured UI run cannot (see below).

    The last two build real windows. They are kept off the visible desktop (CASCADE_TEST_OFFSCREEN parks the
    app beyond the last monitor, and the app checks show theirs at zero opacity out there too), so nothing
    appears over what you are doing and a stray click cannot reach them.

    THE UI SUITE STILL TAKES THE KEYBOARD - it drives the real executable through UI Automation, which is
    only dependable on a desktop that is in front - so typing during that one will lose keystrokes. The
    other two leave it alone. MEASURED with scripts/Measure-Focus.ps1: core 0%, app 0.2%, ui 82% of the run.
    tests/Cascade.AppTests/Infrastructure/Sta.cs records how the app checks got there and what else was
    tried on the way.

.PARAMETER Suite
    core, app, ui, or all. Default all.

.PARAMETER Publish
    Publish the single-file exe first, so the UI suite drives the code as it currently stands. Without it
    the previously published exe is reused, which is a good way to test a change that is not there.

.PARAMETER Coverage
    Collect coverage from every suite - including the UI one - and print the merged figure. Needs
    dotnet-coverage (dotnet tool install --global dotnet-coverage).

.EXAMPLE
    pwsh -NoProfile -File scripts/Run-Tests.ps1 -Publish

.EXAMPLE
    pwsh -NoProfile -File scripts/Run-Tests.ps1 -Suite app -Filter 'FullyQualifiedName~FindBar'
#>
[CmdletBinding()]
param(
    [ValidateSet('core', 'app', 'ui', 'all')] [string] $Suite = 'all',
    [string] $Filter,
    [switch] $Publish,
    [switch] $Coverage,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [int] $TimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$projects = [ordered]@{
    core = 'tests/Cascade.Core.Tests/Cascade.Core.Tests.csproj'
    app  = 'tests/Cascade.AppTests/Cascade.AppTests.csproj'
    ui   = 'tests/Cascade.UiTests/Cascade.UiTests.csproj'
}
$chosen = if ($Suite -eq 'all') { @($projects.Keys) } else { @($Suite) }

if ($Publish) {
    Write-Host 'Publishing...' -ForegroundColor Cyan
    & dotnet publish (Join-Path $repo 'src\Cascade.App\Cascade.App.csproj') `
        -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=embedded `
        -o (Join-Path $repo 'artifacts\publish') -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
}

# A test host left over from an earlier run holds the output assemblies open, MSBuild gives up on the copy
# with a warning rather than an error, and the run below then exercises the PREVIOUS binary while looking
# perfectly healthy. Build here, once, and make it fatal.
Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force
& dotnet build (Join-Path $repo 'Cascade.slnx') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

$published = Join-Path $repo 'artifacts\publish\Cascade.exe'
if (Test-Path $published) {
    $env:CASCADE_TEST_EXE = $published
    Write-Host "UI suite will drive $published" -ForegroundColor DarkGray
}
$env:CASCADE_TEST_OFFSCREEN = '1'

# The UI suite launches the app as a separate process, so it can only be measured by a collector that
# follows children - and the published exe is a SINGLE-FILE bundle, whose assemblies are loaded from the
# bundle rather than from files and cannot be instrumented at all (measured: the run reports the test
# assembly and nothing else). Under -Coverage it therefore drives the ordinary build output, which is the
# same IL differently packaged. What that gives up is the bundle itself, so -Coverage runs the handful of
# checks that exercise the bundle against the published exe afterwards.
$binExe = Join-Path $repo "src\Cascade.App\bin\$Configuration\net10.0-windows\Cascade.exe"
$bundleChecks = 'FullyQualifiedName~ScreenshotHarnessTests|FullyQualifiedName~FixtureSmoke|FullyQualifiedName~AutomationTests'

$coverageTool = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-coverage.exe'
if ($Coverage -and -not (Test-Path $coverageTool)) {
    if (Get-Command dotnet-coverage -ErrorAction SilentlyContinue) { $coverageTool = 'dotnet-coverage' }
    else { throw 'dotnet-coverage is not installed. Run: dotnet tool install --global dotnet-coverage' }
}

$results = Join-Path $repo 'artifacts/test-results'
if (Test-Path $results) { Remove-Item $results -Recurse -Force }

# Runs one command, kills it if it wedges, and reports how long it took.
function Invoke-Timed($exe, $arguments) {
    $started = Get-Date
    $run = Start-Process $exe -WorkingDirectory $repo -ArgumentList $arguments -NoNewWindow -PassThru
    if (-not $run.WaitForExit($TimeoutMinutes * 60 * 1000)) { $run.Kill($true); throw 'A suite timed out.' }
    [pscustomobject]@{ Code = $run.ExitCode; Seconds = ((Get-Date) - $started).TotalSeconds }
}

$failed = @()
foreach ($name in $chosen) {
    # A quiet run says only that a suite went red, and a suite here is a hundred checks. The trx carries the
    # message of every failed one, which is the difference between diagnosing a rare failure and re-running
    # until it happens again.
    $testArgs = @('test', $projects[$name], '-c', $Configuration, '--no-build', '--nologo', '-v', 'q',
                  '--logger', """trx;LogFileName=$name.trx""",
                  '--results-directory', 'artifacts/test-results')
    if ($Filter) { $testArgs += @('--filter', """$Filter""") }

    if ($Coverage) {
        if ($name -eq 'ui') { $env:CASCADE_TEST_EXE = $binExe }
        $outcome = Invoke-Timed $coverageTool @(
            'collect', '--settings', (Join-Path $repo 'tests/coverage.runsettings'),
            '--output', (Join-Path $results "$name.cobertura.xml"),
            '--output-format', 'cobertura', ('dotnet ' + ($testArgs -join ' ')))
        if ($name -eq 'ui' -and (Test-Path $published)) { $env:CASCADE_TEST_EXE = $published }
    }
    else {
        $outcome = Invoke-Timed 'dotnet' $testArgs
    }
    $seconds = $outcome.Seconds

    $verdict = if ($outcome.Code -eq 0) { 'green' } else { 'RED' }
    $colour = if ($outcome.Code -eq 0) { 'Green' } else { 'Red' }
    Write-Host ("{0,-5} {1,-5} {2,6:N1}s" -f $name, $verdict, $seconds) -ForegroundColor $colour
    if ($outcome.Code -ne 0) {
        $failed += $name
        $trx = Join-Path $results "$name.trx"
        if (Test-Path $trx) {
            $xml = [xml](Get-Content $trx -Raw)
            foreach ($result in $xml.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' }) {
                Write-Host ("      {0}" -f $result.testName) -ForegroundColor Red
                foreach ($line in ($result.Output.ErrorInfo.Message -split "`r?`n")) {
                    if ($line.Trim()) { Write-Host ("        {0}" -f $line.Trim()) -ForegroundColor DarkRed }
                }
            }
            Write-Host "      full output: $trx" -ForegroundColor DarkGray
        }
    }
}

# The coverage run drove the build output, so nothing has yet opened the bundle that ships.
if ($Coverage -and $chosen -contains 'ui' -and (Test-Path $published) -and -not $Filter) {
    $outcome = Invoke-Timed 'dotnet' @('test', $projects['ui'], '-c', $Configuration, '--no-build',
                                       '--nologo', '-v', 'q', '--filter', """$bundleChecks""")
    $verdict = if ($outcome.Code -eq 0) { 'green' } else { 'RED' }
    Write-Host ("{0,-5} {1,-5} {2,6:N1}s" -f 'exe', $verdict, $outcome.Seconds) `
               -ForegroundColor $(if ($outcome.Code -eq 0) { 'Green' } else { 'Red' })
    if ($outcome.Code -ne 0) { $failed += 'exe' }
}

if ($Coverage) { & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Report-Coverage.ps1') -ResultsDirectory $results }

if ($failed) { throw "Failed: $($failed -join ', ')" }
Write-Host 'All green.' -ForegroundColor Green
