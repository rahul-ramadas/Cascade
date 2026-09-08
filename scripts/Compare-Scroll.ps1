<#
.SYNOPSIS
    Runs the scroll bench against two builds, alternately, and reports the difference.

.DESCRIPTION
    A drag through a multi-gigabyte log is measured on a machine that is also doing other things, and the
    file it reads is paged in by the operating system rather than by this program. Both drift over minutes:
    the same build measured twice, half an hour apart, has read 2x apart here. So neither build is measured
    on its own - they are run ALTERNATELY, several times each, and the best run of each is compared. Drift
    that affects both equally then cancels, and drift that does not shows up as disagreement between rounds.

    Publish the two builds first, e.g.

        git stash push src/...                 # take the change away
        dotnet publish ... -o artifacts/ab/base
        git stash pop                          # put it back
        dotnet publish ... -o artifacts/ab/new

.EXAMPLE
    pwsh -NoProfile -File scripts/Compare-Scroll.ps1 -File E:\Dump\test.txt -Filters E:\Scripts\Bluetooth.cascade
#>
[CmdletBinding()]
param(
    [string] $Base = 'artifacts/ab/base/Cascade.exe',
    [string] $New = 'artifacts/ab/new/Cascade.exe',
    [string] $File,
    [string] $Filters,
    [string] $Only = '',
    [int] $Rounds = 3,
    [int] $Steps = 150,
    [int] $Lines = 1000000,
    [switch] $Parts
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Invoke-Bench([string] $exe) {
    $arguments = @('--scrollbench', "--steps=$Steps", '--repeat=1')
    if ($File) { $arguments += "--file=$File" } else { $arguments += "--lines=$Lines" }
    if ($Filters) { $arguments += "--filters=$Filters" }
    if ($Only) { $arguments += "--only=$Only" }
    if ($Parts) { $arguments += '--parts' }

    $text = & (Join-Path $repo $exe) @arguments 2>&1 | Out-String
    $found = @{}
    # "  <name>  BEST  0.55 wall |  0.52 cpu | ..." - the bench's own summary line for a scenario.
    foreach ($line in $text -split "`r?`n") {
        if ($line -match '^\s{2}(?<name>.+?)\s+BEST\s+(?<wall>[\d.]+) wall \|\s+(?<cpu>[\d.]+) cpu') {
            $found[$Matches.name.Trim()] = [pscustomobject]@{
                Wall = [double]$Matches.wall
                Cpu  = [double]$Matches.cpu
            }
        }
    }
    if ($found.Count -eq 0) { throw "No results parsed from $exe. Output was:`n$text" }
    $found
}

$runs = @{ base = @(); new = @() }
for ($round = 1; $round -le $Rounds; $round++) {
    # Base first on odd rounds, new first on even, so neither build always gets the cold cache.
    $order = if ($round % 2) { @('base', 'new') } else { @('new', 'base') }
    foreach ($which in $order) {
        Write-Host "round $round/$Rounds : $which" -ForegroundColor DarkGray
        $runs[$which] += , (Invoke-Bench ($which -eq 'base' ? $Base : $New))
    }
}

function Best($list, $scenario, $field) {
    $values = @($list | Where-Object { $_.ContainsKey($scenario) } | ForEach-Object { $_[$scenario].$field })
    if ($values.Count -eq 0) { return $null }
    ($values | Measure-Object -Minimum).Minimum
}

$scenarios = @($runs.base + $runs.new | ForEach-Object { $_.Keys } | Sort-Object -Unique)
Write-Host ''
$scenarios | ForEach-Object {
    $b = Best $runs.base $_ 'Wall'
    $n = Best $runs.new $_ 'Wall'
    $bc = Best $runs.base $_ 'Cpu'
    $nc = Best $runs.new $_ 'Cpu'
    if ($null -eq $b -or $null -eq $n) { return }
    [pscustomobject]@{
        Scenario   = $_
        BaseWall   = $b
        NewWall    = $n
        'Wall%'    = [math]::Round(100 * ($n - $b) / $b, 1)
        BaseCpu    = $bc
        NewCpu     = $nc
        'Cpu%'     = [math]::Round(100 * ($nc - $bc) / $bc, 1)
    }
} | Format-Table -AutoSize

Write-Host 'ms per mouse report, best of every round. Negative is faster.' -ForegroundColor DarkGray
