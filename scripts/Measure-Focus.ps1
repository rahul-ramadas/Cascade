<#
.SYNOPSIS
    Measures how much of a test run holds the desktop foreground - i.e. how much of it would eat the
    keystrokes you meant for something else.

.DESCRIPTION
    The suites are already kept out of SIGHT: the app is parked beyond the last monitor by
    CASCADE_TEST_OFFSCREEN, and the app checks show their windows at zero opacity out there too, so nothing
    appears over your work and no stray click can reach anything. What none of that stops is ACTIVATION -
    showing a window makes it the foreground one - and the only way to know how much that costs is to
    sample it.

    Samples GetForegroundWindow every 50ms for the length of a run and reports who had it, by process.
    Anything owned by testhost (the app checks build their windows in it) or by Cascade (the UI suite
    launches the real executable) is time the tests had your keyboard.

    MEASURED 2026-09-08 on this machine:
        core    0.0%   builds no window at all
        app    86.2%   testhost, about a hundred invisible forms, each activating as it is shown
        ui     82.4%   Cascade, one launch per test (and 2.8% more from the self-update tests' copy)

    On a CI runner none of this matters - nobody is typing there. It matters for a local run, and
    tests/Cascade.AppTests/Infrastructure/Sta.cs records the three remedies that were tried for the app
    checks and what each of them came to.

.EXAMPLE
    pwsh -NoProfile -File scripts/Measure-Focus.ps1 app
#>
[CmdletBinding()]
param(
    [ValidateSet('core', 'app', 'ui')] [string] $Suite = 'app',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Foreground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
}
'@

$project = switch ($Suite) {
    'core' { 'tests/Cascade.Core.Tests/Cascade.Core.Tests.csproj' }
    'app'  { 'tests/Cascade.AppTests/Cascade.AppTests.csproj' }
    'ui'   { 'tests/Cascade.UiTests/Cascade.UiTests.csproj' }
}

$env:CASCADE_TEST_OFFSCREEN = '1'
$published = Join-Path $repo 'artifacts\publish\Cascade.exe'
if (Test-Path $published) { $env:CASCADE_TEST_EXE = $published }

$run = Start-Process dotnet -WorkingDirectory $repo -NoNewWindow -PassThru -ArgumentList @(
    'test', $project, '-c', $Configuration, '--no-build', '--nologo', '-v', 'q'
)

$samples = 0
$byProcess = @{}
while (-not $run.HasExited) {
    Start-Sleep -Milliseconds 50
    $samples++
    $owner = 0
    [void][Foreground]::GetWindowThreadProcessId([Foreground]::GetForegroundWindow(), [ref]$owner)
    $name = try { (Get-Process -Id $owner -ErrorAction Stop).ProcessName } catch { "pid $owner" }
    $byProcess[$name] = 1 + $(if ($byProcess.ContainsKey($name)) { $byProcess[$name] } else { 0 })
}

# testhost is where the app checks build their windows; Cascade is what the UI suite launches.
$theirs = 0
foreach ($name in $byProcess.Keys) {
    if ($name -eq 'testhost' -or $name -like 'Cascade*') { $theirs += $byProcess[$name] }
}

Write-Host ''
Write-Host ("{0}: exit {1}, {2} samples" -f $Suite, $run.ExitCode, $samples)
Write-Host ("the tests held the foreground for {0:P1} of the run" -f $(if ($samples) { $theirs / $samples } else { 0 }))
$byProcess.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("  {0,6:P1}  {1}" -f ($_.Value / $samples), $_.Key)
}
