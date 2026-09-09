<#
.SYNOPSIS
    Proves that a suite split across several runners still ran every test it has.

.DESCRIPTION
    Splitting a suite into shards introduces a failure that a green run cannot otherwise show: a filter
    that selects fewer tests than intended leaves the rest unrun, and the pipeline says nothing at all
    because everything it did run passed. Coverage would drop, but only by whatever those tests uniquely
    reached, which can be nothing.

    So each shard records every test METHOD the assembly holds - the same list in every shard of a suite -
    and this checks it against the methods the shards between them actually produced results for. Methods
    rather than cases, because a theory whose data is not serializable is one entry at discovery and
    several at run time; counting cases would be comparing two different things.

    A skipped test still produces a result, so a skip is not a shortfall.

.PARAMETER ResultsDirectory
    Where the shards' <suite>-<shard>.trx and <suite>-<shard>.expected files were collected.
#>
[CmdletBinding()]
param([string] $ResultsDirectory = 'artifacts/test-results')

$ErrorActionPreference = 'Stop'

$manifests = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter '*.expected' -ErrorAction SilentlyContinue)
if ($manifests.Count -eq 0) { throw "No shard manifests under $ResultsDirectory; the test jobs did not report what they discovered." }

$bad = @()
foreach ($suite in ($manifests | Group-Object { ($_.BaseName -split '-')[0] })) {
    $lists = @($suite.Group | ForEach-Object { ((Get-Content -LiteralPath $_.FullName) -join "`n").Trim() } | Sort-Object -Unique)
    if ($lists.Count -ne 1) {
        $bad += "$($suite.Name): its shards disagree about what the assembly holds. They did not all run the same build."
        continue
    }
    $expected = [System.Collections.Generic.HashSet[string]]::new([string[]] @($lists[0] -split "`n" | Where-Object { $_ }))

    $ran = [System.Collections.Generic.HashSet[string]]::new()
    $shards = 0
    foreach ($trx in Get-ChildItem -Path $ResultsDirectory -Recurse -Filter "$($suite.Name)-*.trx") {
        # XmlReader rather than an XmlDocument walked through PowerShell's adapter: a shard's results run to
        # hundreds of entries and the adapter builds an object for every one of them.
        $reader = [System.Xml.XmlReader]::Create($trx.FullName)
        try {
            while ($reader.Read()) {
                if ($reader.NodeType -eq [System.Xml.XmlNodeType]::Element -and $reader.Name -eq 'UnitTestResult') {
                    $name = $reader.GetAttribute('testName')
                    if ($name) { [void] $ran.Add((($name -split '\(', 2)[0])) }
                }
            }
        }
        finally { $reader.Dispose() }
        $shards++
    }

    $missing = @($expected | Where-Object { -not $ran.Contains($_) })
    $line = '{0,-6} {1,4} of {2,4} test methods across {3} shard(s)' -f $suite.Name, ($expected.Count - $missing.Count), $expected.Count, $shards
    if ($missing.Count -eq 0) { Write-Host "  $line" -ForegroundColor Green }
    else {
        Write-Host "  $line" -ForegroundColor Red
        $shown = ($missing | Select-Object -First 10) -join "`n      "
        $bad += "$($suite.Name): $($missing.Count) test method(s) ran in no shard. The filters do not cover:`n      $shown"
    }
    if ($shards -ne $suite.Group.Count) {
        $bad += "$($suite.Name): $shards result file(s) for $($suite.Group.Count) shard(s) - one did not report."
    }
}

if ($bad) { throw ("Sharding lost tests:`n  " + ($bad -join "`n  ")) }
Write-Host 'Every shard ran what its assembly holds.' -ForegroundColor Green
