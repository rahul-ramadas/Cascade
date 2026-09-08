<#
.SYNOPSIS
    Merges every Cobertura report under a results directory and reports what the suites cover.

.DESCRIPTION
    Two suites instrument the same assemblies - the engine tests and the app checks both execute
    Cascade.Core - so the reports have to be merged by (file, line) before anything is added up. Summing
    the per-report totals instead would count shared lines twice and quietly inflate the figure, which is
    the one failure mode a coverage gate must not have.

    Prints a per-assembly and a worst-covered-files table, writes both to the GitHub step summary when
    running under Actions, and fails when the merged line rate is under -MinimumLineRate.

    The floor exists to catch a body of new code arriving with no tests, not to be ratcheted up for its own
    sake: a number chased for its own reward buys tests written to touch lines rather than to state
    properties.

.EXAMPLE
    pwsh -NoProfile -File scripts/Report-Coverage.ps1 -ResultsDirectory artifacts/test-results
#>
[CmdletBinding()]
param(
    [string] $ResultsDirectory = 'artifacts/test-results',
    [double] $MinimumLineRate = 0,
    [int] $WorstFiles = 12
)

$ErrorActionPreference = 'Stop'

$reports = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter '*cobertura.xml' -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    Write-Host "No Cobertura reports under $ResultsDirectory; nothing to report."
    exit 0
}

Write-Host "Merging $($reports.Count) coverage report(s)."

# key: "<assembly>|<file>" -> hashtable of line number -> covered (bool)
$files = @{}

foreach ($report in $reports) {
    [xml] $xml = Get-Content -LiteralPath $report.FullName
    foreach ($package in @($xml.coverage.packages.package)) {
        if (-not $package) { continue }
        foreach ($class in @($package.classes.class)) {
            if (-not $class) { continue }
            $key = "$($package.name)|$($class.filename)"
            if (-not $files.ContainsKey($key)) { $files[$key] = @{} }
            $seen = $files[$key]
            foreach ($line in @($class.lines.line)) {
                if (-not $line) { continue }
                $number = [int] $line.number
                $hit = ([int] $line.hits) -gt 0
                # A line counts as covered if ANY suite reached it.
                if ($hit -or -not $seen.ContainsKey($number)) { $seen[$number] = ($hit -or $seen[$number]) }
            }
        }
    }
}

$rows = foreach ($key in $files.Keys) {
    $lines = $files[$key]
    $total = $lines.Count
    if ($total -eq 0) { continue }
    $covered = @($lines.Values | Where-Object { $_ }).Count
    $parts = $key -split '\|', 2
    [pscustomobject]@{
        Assembly = $parts[0]
        File     = $parts[1]
        Total    = $total
        Covered  = $covered
        Rate     = $covered / $total
    }
}
$rows = @($rows)

$grandTotal = ($rows | Measure-Object Total -Sum).Sum
$grandCovered = ($rows | Measure-Object Covered -Sum).Sum
$grandRate = if ($grandTotal -gt 0) { $grandCovered / $grandTotal } else { 0 }

$byAssembly = $rows | Group-Object Assembly | ForEach-Object {
    $t = ($_.Group | Measure-Object Total -Sum).Sum
    $c = ($_.Group | Measure-Object Covered -Sum).Sum
    [pscustomobject]@{ Assembly = $_.Name; Covered = $c; Total = $t; Rate = $(if ($t) { $c / $t } else { 0 }) }
} | Sort-Object Assembly

Write-Host ''
Write-Host ('Line coverage: {0:P1} ({1:N0} of {2:N0} lines)' -f $grandRate, $grandCovered, $grandTotal)
Write-Host ''
$byAssembly | ForEach-Object { Write-Host ('  {0,-16} {1,6:P1}  {2,6:N0} / {3,6:N0}' -f $_.Assembly, $_.Rate, $_.Covered, $_.Total) }

$worst = $rows | Where-Object { $_.Total -ge 25 } | Sort-Object Rate | Select-Object -First $WorstFiles
Write-Host ''
Write-Host "Least covered files (25 lines or more):"
$worst | ForEach-Object {
    Write-Host ('  {0,6:P0}  {1,5:N0} lines  {2}' -f $_.Rate, $_.Total, (Split-Path -Leaf $_.File))
}

if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @(
        '### Code coverage'
        ''
        ('**{0:P1}** of lines covered ({1:N0} of {2:N0}), merged across every suite that instruments them.' -f $grandRate, $grandCovered, $grandTotal)
        ''
        '| Assembly | Line coverage | Covered | Total |'
        '| --- | ---: | ---: | ---: |'
    )
    $summary += $byAssembly | ForEach-Object { '| {0} | {1:P1} | {2:N0} | {3:N0} |' -f $_.Assembly, $_.Rate, $_.Covered, $_.Total }
    $summary += @(
        ''
        '<details><summary>Least covered files</summary>'
        ''
        '| File | Line coverage | Lines |'
        '| --- | ---: | ---: |'
    )
    $summary += $worst | ForEach-Object { '| `{0}` | {1:P0} | {2:N0} |' -f (Split-Path -Leaf $_.File), $_.Rate, $_.Total }
    $summary += '</details>'
    $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append
}

if ($MinimumLineRate -gt 0 -and ($grandRate * 100) -lt $MinimumLineRate) {
    throw ('Line coverage is {0:P1}, under the {1}% floor. Either the new code needs tests, or the floor needs a deliberate decision to move.' -f $grandRate, $MinimumLineRate)
}
