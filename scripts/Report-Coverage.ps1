<#
.SYNOPSIS
    Merges every Cobertura report under a results directory and reports what the suites cover.

.DESCRIPTION
    Every suite instruments the same assemblies - the engine tests, the app checks and the UI suite all
    execute Cascade.Core - so the reports have to be merged by (file, line) before anything is added up.
    Summing the per-report totals instead would count shared lines twice and quietly inflate the figure,
    which is the one failure mode a coverage gate must not have.

    THE REPORTS MUST ALL COME FROM THE SAME COLLECTOR AND THE SAME SETTINGS, or the union of their line
    sets is neither one's answer. tests/coverage.runsettings is that one place; scripts/Run-Tests.ps1 wraps
    every suite in dotnet-coverage so the UI suite - which drives the app as a separate process - is
    measured too.

    Prints a per-assembly and a worst-covered-files table, writes both to the GitHub step summary when
    running under Actions, and fails when the merged line rate is under -MinimumLineRate or fewer than
    -MinimumLines were measured at all.

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
    [int] $MinimumLines = 0,
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
    $xml = [System.Xml.XmlDocument]::new()
    $xml.Load($report.FullName)
    # SelectNodes and GetAttribute rather than dot-notation: PowerShell's XML adapter builds an object per
    # element as you walk it, and these reports carry a hundred thousand line elements each. MEASURED on one
    # run's twelve reports, that difference is most of what this script costs.
    foreach ($package in $xml.SelectNodes('/coverage/packages/package')) {
        # A coverage number that counts the test code measures nothing. Named by the suffix rather than by
        # listing the product assemblies, so a new one of those is reported rather than silently dropped.
        $assembly = $package.GetAttribute('name')
        if ($assembly -like '*Tests') { continue }
        foreach ($class in $package.SelectNodes('classes/class')) {
            $key = "$assembly|$($class.GetAttribute('filename'))"
            $seen = $files[$key]
            if ($null -eq $seen) { $seen = @{}; $files[$key] = $seen }
            foreach ($line in $class.SelectNodes('lines/line')) {
                $number = [int] $line.GetAttribute('number')
                $hit = [int] $line.GetAttribute('hits') -gt 0
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

# A rate floor only catches the figure getting WORSE. The failure mode this guards against makes it LOOK
# BETTER: excluding compiler-generated code takes the body of every async method and lambda out of the
# denominator, so the percentage climbs while less is measured. It shows as the line count collapsing -
# 13,616 to 13,026 when it happened - and nothing else would say a word about it.
if ($MinimumLines -gt 0 -and $grandTotal -lt $MinimumLines) {
    throw ('Only {0:N0} lines were measured, under the {1:N0} expected. Coverage is probably no longer being collected the way tests/coverage.runsettings describes - check that before touching this number.' -f $grandTotal, $MinimumLines)
}

if ($MinimumLineRate -gt 0 -and ($grandRate * 100) -lt $MinimumLineRate) {
    throw ('Line coverage is {0:P1}, under the {1}% floor. Either the new code needs tests, or the floor needs a deliberate decision to move.' -f $grandRate, $MinimumLineRate)
}
