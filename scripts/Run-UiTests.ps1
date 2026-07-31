<#
.SYNOPSIS
    Runs the Cascade UI test suite with the app parked off-screen, so it never appears on top of what you
    are doing and cannot catch a stray click.

.DESCRIPTION
    The suite drives Cascade through UI Automation and messages posted straight to its windows, so it never
    needs the foreground - but the window itself was still arriving maximised, over everything. Setting
    CASCADE_TEST_OFFSCREEN puts it beyond the last monitor instead: out of sight, out of reach of the
    mouse, and still on the real desktop, where UI Automation is quick and reliable.

    Giving the run a Windows desktop of its own hides it just as well and was tried first, but measured
    8m03s with 9 failures against 1m27s green on the ordinary desktop, on the same machine minutes apart -
    automation on a desktop that never has the input focus waits out its transaction timeouts. Hence this.

.PARAMETER Filter
    Passed to dotnet test --filter, e.g. 'FullyQualifiedName~UiFeatureTests'.

.PARAMETER Exe
    The Cascade.exe to test (sets CASCADE_TEST_EXE). Defaults to artifacts\publish\Cascade.exe when it
    exists, which is the shape CI tests and therefore the one worth trusting.

.PARAMETER Publish
    Publish the app first, so the run tests the code as it currently stands.

.EXAMPLE
    pwsh -NoProfile -File scripts\Run-UiTests.ps1 -Publish

.EXAMPLE
    pwsh -NoProfile -File scripts\Run-UiTests.ps1 -Filter 'FullyQualifiedName~Find'
#>
[CmdletBinding()]
param(
    [string] $Filter,
    [string] $Exe,
    [switch] $Publish,
    [ValidateSet('q', 'm', 'n', 'd')] [string] $Verbosity = 'q',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [int] $TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ($Publish) {
    Write-Host 'Publishing...' -ForegroundColor Cyan
    & dotnet publish (Join-Path $repo 'src\Cascade.App\Cascade.App.csproj') `
        -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=embedded `
        -o (Join-Path $repo 'artifacts\publish') -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
}

if (-not $Exe) {
    $published = Join-Path $repo 'artifacts\publish\Cascade.exe'
    if (Test-Path $published) { $Exe = $published }
}
if ($Exe) {
    if (-not (Test-Path $Exe)) { throw "No such exe: $Exe" }
    $env:CASCADE_TEST_EXE = (Resolve-Path $Exe).Path
    Write-Host "Testing $env:CASCADE_TEST_EXE" -ForegroundColor Cyan
}

# Inherited all the way down - dotnet test to the test host to each Cascade.exe it launches - so the
# harness itself needs to know nothing about this.
$env:CASCADE_TEST_OFFSCREEN = '1'

$testArgs = @(
    'test', (Join-Path $repo 'tests\Cascade.UiTests\Cascade.UiTests.csproj')
    '-c', $Configuration, '--nologo', '-v', $Verbosity
)
if ($Filter) { $testArgs += @('--filter', $Filter) }

Write-Host 'Running the UI suite off-screen - nothing will appear on your desktop.' -ForegroundColor Cyan
$started = Get-Date
# Started rather than called so a wedged UI test cannot hang the session for ever.
$run = Start-Process dotnet -ArgumentList $testArgs -NoNewWindow -PassThru
if (-not $run.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    $run.Kill()
    Write-Host ("Timed out after {0:n0} minutes." -f $TimeoutMinutes) -ForegroundColor Red
    exit 1
}

$elapsed = (Get-Date) - $started
if ($run.ExitCode -ne 0) { Write-Host ("Tests failed ({0}) in {1:mm\:ss}." -f $run.ExitCode, $elapsed) -ForegroundColor Red }
else { Write-Host ("Tests passed in {0:mm\:ss}." -f $elapsed) -ForegroundColor Green }
exit $run.ExitCode
