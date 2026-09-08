<#
.SYNOPSIS
    Runs every Cascade test suite, in the order that makes a failure name its own cause, and reports what
    each one cost.

.DESCRIPTION
    Three suites, cheapest and most specific first:

      core  the engine. No window, no desktop, fully parallel - about seven seconds for 800 tests.
      app   the WinForms half: real controls, dialogs and windows, built on a hidden STA thread in process.
      ui    the shipped executable, driven through UI Automation. One process launch per test.

    The last two build real windows. They are kept off the visible desktop (CASCADE_TEST_OFFSCREEN parks the
    app beyond the last monitor, and the app checks show theirs at zero opacity out there too), so nothing
    appears over what you are doing and a stray click cannot reach them.

    THEY DO STILL TAKE THE KEYBOARD, though - showing a window activates it - so typing during a run will
    lose keystrokes. tests/Cascade.AppTests/Infrastructure/Sta.cs records the three ways that were measured
    and what each of them came to.

.PARAMETER Suite
    core, app, ui, or all. Default all.

.PARAMETER Publish
    Publish the single-file exe first, so the UI suite drives the code as it currently stands. Without it
    the previously published exe is reused, which is a good way to test a change that is not there.

.PARAMETER Coverage
    Collect coverage from the two suites that can be instrumented, and print the merged figure.

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

$results = Join-Path $repo 'artifacts/test-results'
if ($Coverage -and (Test-Path $results)) { Remove-Item $results -Recurse -Force }

$failed = @()
foreach ($name in $chosen) {
    $arguments = @('test', $projects[$name], '-c', $Configuration, '--no-build', '--nologo', '-v', 'q')
    if ($Filter) { $arguments += @('--filter', $Filter) }
    # The UI suite launches the executable, so instrumenting this process would measure nothing of it.
    # Naming the runsettings is enough: a data collector declared there is enabled by it, and passing
    # --collect as well only risks the two disagreeing about what is being measured.
    if ($Coverage -and $name -ne 'ui') {
        $arguments += @('--settings', 'tests/coverage.runsettings',
                        '--results-directory', 'artifacts/test-results')
    }

    $started = Get-Date
    $run = Start-Process dotnet -WorkingDirectory $repo -ArgumentList $arguments -NoNewWindow -PassThru
    if (-not $run.WaitForExit($TimeoutMinutes * 60 * 1000)) { $run.Kill($true); throw "$name timed out." }
    $seconds = ((Get-Date) - $started).TotalSeconds

    $verdict = if ($run.ExitCode -eq 0) { 'green' } else { 'RED' }
    $colour = if ($run.ExitCode -eq 0) { 'Green' } else { 'Red' }
    Write-Host ("{0,-5} {1,-5} {2,6:N1}s" -f $name, $verdict, $seconds) -ForegroundColor $colour
    if ($run.ExitCode -ne 0) { $failed += $name }
}

if ($Coverage) { & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Report-Coverage.ps1') -ResultsDirectory $results }

if ($failed) { throw "Failed: $($failed -join ', ')" }
Write-Host 'All green.' -ForegroundColor Green
