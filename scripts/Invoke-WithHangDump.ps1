<#
.SYNOPSIS
    Runs a command and, if it has not finished in time, photographs everything underneath it before
    killing it.

.DESCRIPTION
    A step that wedges on a hosted runner leaves nothing behind. GitHub Actions kills the job when it
    reaches its cap, the log stops mid-sentence, and afterwards there is no way to tell a deadlock from a
    slow machine. That is exactly what a nightly fuzz run did: twenty-three seconds one night, EIGHTY-EIGHT
    MINUTES the next, cancelled at the job's limit, with no stack, no dump, and nothing to reproduce - and
    it took the three steps behind it with it.

    This runs the command as a child process and watches the clock. Past the deadline it walks the whole
    process tree, says what each process is and how much processor time it has had, asks every .NET process
    in it for its managed stacks, writes a dump of each, and only then kills them.

    THE STACKS GO TO THE CONSOLE as well as to files. An artifact has to be downloaded, and a run's log can
    be read the moment it goes red - so the answer to "where was it stuck" should be in the log itself, and
    the dump kept for the questions the stacks raise.

    `dotnet test --blame-hang` covers the case where a TEST hangs, and the nightly asks for that too; the
    two are not the same thing. Blame arms inside the test host, so it can say nothing at all when the test
    host never started, when discovery wedged, or when the runner itself is the problem. This watches from
    outside and needs nothing of the thing it is watching.

    On the happy path the cost is one extra process and a poll.

.PARAMETER TimeoutMinutes
    How long to let it run. Keep this comfortably BELOW the step's own timeout-minutes: Actions kills a
    step at its cap without asking anybody, so a watchdog that fires after it never fires at all.

.PARAMETER ReportDirectory
    Where the stacks, dumps and captured output go. Upload it from the job with if: always().

.PARAMETER Name
    A word for this run, used to name the files and to label the console output.

.PARAMETER Quiet
    Send the command's output to a file rather than the console, and print the tail of it only if the
    command fails. For a caller that runs the same suite many times over.

.PARAMETER Command
    The command and its arguments, after a bare `--`. The marker is what keeps `-c Release` from binding
    to this script's own -Command, so it is not optional.

    CALL THIS SCRIPT INLINE, not with `pwsh -File`: -File hands `--` to the parameter binder as though it
    were a parameter name, and the run fails before the command is reached. A `run:` block in a workflow
    with `shell: pwsh` is inline, which is what the nightly relies on.

.EXAMPLE
    ./scripts/Invoke-WithHangDump.ps1 -Name fuzz -TimeoutMinutes 20 `
        -ReportDirectory artifacts/hangs -- dotnet test tests/Cascade.Core.Tests/Cascade.Core.Tests.csproj

.EXAMPLE
    # Prove it still bites: a suite cut off part way should print stacks, write a dump and exit 124.
    ./scripts/Invoke-WithHangDump.ps1 -Name proof -TimeoutMinutes 0.1 `
        -ReportDirectory artifacts/hangs -- dotnet test tests/Cascade.Core.Tests/Cascade.Core.Tests.csproj
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [double] $TimeoutMinutes,
    [Parameter(Mandatory)] [string] $ReportDirectory,
    [string] $Name = 'run',
    [switch] $Quiet,
    [Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $Command
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$ReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path

# Everything the watchdog produces is written here as well as printed, so a job can upload one directory.
$summary = Join-Path $ReportDirectory "$Name-hang.txt"
function Record([string] $line) {
    Write-Host $line
    Add-Content -LiteralPath $summary -Value $line
}

# The process tree as it stands, breadth first. Parent ids are reused once a process exits, so a visited
# set is not paranoia: without it a recycled id can point back up the tree and the walk never ends.
function Get-Tree([int] $root) {
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine, CreationDate
    $children = @{}
    foreach ($process in $all) {
        $parent = [int] $process.ParentProcessId
        if (-not $children.ContainsKey($parent)) { $children[$parent] = @() }
        $children[$parent] += $process
    }

    $found = @()
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($process in $all) { if ([int] $process.ProcessId -eq $root) { $queue.Enqueue($process) } }
    while ($queue.Count -gt 0) {
        $process = $queue.Dequeue()
        if (-not $seen.Add([int] $process.ProcessId)) { continue }
        $found += $process
        foreach ($child in $children[[int] $process.ProcessId]) { $queue.Enqueue($child) }
    }
    $found
}

# The diagnostic tools are fetched only once something has actually wedged. On the happy path that is a
# download nobody pays for, and by the time it is wanted there is time to spare - the alternative is
# twenty seconds off every nightly for a tool that is almost never used.
function Resolve-Tool([string] $tool) {
    $installed = Join-Path $env:USERPROFILE ".dotnet\tools\$tool.exe"
    if (Test-Path -LiteralPath $installed) { return $installed }
    $onPath = Get-Command $tool -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    Record "  installing $tool..."
    & dotnet tool install --global $tool 2>&1 | ForEach-Object { Record "    $_" }
    if (Test-Path -LiteralPath $installed) { return $installed }
    $onPath = Get-Command $tool -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    Record "  $tool is not available; carrying on without it."
    return $null
}

# A diagnostic that hangs is worse than no diagnostic: it would eat the rest of the step's budget and the
# kill below would never happen. Every one of them is given a short leash of its own.
function Invoke-Tool([string] $tool, [string[]] $toolArguments, [string] $outputFile, [int] $seconds = 90) {
    if (-not $tool) { return $false }
    try {
        $options = @{ FilePath = $tool; ArgumentList = $toolArguments; NoNewWindow = $true; PassThru = $true }
        if ($outputFile) { $options.RedirectStandardOutput = $outputFile }
        $tail = Start-Process @options
        if (-not $tail.WaitForExit($seconds * 1000)) {
            $tail.Kill($true)
            Record "  ($(Split-Path -Leaf $tool) did not answer within ${seconds}s)"
            return $false
        }
        return $true
    }
    catch { Record "  ($(Split-Path -Leaf $tool) failed: $($_.Exception.Message))"; return $false }
}

# Only a .NET process can be asked any of this, and asking anything else is not merely useless: dotnet-stack
# waits out its whole timeout on a conhost, which is time the kill below is waiting for. The runtime creates
# a `dotnet-diagnostic-<pid>-...` named pipe in every .NET process, so the question answers itself.
function Get-DotNetProcessIds() {
    try {
        return [System.IO.Directory]::GetFiles('\\.\pipe\') |
               ForEach-Object { [regex]::Match($_, 'dotnet-diagnostic-(\d+)(?:-|$)') } |
               Where-Object { $_.Success } |
               ForEach-Object { [int] $_.Groups[1].Value }
    }
    catch { return @() }
}

if ($Command[0] -eq '--') { $Command = @($Command | Select-Object -Skip 1) }
if ($Command.Count -eq 0) { throw 'Nothing to run: put the command after a bare -- .' }
$exe = $Command[0]
$rest = if ($Command.Count -gt 1) { $Command[1..($Command.Count - 1)] } else { @() }

$options = @{ FilePath = $exe; NoNewWindow = $true; PassThru = $true }
if ($rest.Count -gt 0) { $options.ArgumentList = $rest }
$captured = Join-Path $ReportDirectory "$Name-output.log"
if ($Quiet) {
    $options.RedirectStandardOutput = $captured
    $options.RedirectStandardError = (Join-Path $ReportDirectory "$Name-error.log")
}

Write-Host "[$Name] $exe $($rest -join ' ')" -ForegroundColor DarkGray
$started = Get-Date
$run = Start-Process @options

if ($run.WaitForExit([int] [Math]::Round($TimeoutMinutes * 60000))) {
    $run.WaitForExit()          # settles the exit code; the timed overload can return before it is cached
    $seconds = ((Get-Date) - $started).TotalSeconds
    Write-Host ("[{0}] finished in {1:N1}s with exit code {2}." -f $Name, $seconds, $run.ExitCode) -ForegroundColor DarkGray
    if ($Quiet -and $run.ExitCode -ne 0 -and (Test-Path -LiteralPath $captured)) {
        Get-Content -LiteralPath $captured -Tail 40 | ForEach-Object { Write-Host "  $_" }
    }
    exit $run.ExitCode
}

Record "=== $Name did not finish ==="
Record "Ran for $TimeoutMinutes minute(s) and was still going: $exe $($rest -join ' ')"

# From here on nothing may be allowed to throw: everything below is a best effort at describing a process
# that is already wedged, and the one thing that MUST happen is the kill in the finally. Under
# ErrorActionPreference Stop - which is what a workflow step runs with - a tool that writes a word to
# stderr is a terminating error, and the step would then leave the wedged tree behind for the job to
# inherit. This is the one place in the repository where Continue is the careful setting.
$ErrorActionPreference = 'Continue'
$PSNativeCommandUseErrorActionPreference = $false

try {
    Record ''
    Record 'Process tree:'

    $tree = @()
    try { $tree = Get-Tree $run.Id } catch { Record "  (could not read the process tree: $($_.Exception.Message))" }
    foreach ($process in $tree) {
        $cpu = '?'
        $memory = '?'
        try {
            $live = Get-Process -Id ([int] $process.ProcessId) -ErrorAction Stop
            $cpu = '{0:N1}s' -f $live.TotalProcessorTime.TotalSeconds
            $memory = '{0:N0} MB' -f ($live.WorkingSet64 / 1MB)
        }
        catch { }
        Record ("  {0,-6} {1,-22} cpu {2,-9} ws {3,-10} started {4:HH:mm:ss}" -f
                $process.ProcessId, $process.Name, $cpu, $memory, $process.CreationDate)
        Record ("           {0}" -f $process.CommandLine)
    }

    # Managed stacks first: they are the answer most of the time, and unlike a dump they can be read
    # straight out of the run's log without downloading anything.
    Record ''
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'   # the tools target an older runtime than the SDK under test
    $stackTool = Resolve-Tool 'dotnet-stack'
    $dumpTool = Resolve-Tool 'dotnet-dump'
    $managed = Get-DotNetProcessIds

    foreach ($process in $tree) {
        $id = [int] $process.ProcessId
        if ($id -eq $PID) { continue }
        if ($managed -notcontains $id) {
            Record "--- $($process.Name) ($id) is not a .NET process; nothing to ask it ---"
            continue
        }

        $stacks = Join-Path $ReportDirectory "$Name-$id-$($process.Name)-stacks.txt"
        Record "--- managed stacks of $($process.Name) ($id), also in $stacks ---"
        if ((Invoke-Tool $stackTool @('report', '--process-id', "$id") $stacks 60) -and (Test-Path -LiteralPath $stacks)) {
            Get-Content -LiteralPath $stacks | ForEach-Object { Write-Host "  $_" }
        }

        $dump = Join-Path $ReportDirectory "$Name-$id-$($process.Name).dmp"
        Invoke-Tool $dumpTool @('collect', '--process-id', "$id", '--type', 'Mini', '--output', $dump) '' 120
        # An empty dump means the process went away part way through being photographed. Keep the fact, not
        # the file: a nought-byte .dmp in an artifact reads as a dump somebody could open, and it is not one.
        if ((Test-Path -LiteralPath $dump) -and (Get-Item -LiteralPath $dump).Length -eq 0) {
            Remove-Item -LiteralPath $dump -Force
            Record '  (it exited while the dump was being taken)'
        }
        elseif (Test-Path -LiteralPath $dump) {
            Record ("  dump: {0} ({1:N0} KB)" -f $dump, ((Get-Item -LiteralPath $dump).Length / 1KB))
        }
    }

    if ($Quiet -and (Test-Path -LiteralPath $captured)) {
        Record ''
        Record "Last of its output ($captured):"
        Get-Content -LiteralPath $captured -Tail 40 | ForEach-Object { Record "  $_" }
    }
}
finally {
    Record ''
    Record 'Killing it.'
    try { $run.Kill($true) } catch { Record "  (could not kill it: $($_.Exception.Message))" }
}

exit 124
