<#
.SYNOPSIS
    Works out which tests belong to one shard of a suite, and prints the filter that selects them.

.DESCRIPTION
    A suite is split across several CI runners so its wall time falls by the number of them. The split is
    computed from what is really in the assembly rather than from a list kept by hand: a test added
    tomorrow lands in a shard without anybody remembering to put it there, and a shard that would
    otherwise silently run nothing is an error rather than a green tick.

    Tests are discovered, grouped (by class, or by method so that one big class can still be split),
    sorted so the answer does not depend on discovery order, and dealt round-robin. Round-robin rather than
    contiguous blocks because neighbouring tests in a class tend to cost the same, so dealing them spreads
    the expensive ones out.

    VSTest's ~ operator means "contains", so a group whose name is a prefix of another's would silently
    drag it along. That is checked for and reported rather than left to be discovered as a test running
    twice - or, worse, a shard quietly covering something the tally says it did not.

.PARAMETER Project
    The test project to discover. Must already be built; discovery uses --no-build.

.PARAMETER Shard
    Which shard to emit, from 1 to -Of.

.PARAMETER By
    Class (default) keeps a class together and gives a short filter. Method splits classes up, which is
    what a suite with one very large class needs.

.PARAMETER ExpectedFile
    Where to write every test method the assembly holds, for scripts/Check-Shards.ps1 to hold the shards to.

.EXAMPLE
    scripts/Split-Tests.ps1 -Project tests/Cascade.UiTests/Cascade.UiTests.csproj -Shard 2 -Of 4 -By Method
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [int] $Shard,
    [Parameter(Mandatory)] [int] $Of,
    [ValidateSet('Class', 'Method')] [string] $By = 'Class',
    [string] $ExpectedFile,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if ($Shard -lt 1 -or $Shard -gt $Of) { throw "Shard $Shard is not between 1 and $Of." }

$listing = & dotnet test $Project -c $Configuration --no-build --nologo -v q --list-tests 2>&1
if ($LASTEXITCODE -ne 0) { throw "Discovery failed ($LASTEXITCODE):`n$($listing -join "`n")" }

# Everything indented under "The following Tests are available:". A theory case carries its arguments in
# brackets, which are dropped so every case of one theory stays with its method.
$names = @($listing |
    Where-Object { $_ -match '^\s{4,}\S' } |
    ForEach-Object { $_.Trim() } |
    ForEach-Object { ($_ -split '\(', 2)[0] })

if ($names.Count -eq 0) { throw "No tests were discovered in $Project. Was it built?" }

$groups = @($names | ForEach-Object {
        if ($By -eq 'Class') { $_.Substring(0, $_.LastIndexOf('.')) } else { $_ }
    } | Sort-Object -Unique)

if ($groups.Count -lt $Of) {
    throw "$Project has only $($groups.Count) $By group(s), which cannot fill $Of shards."
}

# "Contains" would match a longer name that starts the same way. A class filter can end in a dot, which no
# longer name can extend; a method filter cannot, so a collision there has to be reported.
foreach ($g in $groups) {
    $clash = @($groups | Where-Object { $_ -ne $g -and $_.StartsWith($g, [StringComparison]::Ordinal) })
    if ($clash.Count -gt 0 -and $By -eq 'Method') {
        throw "'$g' is a prefix of $($clash -join ', '), so a contains-filter cannot tell them apart. Rename one."
    }
}

$mine = @(for ($i = $Shard - 1; $i -lt $groups.Count; $i += $Of) { $groups[$i] })
$suffix = if ($By -eq 'Class') { '.' } else { '' }
$filter = ($mine | ForEach-Object { "FullyQualifiedName~$_$suffix" }) -join '|'

$methods = @($names | Sort-Object -Unique)
Write-Host "Shard $Shard of ${Of}: $($mine.Count) of $($groups.Count) $By groups, $($methods.Count) test methods in the assembly" -ForegroundColor Cyan
$mine | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

if ($ExpectedFile) {
    # Every method the assembly has, so the job that collects the shards can prove that between them they
    # ran all of it. Methods rather than cases: a theory whose data is not serializable is one entry at
    # discovery and several at run time, so counting cases would compare two different things.
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ExpectedFile) | Out-Null
    $methods | Set-Content -LiteralPath $ExpectedFile
}
if ($env:GITHUB_OUTPUT) { "filter=$filter" | Out-File -FilePath $env:GITHUB_OUTPUT -Append }
$filter
