#requires -Version 7.0
<#
.SYNOPSIS
Focused, local validation for the temporary process observer. No DCP is launched.
.DESCRIPTION
Uses an isolated artifacts directory, harmless naturally exiting pwsh children,
and synthetic parser/identity inputs. Never terminates any process. No dependencies.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ObserverTest
{
    param([bool] $Condition, [string] $Description)
    if (-not $Condition) { throw "Observer validation failed: $Description" }
}

function Read-ObserverJsonLines
{
    param([string] $Path)
    $options = @{}
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $options.DateKind = 'String' }
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try
    {
        # A live checkpoint may end at '{"type":"slow-stop-sam' while the writer
        # is still appending. Parse only newline-terminated records; the next
        # checkpoint reads the original file again and includes the completed row.
        $text = $reader.ReadToEnd()
        $lastNewline = $text.LastIndexOf("`n")
        if ($lastNewline -lt 0) { return }
        foreach ($line in $text.Substring(0, $lastNewline).Split("`n"))
        {
            if ($line.Length -gt 0) { $line | ConvertFrom-Json @options }
        }
    }
    finally { $reader.Dispose() }
}

function Start-ObserverTestProcess
{
    param([string[]] $Arguments)
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -ErrorAction Stop).Source)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($info)
}

$observerPath = Join-Path $PSScriptRoot 'Observe-TreeActionsProcesses.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($observerPath, [ref]$tokens, [ref]$parseErrors)
Assert-ObserverTest ($parseErrors.Count -eq 0) 'PowerShell syntax'
# Load only this owned script's function definitions, not its CLI entry point.
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] })
{
    . ([scriptblock]::Create($definition.Extent.Text))
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$root = Join-Path $repoRoot "artifacts\tree-actions-observer-validation\$([Guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($root)
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
$success = $false
try
{
    $timestamp = '2026-09-18T19:23:08.599Z'
    $cases = @(
        @{ command = """C:\test tools\dcp.exe"" stop-process-tree --pid 123 --process-start-time $timestamp"; valid = $true },
        @{ command = "dcp.exe stop-process-tree --process-start-time=""$timestamp"" --pid=123"; valid = $true },
        @{ command = 'dcp.exe stop-process-tree --pid 123 --process-start-time 2026-09-18T21:23:08.599+02:00'; valid = $true },
        @{ command = 'dcp.exe stop-process-tree --pid 123 --process-start-time 2026-09-18T19:23:08.599123456Z'; valid = $true },
        @{ command = 'dcp.exe stop-process-tree --pid 123 --process-start-time 2026-09-18T19:23:08.999999999Z'; valid = $true },
        @{ command = "dcp.exe stop-process-tree --pid 123 --process-start-time $timestamp --other DO_NOT_PERSIST"; valid = $true },
        @{ command = "dcp.exe stop-process-tree --pids 123 --process-start-time $timestamp"; valid = $false },
        @{ command = "dcp.exe stop-process-tree --pid --process-start-time $timestamp"; valid = $false },
        @{ command = 'dcp.exe stop-process-tree --pid 123 --process-start-time'; valid = $false },
        @{ command = "dcp.exe stop-process-tree --pid 0 --process-start-time $timestamp"; valid = $false },
        @{ command = "dcp.exe stop-process-tree --pid -1 --process-start-time $timestamp"; valid = $false },
        @{ command = "dcp.exe stop-process-tree --pid 4294967296 --process-start-time $timestamp"; valid = $false },
        @{ command = "dcp.exe stop-process-tree --pid 123 --pid 124 --process-start-time $timestamp"; valid = $false },
        @{ command = 'dcp.exe stop-process-tree --pid 123 --process-start-time not-a-timestamp'; valid = $false },
        @{ command = 'dcp.exe stop-process-tree --pid 123 --process-start-time 2026-02-31T19:23:08Z'; valid = $false },
        @{ command = "dcp.exe stop-process-tree --pid 123 --process-start-time ""$timestamp`n"""; valid = $false },
        @{ command = "dcp.exe stop-process-tree ""--pid 123"" --process-start-time $timestamp"; valid = $false },
        @{ command = "dcp.exe unrelated --pid 123 --process-start-time $timestamp"; valid = $false },
        @{ command = '"C:\unterminated path\dcp.exe stop-process-tree --pid 123'; valid = $false }
    )
    foreach ($case in $cases)
    {
        $parsed = ConvertFrom-StopProcessTreeCommandLine $case.command
        Assert-ObserverTest (($parsed.argumentStatus -eq 'valid') -eq $case.valid) 'parser acceptance/rejection'
        if ($case.valid) { Assert-ObserverTest ($parsed.targetProcessId -eq 123) 'numeric target extraction' }
        Assert-ObserverTest (($parsed | ConvertTo-Json -Compress) -notmatch 'DO_NOT_PERSIST|test tools|not-a-timestamp') 'parser allowlist'
    }
    $nano = ConvertFrom-StopProcessTreeCommandLine 'dcp.exe stop-process-tree --pid 123 --process-start-time 2026-09-18T19:23:08.999999999Z'
    Assert-ObserverTest ($nano.expectedStartTimeUtc -ceq '2026-09-18T19:23:08.9999999Z') 'nanoseconds truncated without rounding into a newer time'

    $invalidConfigurationRejected = $false
    try { $null = New-ObserverState 'relative-path' $root (Join-Path $root 'stop') 1000 1 }
    catch { $invalidConfigurationRejected = $true }
    Assert-ObserverTest $invalidConfigurationRejected 'relative output rejected'

    $checkpointPath = Join-Path $root 'partial-checkpoint.jsonl'
    $checkpointWriter = Open-ObserverJsonLines $checkpointPath
    try
    {
        $checkpointWriter.WriteLine('{"type":"completed"}')
        $checkpointWriter.Write('{"type":"in-prog')
        $checkpointRows = @(Read-ObserverJsonLines $checkpointPath)
        Assert-ObserverTest ($checkpointRows.Count -eq 1 -and $checkpointRows[0].type -eq 'completed') 'live checkpoint excludes only its incomplete tail'
        $checkpointWriter.WriteLine('ress"}')
        $checkpointRows = @(Read-ObserverJsonLines $checkpointPath)
        Assert-ObserverTest ($checkpointRows.Count -eq 2 -and $checkpointRows[1].type -eq 'in-progress') 'next checkpoint sees completed record without closing writer'
    }
    finally { $checkpointWriter.Dispose() }

    $stopFile = Join-Path $root 'stop'
    $liveOutput = Join-Path $root 'live\observations'
    $observer = Start-ObserverTestProcess @('-NoProfile', '-NonInteractive', '-File', $observerPath,
        '-OutputDirectory', $liveOutput, '-DcpDirectory', $root, '-StopFile', $stopFile,
        '-SampleMilliseconds', '250', '-MaximumSeconds', '12')
    $children.Add($observer)
    $statusPath = Join-Path $liveOutput 'observer-status.json'
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do
    {
        Start-Sleep -Milliseconds 100
        $status = if ([IO.File]::Exists($statusPath)) { [IO.File]::ReadAllText($statusPath) | ConvertFrom-Json } else { $null }
    } while (($null -eq $status -or $status.counts.successfulSamples -lt 1) -and $deadline.Elapsed.TotalSeconds -lt 6 -and -not $observer.HasExited)
    Assert-ObserverTest ($null -ne $status -and $status.counts.successfulSamples -gt 0 -and -not $observer.HasExited) 'live heartbeat and successful sampling'

    $child = Start-ObserverTestProcess @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Milliseconds 1600')
    $children.Add($child)
    Assert-ObserverTest ($child.WaitForExit(5000)) 'harmless child exits naturally'
    $deadline.Restart()
    do
    {
        Start-Sleep -Milliseconds 150
        $records = @(Read-ObserverJsonLines (Join-Path $liveOutput 'processes.jsonl'))
        $exits = @($records | Where-Object { $_.type -eq 'process-disappeared' -and $_.identity.processId -eq $child.Id })
    } while ($exits.Count -eq 0 -and $deadline.Elapsed.TotalSeconds -lt 3 -and -not $observer.HasExited)
    [IO.File]::WriteAllText($stopFile, 'stop')
    $stopLatency = [Diagnostics.Stopwatch]::StartNew()
    Assert-ObserverTest ($observer.WaitForExit(5000)) 'sentinel stop within bounded CIM operation'
    $stopLatency.Stop()
    Assert-ObserverTest ($observer.ExitCode -eq 0) 'live observer exit code'
    $status = [IO.File]::ReadAllText($statusPath) | ConvertFrom-Json
    Assert-ObserverTest ($status.state -eq 'stopped' -and $status.stopReason -eq 'stop-file' -and $status.counts.diagnosticErrors -eq 0) 'clean final status'
    $starts = @($records | Where-Object { $_.type -eq 'process-observed' -and $_.identity.processId -eq $child.Id })
    Assert-ObserverTest ($starts.Count -eq 1 -and $exits.Count -eq 1) 'child start and disappearance identities'
    Assert-ObserverTest ($starts[0].identity.creationTimeUtc.EndsWith('Z')) 'UTC process creation time'
    Assert-ObserverTest ($starts[0].identity.parentProcessId -eq $PID) 'child parent PID'
    foreach ($record in $records)
    {
        Assert-ObserverTest ($record.schemaVersion -eq 1 -and $record.observerId -eq $status.observerId -and $record.observedAtUtc.EndsWith('Z')) 'JSONL envelope'
        Assert-ObserverTest ($record.PSObject.Properties.Name -notcontains 'CommandLine') 'no command line property'
    }

    $timeoutOutput = Join-Path $root 'timeout\observations'
    $timeoutObserver = Start-ObserverTestProcess @('-NoProfile', '-NonInteractive', '-File', $observerPath,
        '-OutputDirectory', $timeoutOutput, '-DcpDirectory', $root, '-StopFile', (Join-Path $root 'never-created'),
        '-MaximumSeconds', '2')
    $children.Add($timeoutObserver)
    Assert-ObserverTest ($timeoutObserver.WaitForExit(8000) -and $timeoutObserver.ExitCode -eq 0) 'finite timeout exit'
    $timeoutStatus = [IO.File]::ReadAllText((Join-Path $timeoutOutput 'observer-status.json')) | ConvertFrom-Json
    Assert-ObserverTest ($timeoutStatus.state -eq 'timed-out' -and $timeoutStatus.stopReason -eq 'maximum-seconds' -and $timeoutStatus.counts.successfulSamples -gt 0) 'timeout status and samples'

    $selfRow = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $PID" -Property ProcessId, ParentProcessId, Name, CreationDate -OperationTimeoutSec 2
    $selfIdentity = ConvertTo-ObserverIdentity $selfRow
    $unitState = New-ObserverState (Join-Path $root 'unit') $root (Join-Path $root 'unit-stop') 1000 30
    [void][IO.Directory]::CreateDirectory($unitState.OutputDirectory)
    $unitState.ProcessWriter = Open-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'processes.jsonl')
    $unitState.SlowStopWriter = Open-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'slow-stops.jsonl')
    try
    {
        $runtime = Get-ObserverHelperRuntime $unitState $selfIdentity
        Assert-ObserverTest ($runtime.availability -eq 'observed' -and $runtime.threads.Count -gt 0 -and $runtime.workingSetBytes -gt 0 -and $runtime.cpuTimeMilliseconds -ge 0) 'read-only thread and process metrics'
        foreach ($thread in $runtime.threads)
        {
            Assert-ObserverTest ($thread.threadId -gt 0 -and $thread.threadState -ne 'unavailable') 'thread-state schema'
            if ($thread.threadState -eq 'Wait') { Assert-ObserverTest ($null -ne $thread.waitReason) 'wait reason for waiting threads' }
        }
        $oldIdentity = [pscustomobject]@{
            processId = $selfIdentity.processId; parentProcessId = $selfIdentity.parentProcessId
            processName = $selfIdentity.processName; creationTimeUtc = '2000-01-01T00:00:00.0000000Z'
        }
        $mismatch = Get-ObserverHelperRuntime $unitState $oldIdentity
        Assert-ObserverTest ($mismatch.availability -eq 'identity-mismatch' -and -not $mismatch.ContainsKey('threads')) 'reused identity rejected before thread sampling'
        $snapshot = @{ $selfIdentity.processId = @{ identity = $selfIdentity } }
        $arguments = ConvertFrom-StopProcessTreeCommandLine "dcp.exe stop-process-tree --pid $PID --process-start-time $($selfIdentity.creationTimeUtc)"
        Assert-ObserverTest ((Get-ObserverTarget $arguments $snapshot).status -eq 'matches-recorded-precision') 'target identity correlation'
        $arguments.expectedStartTimeUtc = $oldIdentity.creationTimeUtc
        $target = Get-ObserverTarget $arguments $snapshot
        Assert-ObserverTest ($target.status -eq 'identity-mismatch' -and $null -eq $target.parentChain) 'mismatched target chain not followed'
        $childIdentity = [pscustomobject]@{ processId = [uint32]1; parentProcessId = $selfIdentity.processId; processName = 'synthetic'; creationTimeUtc = $oldIdentity.creationTimeUtc }
        Assert-ObserverTest ((Get-ObserverParentChain $childIdentity $snapshot).terminationReason -eq 'parent-pid-reused') 'newer parent rejected'

        $sampleTime = Get-ObserverUtcTimestamp
        $previous = @{ $selfIdentity.processId = @{ identity = $oldIdentity; firstObservedAtUtc = $sampleTime; lastObservedAtUtc = $sampleTime } }
        $current = @{ $selfIdentity.processId = @{ identity = $selfIdentity; firstObservedAtUtc = $sampleTime; lastObservedAtUtc = $sampleTime } }
        Update-ObserverProcesses $unitState $previous $current $sampleTime $false
        $transitionRecords = @(Read-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'processes.jsonl'))
        Assert-ObserverTest ($transitionRecords.Count -eq 2 -and $transitionRecords[0].type -eq 'process-disappeared' -and
            $transitionRecords[0].reason -eq 'identity-mismatch' -and $transitionRecords[1].observation -eq 'pid-reused') 'PID reuse emits separate old/new lifetimes'

        $generationRecordOffset = $unitState.Counts.processRecords
        $generationSnapshot = @{}
        for ($generation = 0; $generation -lt 30; $generation++)
        {
            $generationIdentity = [pscustomobject]@{
                processId = [uint32]126; parentProcessId = [uint32]0; processName = 'synthetic.exe'
                creationTimeUtc = [DateTimeOffset]::Parse($timestamp).UtcDateTime.AddSeconds($generation).ToString('o')
            }
            $nextGenerationSnapshot = @{ $generationIdentity.processId = @{
                identity = $generationIdentity; firstObservedAtUtc = $sampleTime; lastObservedAtUtc = $sampleTime
            } }
            Update-ObserverProcesses $unitState $generationSnapshot $nextGenerationSnapshot $sampleTime ($generation -eq 0)
            $generationSnapshot = $nextGenerationSnapshot
            $generationRecords = @(Read-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'processes.jsonl'))
            Assert-ObserverTest ($generationRecords.Count -eq ($generationRecordOffset + 1 + 2 * $generation) -and
                $generationRecords[-1].identity.creationTimeUtc -ceq $generationIdentity.creationTimeUtc) 'each PID generation is checkpoint-readable before writer closes'
        }
        Update-ObserverProcesses $unitState $generationSnapshot @{} $sampleTime $false
        $generationRecords = @(Read-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'processes.jsonl') | Select-Object -Skip $generationRecordOffset)
        Assert-ObserverTest ($generationRecords.Count -eq 60 -and
            @($generationRecords | Where-Object { $_.type -eq 'process-observed' }).Count -eq 30 -and
            @($generationRecords | Where-Object { $_.type -eq 'process-disappeared' }).Count -eq 30) '30 generations retain all 60 lifetime records'

        $script:discoveryState = $unitState
        $script:discoveryCalls = 0
        $script:discoveryRow = [pscustomobject]@{
            ProcessId = [uint32]125; ParentProcessId = [uint32]0; Name = 'dcp.exe'; CreationDate = [DateTime]::UtcNow
            ExecutablePath = $unitState.DcpExecutable
            CommandLine = """$($unitState.DcpExecutable)"" stop-process-tree --pid $PID --process-start-time $($selfIdentity.creationTimeUtc) --other DO_NOT_PERSIST"
        }
        function Get-CimInstance
        {
            param($ClassName, $Filter, $Property, $OperationTimeoutSec, $ErrorAction)
            $script:discoveryCalls++
            $escaped = $script:discoveryState.DcpExecutable.Replace('\', '\\').Replace("'", "\'")
            Assert-ObserverTest ($ClassName -eq 'Win32_Process' -and $Filter -ceq "Name = 'dcp.exe' AND ExecutablePath = '$escaped'" -and
                $OperationTimeoutSec -eq 2 -and $Property -contains 'CommandLine') 'command-line query restricted to exact DCP executable'
            return $script:discoveryRow
        }
        try
        {
            $discoveryIdentity = ConvertTo-ObserverIdentity $script:discoveryRow
            $discoverySnapshot = @{ $discoveryIdentity.processId = @{ identity = $discoveryIdentity } }
            $inspected = [Collections.Generic.HashSet[string]]::new()
            $discovered = @{}
            Find-ObserverStopHelpers $unitState $discoverySnapshot $inspected $discovered
            Find-ObserverStopHelpers $unitState $discoverySnapshot $inspected $discovered
            Assert-ObserverTest ($discovered.Count -eq 1 -and $script:discoveryCalls -eq 1) 'helper discovered once without repeatedly querying command lines'
            $discoveryRecords = @(Read-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'slow-stops.jsonl'))
            Assert-ObserverTest (($discoveryRecords | ConvertTo-Json -Depth 20) -notmatch 'DO_NOT_PERSIST|CommandLine|ExecutablePath') 'only allowlisted command-line fields persisted'
        }
        finally { Remove-Item Function:\Get-CimInstance }

        $originalRuntimeFunction = ${function:Get-ObserverHelperRuntime}
        function Get-ObserverHelperRuntime
        {
            param($State, $Identity)
            return @{
                availability = 'observed'; captureKind = 'thread-state-snapshot'; creationTimeUtc = $Identity.creationTimeUtc
                cpuTimeMilliseconds = 0; workingSetBytes = 1
                threads = @([pscustomobject]@{ threadId = 1; threadState = 'Wait'; waitReason = 'Executive' })
            }
        }
        try
        {
            # Exercise scheduling without running a real stop-process-tree helper.
            $helperIdentity = [pscustomobject]@{ processId = [uint32]123; parentProcessId = [uint32]0; processName = 'dcp.exe'; creationTimeUtc = '' }
            $snapshot[$helperIdentity.processId] = @{ identity = $helperIdentity }
            $arguments = ConvertFrom-StopProcessTreeCommandLine "dcp.exe stop-process-tree --pid $PID --process-start-time $($selfIdentity.creationTimeUtc)"
            $helpers = @{ synthetic = @{ identity = $helperIdentity; arguments = $arguments; attemptedAges = [Collections.Generic.HashSet[int]]::new() } }
            foreach ($age in @(4, 8, 12))
            {
                $helperIdentity.creationTimeUtc = [DateTime]::UtcNow.AddSeconds(-$age - 0.1).ToString('o')
                Update-ObserverStopHelpers $unitState $snapshot $helpers $sampleTime
                $liveSlowRecords = @(Read-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'slow-stops.jsonl') | Where-Object { $_.type -eq 'slow-stop-sample' })
                Assert-ObserverTest ($liveSlowRecords.Count -eq ($age / 4) -and $liveSlowRecords[-1].targetAgeSeconds -eq $age -and
                    $liveSlowRecords[-1].runtime.threads[0].waitReason -eq 'Executive') 'each slow-stop thread sample flushes before the next threshold'
                Update-ObserverStopHelpers $unitState $snapshot $helpers $sampleTime
            }
            $slowRecords = @(Read-ObserverJsonLines (Join-Path $unitState.OutputDirectory 'slow-stops.jsonl') | Where-Object { $_.type -eq 'slow-stop-sample' })
            Assert-ObserverTest ($slowRecords.Count -eq 3 -and ($slowRecords.targetAgeSeconds -join ',') -eq '4,8,12') 'one sample per threshold'
            foreach ($record in $slowRecords)
            {
                Assert-ObserverTest ($record.runtime.captureKind -eq 'thread-state-snapshot' -and $record.runtime.threads.Count -eq 1 -and
                    $record.target.status -eq 'matches-recorded-precision' -and $record.helperParentChain.terminationReason -eq 'root') 'slow-stop JSON shape'
            }
            $helpers.synthetic.attemptedAges.Clear()
            $helperIdentity.creationTimeUtc = [DateTime]::UtcNow.AddSeconds(-20).ToString('o')
            Update-ObserverStopHelpers $unitState $snapshot $helpers $sampleTime
            Assert-ObserverTest ($unitState.Counts.missedHelperSamples -eq 3 -and $unitState.Counts.helperSamples -eq 3) 'late samples marked missed, not relabeled'
            $snapshot.Remove($helperIdentity.processId)
            Update-ObserverStopHelpers $unitState $snapshot $helpers $sampleTime
            Assert-ObserverTest ($helpers.Count -eq 0) 'disappeared helper removed'
        }
        finally { Set-Item Function:\Get-ObserverHelperRuntime $originalRuntimeFunction }
    }
    finally { $unitState.ProcessWriter.Dispose(); $unitState.SlowStopWriter.Dispose() }

    $script:testCimInvocation = 0
    function Get-CimInstance
    {
        $script:testCimInvocation++
        $row = [pscustomobject]@{ ProcessId = [uint32]123; ParentProcessId = [uint32]0; Name = 'synthetic.exe'; CreationDate = [DateTime]::UtcNow.AddMinutes(-1) }
        if ($script:testCimInvocation -eq 1) { return $row }
        if ($script:testCimInvocation -eq 2)
        {
            return @($row, [pscustomobject]@{ ProcessId = [uint32]124; ParentProcessId = [uint32]0; Name = 'invalid.exe'; CreationDate = 'DO_NOT_PERSIST_PRIVATE_ERROR' })
        }
        throw [InvalidOperationException]::new('DO_NOT_PERSIST_PRIVATE_ERROR')
    }
    try
    {
        $errorState = New-ObserverState (Join-Path $root 'sampling-error') $root (Join-Path $root 'error-stop') 250 1
        Assert-ObserverTest ((Invoke-ProcessLifetimeObserver $errorState) -eq 2) 'sampling failures affect exit code'
        $errorRecords = @(Read-ObserverJsonLines (Join-Path $errorState.OutputDirectory 'processes.jsonl'))
        Assert-ObserverTest (@($errorRecords | Where-Object { $_.type -eq 'diagnostic-error' }).Count -gt 0) 'explicit sampling error records'
        Assert-ObserverTest (($errorRecords | ConvertTo-Json -Depth 20) -notmatch 'DO_NOT_PERSIST_PRIVATE_ERROR') 'exception message redaction'
        $errorStatus = [IO.File]::ReadAllText((Join-Path $errorState.OutputDirectory 'observer-status.json')) | ConvertFrom-Json
        Assert-ObserverTest ($errorStatus.health -eq 'degraded' -and $errorStatus.counts.successfulSamples -eq 1 -and $errorStatus.counts.diagnosticErrors -gt 0) 'final failure counts'
        Assert-ObserverTest (@($errorRecords | Where-Object { $_.type -eq 'process-disappeared' }).Count -eq 0) 'failed/partial snapshots never fabricate exits'
    }
    finally { Remove-Item Function:\Get-CimInstance }

    $lockedOutput = Join-Path $root 'locked-output'
    [void][IO.Directory]::CreateDirectory($lockedOutput)
    $lockedStream = [IO.FileStream]::new((Join-Path $lockedOutput 'processes.jsonl'), [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $savedError = [Console]::Error
    $capturedError = [IO.StringWriter]::new()
    try
    {
        [Console]::SetError($capturedError)
        $lockedState = New-ObserverState $lockedOutput $root (Join-Path $root 'locked-stop') 1000 1
        $lockedExitCode = Invoke-ProcessLifetimeObserver $lockedState
    }
    finally
    {
        [Console]::SetError($savedError)
        $lockedStream.Dispose()
    }
    Assert-ObserverTest ($lockedExitCode -eq 1 -and $capturedError.ToString().Contains('Process observer failed:')) 'filesystem startup failure is explicit'
    Assert-ObserverTest (-not [IO.File]::Exists((Join-Path $lockedOutput 'observer-status.json'))) 'failed duplicate startup cannot replace owner status'
    $capturedError.Dispose()

    $success = $true
    Write-Output "PASS: $($cases.Count) parser cases; live JSONL/heartbeat; child start/exit; sentinel stop ($($stopLatency.ElapsedMilliseconds) ms); timeout; thread metrics; PID/parent reuse; exact-DCP command-line query; 4/8/12s sample windows; slow-stop JSON; sanitized sampling errors; failed/partial snapshots; filesystem startup failure; live checkpoints; 30 generations/60 promptly flushed lifetime records."
}
catch
{
    $failedStatusPath = Join-Path $root 'live\observations\observer-status.json'
    if ([IO.File]::Exists($failedStatusPath))
    {
        Write-Output "Validation observer status: $([IO.File]::ReadAllText($failedStatusPath))"
    }
    throw
}
finally
{
    $allExited = $true
    foreach ($process in $children)
    {
        if (-not $process.HasExited)
        {
            # Await only children created above; all have their own short deadlines.
            # A failed assertion must never introduce a process-termination cleanup.
            if (-not $process.WaitForExit(16000)) { $allExited = $false }
        }
        $process.Dispose()
    }
    if ($allExited)
    {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    else
    {
        Write-Warning 'A validation child exceeded its deadline; no process was terminated and its diagnostics were retained.'
        if ($success) { throw 'A validation child did not exit naturally.' }
    }
}
