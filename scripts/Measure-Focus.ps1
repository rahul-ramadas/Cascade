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
        app     0.2%   was 86% before the app checks were taught to give the desktop back
        ui     82.4%   Cascade, one launch per test (and 2.8% more from the self-update tests' copy)

    On a CI runner none of this matters - nobody is typing there. It matters for a local run, and
    tests/Cascade.AppTests/Infrastructure/Sta.cs records what was tried for the app checks, what each
    remedy came to, and why the UI suite is left as it is.

.EXAMPLE
    pwsh -NoProfile -File scripts/Measure-Focus.ps1 app
#>
[CmdletBinding()]
param(
    [ValidateSet('core', 'app', 'ui')] [string] $Suite = 'app',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $Filter
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Foreground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
}
'@

$project = switch ($Suite) {
    'core' { 'tests/Cascade.Core.Tests/Cascade.Core.Tests.csproj' }
    'app'  { 'tests/Cascade.AppTests/Cascade.AppTests.csproj' }
    'ui'   { 'tests/Cascade.UiTests/Cascade.UiTests.csproj' }
}

# The UI suite launches the real executable and needs it parked off the desktop. The app checks place
# their own windows, so measuring them with this set would measure something nobody runs.
$env:CASCADE_TEST_OFFSCREEN = if ($Suite -eq 'ui') { '1' } else { $null }
$published = Join-Path $repo 'artifacts\publish\Cascade.exe'
if (Test-Path $published) { $env:CASCADE_TEST_EXE = $published }

$arguments = @('test', $project, '-c', $Configuration, '--no-build', '--nologo', '-v', 'q')
if ($Filter) { $arguments += @('--filter', $Filter) }
$run = Start-Process dotnet -WorkingDirectory $repo -NoNewWindow -PassThru -ArgumentList $arguments

$samples = 0
$byProcess = @{}
$byWindow = @{}
while (-not $run.HasExited) {
    Start-Sleep -Milliseconds 50
    $samples++
    $window = [Foreground]::GetForegroundWindow()
    $owner = 0
    [void][Foreground]::GetWindowThreadProcessId($window, [ref]$owner)
    $name = try { (Get-Process -Id $owner -ErrorAction Stop).ProcessName } catch { "pid $owner" }
    $byProcess[$name] = 1 + $(if ($byProcess.ContainsKey($name)) { $byProcess[$name] } else { 0 })

    if ($name -eq 'testhost' -or $name -like 'Cascade*') {
        $text = New-Object System.Text.StringBuilder 256
        [void][Foreground]::GetWindowText($window, $text, 256)
        # A window with no title says nothing about itself; its class does, and the WinForms one carries
        # the .NET type name, which is the whole answer.
        $class = New-Object System.Text.StringBuilder 256
        [void][Foreground]::GetClassName($window, $class, 256)
        $title = if ($text.Length -gt 0) { $text.ToString() } else { "(untitled: $($class.ToString()))" }
        $byWindow[$title] = 1 + $(if ($byWindow.ContainsKey($title)) { $byWindow[$title] } else { 0 })
    }
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

# Which window, when it is one of ours: that is what says where to look.
if ($byWindow.Count -gt 0) {
    Write-Host ''
    Write-Host 'the windows that had it:'
    $byWindow.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12 | ForEach-Object {
        Write-Host ("  {0,6:P1}  {1}" -f ($_.Value / $samples), $_.Key)
    }
}
