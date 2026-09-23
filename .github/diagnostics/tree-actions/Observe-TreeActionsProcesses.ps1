#requires -Version 7.0
<#
.SYNOPSIS
Temporary, read-only Windows process observer for Aspire issue #20103.
.DESCRIPTION
Run in a separate pwsh process with a run-owned output directory. JSONL records
are appended and flushed individually; observer-status.json is replaced atomically.
Only the exact DcpDirectory\dcp.exe may have its command line queried. Only its
numeric target PID and validated RFC3339 start-time argument leave memory.

These are sampled identities and thread states, NOT stack dumps or exit events
from the OS. Processes shorter than the sampling interval can be missed. A parent
PID is not proof of ownership, particularly when its creation time is unavailable.
Use a fresh hosted VM for global PID/name/creation metadata; sanitize before upload.

Exit codes: 0 = clean sentinel stop or time limit, 2 = completed with diagnostic
errors, 1 = invalid startup configuration or fatal observer/filesystem failure.
No processes are stopped, debugged, suspended, or elevated.
.PARAMETER OutputDirectory
Exact absolute destination, normally the parent's <runOutput>\observations.
No implicit subdirectory is added. Use a separate directory for each fresh job.
.PARAMETER StopFile
Absolute sentinel in an existing directory. The parent creates it after ExTester
exits, waits for the observer to exit, then takes its final checkpoint.
.PARAMETER MaximumSeconds
Finite safety deadline (default 900), measured after argument validation. Supply
the full runner budget, including startup and cleanup, if it exceeds the default.
.NOTES
Before starting ExTester, check observer-status.json for state=running, a recent
heartbeatAtUtc, counts.successfulSamples > 0, and dcpExecutablePresent=true.
Status also exposes lastSuccessfulSampleAtUtc, health, counts.diagnosticErrors,
counts.processRecords, counts.slowStopRecords, and counts.helperSamples.

Final state=stopped with stopReason=stop-file means the sentinel was observed;
state=timed-out with stopReason=maximum-seconds means coverage ended at the deadline.
Both can exit 0, so the controller must inspect status, not just the exit code.
Fatal runtime failures set state=failed; startup failures may leave no status file.

For live checkpoints, copy rather than move/delete active JSONL files. Each
record is flushed to the OS before its counter advances. A concurrent copy can
end during the next write: consume only the prefix ending at the last newline,
and retain the original active file for subsequent checkpoints. Complete records
must parse; do not silently discard malformed complete lines. Status is atomic
but summarizes a completed iteration, so JSONL can be ahead of its record counts.
These files never contain environment variables, full command lines, or dumps.
.EXAMPLE
pwsh -NoProfile -NonInteractive -File .github\diagnostics\tree-actions\Observe-TreeActionsProcesses.ps1 -OutputDirectory C:\run\observations -DcpDirectory C:\tools\dcp -StopFile C:\run\observer.stop -MaximumSeconds 900
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $OutputDirectory,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $DcpDirectory,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $StopFile,
    [ValidateRange(250, 10000)][int] $SampleMilliseconds = 1000,
    [ValidateRange(1, 86400)][int] $MaximumSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ObserverUtcTimestamp
{
    return [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
}

function ConvertFrom-StopProcessTreeCommandLine
{
    param([AllowNull()][string] $CommandLine)

    $result = [ordered]@{
        isStopProcessTree = $false
        argumentStatus = 'not-stop-process-tree'
        targetProcessId = $null
        processStartTimeToken = $null
        expectedStartTimeUtc = $null
        timestampQuantumTicks = $null
    }

    # Accepted machine-generated shapes include:
    #   "C:\test tools\dcp.exe" stop-process-tree --pid 123 --process-start-time 2026-09-18T19:23:08.599Z
    #   dcp.exe stop-process-tree --pid=123 --process-start-time="2026-09-18T19:23:08.599+00:00"
    # "--pid 123" is ONE token, not a flag. Bare, duplicate, and nonnumeric flags
    # are rejected. Embedded escaped quotes are unnecessary for these arguments;
    # reject them rather than pretending to implement arbitrary Windows argv rules.
    # Never return the original command line, other arguments, or invalid values.
    if ([string]::IsNullOrWhiteSpace($CommandLine) -or $CommandLine.Contains('\"'))
    {
        $result.argumentStatus = 'unsupported-quoting'
        return [pscustomobject]$result
    }

    $words = [Collections.Generic.List[string]]::new()
    $tokenPattern = [regex]::new('\G\s*(?<word>(?:[^\s"]+|"[^"]*")+)(?:\s+|$)',
        [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(1))
    $position = 0
    while ($position -lt $CommandLine.Length)
    {
        $match = $tokenPattern.Match($CommandLine, $position)
        if (-not $match.Success)
        {
            $result.argumentStatus = 'unsupported-quoting'
            return [pscustomobject]$result
        }
        $words.Add($match.Groups['word'].Value.Replace('"', ''))
        if ($words.Count -eq 2 -and $words[1] -ceq 'stop-process-tree') { $result.isStopProcessTree = $true }
        $position = $match.Index + $match.Length
    }

    if ($words.Count -lt 2 -or $words[1] -cne 'stop-process-tree')
    {
        return [pscustomobject]$result
    }
    $result.isStopProcessTree = $true
    $values = @{}
    for ($index = 2; $index -lt $words.Count; $index++)
    {
        $flag = $words[$index]
        $equals = $flag.IndexOf('=')
        $value = $null
        if ($equals -ge 0)
        {
            $value = $flag.Substring($equals + 1)
            $flag = $flag.Substring(0, $equals)
        }
        if ($flag -cne '--pid' -and $flag -cne '--process-start-time')
        {
            continue
        }
        if ($values.ContainsKey($flag))
        {
            $result.argumentStatus = 'duplicate-flag'
            return [pscustomobject]$result
        }
        if ($equals -lt 0)
        {
            $index++
            if ($index -ge $words.Count -or $words[$index].StartsWith('--'))
            {
                $result.argumentStatus = 'bare-flag'
                return [pscustomobject]$result
            }
            $value = $words[$index]
        }
        $values[$flag] = $value
    }

    [uint32]$targetId = 0
    if (-not $values.ContainsKey('--pid') -or $values['--pid'] -cnotmatch '\A[0-9]+\z' -or
        -not [uint32]::TryParse($values['--pid'], [ref]$targetId) -or $targetId -eq 0)
    {
        $result.argumentStatus = 'invalid-or-missing-pid'
        return [pscustomobject]$result
    }
    $time = if ($values.ContainsKey('--process-start-time')) { $values['--process-start-time'] } else { '' }
    $timeMatch = [regex]::Match($time, '\A[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.(?<fraction>[0-9]{1,9}))?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})\z')
    # RFC3339Nano can contain nine fractional digits. DateTimeOffset rounds those;
    # truncate to its seven-digit precision so rounding cannot invent a newer PID.
    $parseableTime = $time
    if ($timeMatch.Success -and $timeMatch.Groups['fraction'].Length -gt 7)
    {
        $fraction = $timeMatch.Groups['fraction']
        $parseableTime = $time.Remove($fraction.Index + 7, $fraction.Length - 7)
    }
    $parsedTime = [DateTimeOffset]::MinValue
    if (-not $timeMatch.Success -or -not [DateTimeOffset]::TryParse(
        $parseableTime, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$parsedTime))
    {
        $result.argumentStatus = 'invalid-or-missing-start-time'
        return [pscustomobject]$result
    }

    $result.argumentStatus = 'valid'
    $result.targetProcessId = $targetId
    $result.processStartTimeToken = $time
    $result.expectedStartTimeUtc = $parsedTime.UtcDateTime.ToString('o')
    $result.timestampQuantumTicks = [long][Math]::Pow(10, 7 - [Math]::Min(7, $timeMatch.Groups['fraction'].Length))
    return [pscustomobject]$result
}

function New-ObserverState
{
    param([string] $OutputDirectory, [string] $DcpDirectory, [string] $StopFile, [int] $SampleMilliseconds, [int] $MaximumSeconds)

    if (-not $IsWindows)
    {
        throw 'This observer requires Windows.'
    }
    foreach ($value in @($OutputDirectory, $DcpDirectory, $StopFile))
    {
        if (-not [IO.Path]::IsPathFullyQualified($value))
        {
            throw 'OutputDirectory, DcpDirectory, and StopFile must be absolute filesystem paths.'
        }
    }
    $OutputDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
    $DcpDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($DcpDirectory))
    $StopFile = [IO.Path]::GetFullPath($StopFile)
    if ([IO.File]::Exists($OutputDirectory) -or $OutputDirectory -eq [IO.Path]::GetPathRoot($OutputDirectory))
    {
        throw 'OutputDirectory must be a run-owned directory, not a file or drive root.'
    }
    if (-not [IO.Directory]::Exists($DcpDirectory))
    {
        throw 'DcpDirectory must be an existing directory.'
    }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($StopFile)) -or [IO.Directory]::Exists($StopFile))
    {
        throw 'StopFile must name a file in an existing directory.'
    }
    foreach ($reserved in @('processes.jsonl', 'slow-stops.jsonl', 'observer-status.json'))
    {
        if ($StopFile -ieq [IO.Path]::Combine($OutputDirectory, $reserved))
        {
            throw 'StopFile must not be an observer output file.'
        }
    }

    $identifier = [Guid]::NewGuid().ToString('N')
    return @{
        Id = $identifier
        OutputDirectory = $OutputDirectory
        DcpExecutable = [IO.Path]::Combine($DcpDirectory, 'dcp.exe')
        StopFile = $StopFile
        SampleMilliseconds = $SampleMilliseconds
        MaximumSeconds = $MaximumSeconds
        StartedAtUtc = Get-ObserverUtcTimestamp
        Clock = [Diagnostics.Stopwatch]::StartNew()
        Status = 'starting'
        StopReason = $null
        FinishedAtUtc = $null
        LastSuccessfulSampleAtUtc = $null
        FatalError = $null
        ProcessWriter = $null
        SlowStopWriter = $null
        StatusWorkFile = [IO.Path]::Combine($OutputDirectory, "observer-status.$identifier.new")
        Counts = [ordered]@{
            samplingAttempts = 0; successfulSamples = 0; processRecords = 0
            slowStopRecords = 0; diagnosticErrors = 0; observedProcessCount = 0
            helpersObserved = 0; helperSamples = 0; missedHelperSamples = 0
        }
        ErrorsByStage = @{}
    }
}

function Open-ObserverJsonLines
{
    param([string] $Path)

    # Readers must share write access (Node's normal fs readers do). Denying other
    # writers prevents two observers from silently interleaving the same run's logs.
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    # Checkpoints must see every completed generation/thread record while this
    # writer stays open, without waiting for the next heartbeat or process exit.
    $writer.AutoFlush = $true
    return $writer
}

function Write-ObserverRecord
{
    param($State, [ValidateSet('processes', 'slow-stops')][string] $Stream, [string] $Type, [Collections.IDictionary] $Data)

    $record = [ordered]@{ schemaVersion = 1; observerId = $State.Id; observedAtUtc = Get-ObserverUtcTimestamp; type = $Type }
    foreach ($key in $Data.Keys) { $record[$key] = $Data[$key] }
    $writer = if ($Stream -eq 'processes') { $State.ProcessWriter } else { $State.SlowStopWriter }
    $writer.WriteLine(($record | ConvertTo-Json -Depth 20 -Compress))
    if ($Stream -eq 'processes') { $State.Counts.processRecords++ } else { $State.Counts.slowStopRecords++ }
}

function Get-ObserverErrorMetadata
{
    param([Management.Automation.ErrorRecord] $ErrorRecord)

    # Exception messages, invocation information, and stack traces may contain
    # input strings. Persist only these bounded, non-content-bearing attributes.
    $exception = $ErrorRecord.Exception.GetBaseException()
    return [ordered]@{
        exceptionType = $exception.GetType().FullName
        hresult = $exception.HResult
        category = $ErrorRecord.CategoryInfo.Category.ToString()
    }
}

function Write-ObserverDiagnosticError
{
    param($State, [string] $Stream, [string] $Stage, [string] $Code, $ErrorRecord = $null, $Identity = $null)

    $State.Counts.diagnosticErrors++
    if (-not $State.ErrorsByStage.ContainsKey($Stage)) { $State.ErrorsByStage[$Stage] = 0 }
    $State.ErrorsByStage[$Stage]++
    $errorData = if ($null -ne $ErrorRecord) { Get-ObserverErrorMetadata $ErrorRecord } else { $null }
    Write-ObserverRecord $State $Stream 'diagnostic-error' @{ stage = $Stage; code = $Code; error = $errorData; identity = $Identity }
}

function Write-ObserverStatus
{
    param($State)

    $status = [ordered]@{
        schemaVersion = 1; observerId = $State.Id; observerProcessId = $PID
        state = $State.Status; stopReason = $State.StopReason
        health = if ($State.Counts.diagnosticErrors -eq 0 -and $null -eq $State.FatalError) { 'healthy' } else { 'degraded' }
        startedAtUtc = $State.StartedAtUtc; heartbeatAtUtc = Get-ObserverUtcTimestamp
        lastSuccessfulSampleAtUtc = $State.LastSuccessfulSampleAtUtc; finishedAtUtc = $State.FinishedAtUtc
        elapsedMilliseconds = $State.Clock.ElapsedMilliseconds
        sampleMilliseconds = $State.SampleMilliseconds; maximumSeconds = $State.MaximumSeconds
        cimOperationTimeoutSeconds = 2; counts = $State.Counts; errorsByStage = $State.ErrorsByStage
        dcpExecutablePresent = [IO.File]::Exists($State.DcpExecutable); fatalError = $State.FatalError
    }
    [IO.File]::WriteAllText($State.StatusWorkFile, ($status | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($State.StatusWorkFile, [IO.Path]::Combine($State.OutputDirectory, 'observer-status.json'), $true)
}

function Get-ObserverStopReason
{
    param($State)

    if ([IO.File]::Exists($State.StopFile)) { return 'stop-file' }
    if ($State.Clock.Elapsed.TotalSeconds -ge $State.MaximumSeconds) { return 'maximum-seconds' }
    return $null
}

function ConvertTo-ObserverIdentity
{
    param($Process)

    return [pscustomobject]@{
        processId = [uint32]$Process.ProcessId
        parentProcessId = [uint32]$Process.ParentProcessId
        processName = [string]$Process.Name
        creationTimeUtc = if ($null -ne $Process.CreationDate) { $Process.CreationDate.ToUniversalTime().ToString('o') } else { $null }
    }
}

function Test-ObserverIdentity
{
    param($Left, $Right)

    return $Left.processId -eq $Right.processId -and $Left.creationTimeUtc -ceq $Right.creationTimeUtc -and $Left.processName -ieq $Right.processName
}

function Get-ObserverIdentityKey
{
    param($Identity)

    return "$($Identity.processId):$($Identity.creationTimeUtc):$($Identity.processName)"
}

function Update-ObserverProcesses
{
    param($State, $Previous, $Current, [string] $SampleTime, [bool] $Initial)

    foreach ($entry in $Previous.Values)
    {
        $id = $entry.identity.processId
        if (-not $Current.ContainsKey($id) -or -not (Test-ObserverIdentity $entry.identity $Current[$id].identity))
        {
            $reason = if ($Current.ContainsKey($id)) { 'identity-mismatch' } else { 'not-in-snapshot' }
            Write-ObserverRecord $State 'processes' 'process-disappeared' @{
                identity = $entry.identity; reason = $reason; snapshotAtUtc = $SampleTime
                firstObservedAtUtc = $entry.firstObservedAtUtc; lastObservedAtUtc = $entry.lastObservedAtUtc
            }
        }
    }
    foreach ($entry in $Current.Values)
    {
        $id = $entry.identity.processId
        if ($Previous.ContainsKey($id) -and (Test-ObserverIdentity $Previous[$id].identity $entry.identity))
        {
            $entry.firstObservedAtUtc = $Previous[$id].firstObservedAtUtc
            if ($entry.identity.parentProcessId -ne $Previous[$id].identity.parentProcessId)
            {
                Write-ObserverRecord $State 'processes' 'process-metadata-changed' @{
                    identity = $entry.identity; previousIdentity = $Previous[$id].identity; snapshotAtUtc = $SampleTime
                }
            }
        }
        else
        {
            $observation = if ($Initial) { 'initial' } elseif ($Previous.ContainsKey($id)) { 'pid-reused' } else { 'started' }
            Write-ObserverRecord $State 'processes' 'process-observed' @{
                identity = $entry.identity; observation = $observation; snapshotAtUtc = $SampleTime
                firstObservedAtUtc = $entry.firstObservedAtUtc
            }
        }
    }
}

function Get-ObserverParentChain
{
    param($Identity, $Snapshot)

    $nodes = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[uint32]]::new()
    [void]$seen.Add($Identity.processId)
    $child = $Identity
    $reason = 'depth-limit'
    for ($depth = 0; $depth -lt 64; $depth++)
    {
        $parentId = $child.parentProcessId
        if ($parentId -eq 0) { $reason = 'root'; break }
        if (-not $seen.Add($parentId)) { $reason = 'cycle'; break }
        if (-not $Snapshot.ContainsKey($parentId)) { $reason = 'parent-not-in-snapshot'; break }
        $parent = $Snapshot[$parentId].identity
        if ($null -eq $child.creationTimeUtc -or $null -eq $parent.creationTimeUtc)
        {
            $nodes.Add([pscustomobject]@{ identity = $parent; relationship = 'creation-time-unavailable' })
            $reason = 'unverifiable-parent'
            break
        }
        if ([DateTimeOffset]::Parse($parent.creationTimeUtc) -gt [DateTimeOffset]::Parse($child.creationTimeUtc))
        {
            $nodes.Add([pscustomobject]@{ identity = $parent; relationship = 'parent-created-after-child' })
            $reason = 'parent-pid-reused'
            break
        }
        $nodes.Add([pscustomobject]@{ identity = $parent; relationship = 'consistent-with-creation-times' })
        $child = $parent
    }
    return [pscustomobject]@{ nodes = $nodes.ToArray(); terminationReason = $reason; unresolvedParentProcessId = $child.parentProcessId }
}

function Get-ObserverTarget
{
    param($Arguments, $Snapshot)

    $result = [ordered]@{
        requestedProcessId = $Arguments.targetProcessId; requestedStartTime = $Arguments.processStartTimeToken
        expectedStartTimeUtc = $Arguments.expectedStartTimeUtc; status = 'arguments-unavailable'
        identity = $null; parentChain = $null
    }
    if ($Arguments.argumentStatus -ne 'valid') { return [pscustomobject]$result }
    if (-not $Snapshot.ContainsKey($Arguments.targetProcessId))
    {
        $result.status = 'not-in-snapshot'
        return [pscustomobject]$result
    }
    $identity = $Snapshot[$Arguments.targetProcessId].identity
    $result.identity = $identity
    if ($null -eq $identity.creationTimeUtc)
    {
        $result.status = 'creation-time-unavailable'
        return [pscustomobject]$result
    }

    # CIM truncates creation times to microseconds; a supplied RFC3339 token may
    # itself only retain milliseconds or seconds. Preserve both raw timestamps and
    # label a common precision bucket as correlation, not proof of process identity.
    # Integer remainders avoid losing precision through floating-point tick division.
    $quantum = [Math]::Max([long]10, [long]$Arguments.timestampQuantumTicks)
    $expectedTicks = [DateTimeOffset]::Parse($Arguments.expectedStartTimeUtc).UtcTicks
    $actualTicks = [DateTimeOffset]::Parse($identity.creationTimeUtc).UtcTicks
    if (($expectedTicks - ($expectedTicks % $quantum)) -ne ($actualTicks - ($actualTicks % $quantum)))
    {
        $result.status = 'identity-mismatch'
        return [pscustomobject]$result
    }
    $result.status = 'matches-recorded-precision'
    $result.parentChain = Get-ObserverParentChain $identity $Snapshot
    return [pscustomobject]$result
}

function Get-ObserverHelperRuntime
{
    param($State, $Identity)

    $live = $null
    $verification = $null
    try
    {
        $expectedTicks = [DateTimeOffset]::Parse($Identity.creationTimeUtc).UtcTicks
        $live = [Diagnostics.Process]::GetProcessById([int]$Identity.processId)
        $actualStart = $live.StartTime.ToUniversalTime()
        if (($actualStart.Ticks - ($actualStart.Ticks % 10)) -ne $expectedTicks)
        {
            return @{ availability = 'identity-mismatch'; observedCreationTimeUtc = $actualStart.ToString('o') }
        }
        $threads = [Collections.Generic.List[object]]::new()
        foreach ($thread in $live.Threads)
        {
            $threadId = $null
            try
            {
                $threadId = $thread.Id
                $threadState = $thread.ThreadState
                $threads.Add([pscustomobject]@{
                    threadId = $threadId; threadState = $threadState.ToString()
                    waitReason = if ($threadState -eq [Diagnostics.ThreadState]::Wait) { $thread.WaitReason.ToString() } else { $null }
                })
            }
            catch
            {
                Write-ObserverDiagnosticError $State 'slow-stops' 'thread-state' 'thread-unavailable' $_ $Identity
                $threads.Add([pscustomobject]@{ threadId = $threadId; threadState = 'unavailable'; waitReason = $null })
            }
            finally { $thread.Dispose() }
        }
        $cpuTime = $live.TotalProcessorTime.TotalMilliseconds
        $workingSet = $live.WorkingSet64

        # A fresh Process instance avoids cached StartTime data. Discard statistics
        # if the PID disappeared or was reused while the non-atomic APIs ran.
        $verification = [Diagnostics.Process]::GetProcessById([int]$Identity.processId)
        $verifiedStart = $verification.StartTime.ToUniversalTime()
        if ($verifiedStart.Ticks -ne $actualStart.Ticks)
        {
            return @{ availability = 'identity-mismatch'; observedCreationTimeUtc = $verifiedStart.ToString('o') }
        }
        if ($verification.HasExited) { return @{ availability = 'disappeared' } }
        return @{
            availability = 'observed'; captureKind = 'thread-state-snapshot'
            creationTimeUtc = $actualStart.ToString('o'); cpuTimeMilliseconds = $cpuTime
            workingSetBytes = $workingSet; threads = $threads.ToArray()
        }
    }
    catch
    {
        $exception = $_.Exception.GetBaseException()
        if ($exception -is [ArgumentException] -or $exception -is [InvalidOperationException] -or
            ($exception -is [ComponentModel.Win32Exception] -and $exception.NativeErrorCode -in @(6, 87, 1168)))
        {
            return @{ availability = 'disappeared-during-sample'; error = Get-ObserverErrorMetadata $_ }
        }
        Write-ObserverDiagnosticError $State 'slow-stops' 'helper-runtime' 'runtime-query-failed' $_ $Identity
        return @{ availability = 'query-failed' }
    }
    finally
    {
        if ($null -ne $verification) { $verification.Dispose() }
        if ($null -ne $live) { $live.Dispose() }
    }
}

function Find-ObserverStopHelpers
{
    param($State, $Snapshot, $Inspected, $Helpers)

    $candidates = @($Snapshot.Values | Where-Object {
        $_.identity.processName -ieq 'dcp.exe' -and -not $Inspected.Contains((Get-ObserverIdentityKey $_.identity))
    })
    if ($candidates.Count -eq 0 -or $null -ne (Get-ObserverStopReason $State)) { return }
    # WQL escapes '\' and apostrophes inside string literals. Apply the exact
    # executable filter at the provider, BEFORE requesting any CommandLine value.
    $escapedPath = $State.DcpExecutable.Replace('\', '\\').Replace("'", "\'")
    try
    {
        $rows = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'dcp.exe' AND ExecutablePath = '$escapedPath'" `
            -Property ProcessId, ParentProcessId, Name, CreationDate, ExecutablePath, CommandLine -OperationTimeoutSec 2 -ErrorAction Stop)
    }
    catch
    {
        Write-ObserverDiagnosticError $State 'slow-stops' 'helper-discovery' 'cim-query-failed' $_
        return
    }
    foreach ($candidate in $candidates) { [void]$Inspected.Add((Get-ObserverIdentityKey $candidate.identity)) }
    foreach ($row in $rows)
    {
        if ($null -ne (Get-ObserverStopReason $State)) { break }
        $identity = ConvertTo-ObserverIdentity $row
        $key = Get-ObserverIdentityKey $identity
        if ($Helpers.ContainsKey($key)) { continue }
        if ($row.ExecutablePath -ine $State.DcpExecutable) { continue }
        if (-not $Snapshot.ContainsKey($identity.processId) -or
            -not (Test-ObserverIdentity $Snapshot[$identity.processId].identity $identity))
        {
            Write-ObserverRecord $State 'slow-stops' 'helper-unavailable' @{ identity = $identity; reason = 'snapshot-identity-mismatch' }
            continue
        }
        if ($null -eq $identity.creationTimeUtc -or [string]::IsNullOrWhiteSpace($row.CommandLine))
        {
            Write-ObserverDiagnosticError $State 'slow-stops' 'helper-discovery' 'identity-or-command-line-unavailable' $null $identity
            continue
        }
        try { $arguments = ConvertFrom-StopProcessTreeCommandLine $row.CommandLine }
        catch
        {
            Write-ObserverDiagnosticError $State 'slow-stops' 'helper-discovery' 'argument-parser-failed' $_ $identity
            continue
        }
        if (-not $arguments.isStopProcessTree) { continue }
        if ($arguments.argumentStatus -ne 'valid')
        {
            Write-ObserverDiagnosticError $State 'slow-stops' 'helper-arguments' $arguments.argumentStatus $null $identity
        }
        $Helpers[$key] = @{ identity = $identity; arguments = $arguments; attemptedAges = [Collections.Generic.HashSet[int]]::new() }
        $State.Counts.helpersObserved++
        Write-ObserverRecord $State 'slow-stops' 'stop-helper-observed' @{ identity = $identity; arguments = $arguments }
    }
}

function Update-ObserverStopHelpers
{
    param($State, $Snapshot, $Helpers, [string] $SampleTime)

    foreach ($key in @($Helpers.Keys))
    {
        if ($null -ne (Get-ObserverStopReason $State)) { break }
        $helper = $Helpers[$key]
        $identity = $helper.identity
        if (-not $Snapshot.ContainsKey($identity.processId) -or -not (Test-ObserverIdentity $identity $Snapshot[$identity.processId].identity))
        {
            $reason = if ($Snapshot.ContainsKey($identity.processId)) { 'identity-mismatch' } else { 'not-in-snapshot' }
            Write-ObserverRecord $State 'slow-stops' 'stop-helper-disappeared' @{ identity = $identity; reason = $reason; snapshotAtUtc = $SampleTime }
            $Helpers.Remove($key)
            continue
        }
        $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($identity.creationTimeUtc)).TotalSeconds
        foreach ($threshold in @(4, 8, 12))
        {
            if ($age -lt $threshold -or -not $helper.attemptedAges.Add($threshold)) { continue }
            # Do not label three identical late snapshots as samples at ages 4/8/12.
            # A missed four-second window is explicit evidence of a sampling gap.
            if ($age -ge ($threshold + 4))
            {
                $State.Counts.missedHelperSamples++
                Write-ObserverRecord $State 'slow-stops' 'slow-stop-sample-missed' @{
                    identity = $identity; targetAgeSeconds = $threshold; actualAgeSeconds = $age; reason = 'missed-window'
                }
                continue
            }
            $runtime = Get-ObserverHelperRuntime $State $identity
            $recordType = if ($runtime.availability -eq 'observed') { 'slow-stop-sample' } else { 'slow-stop-unavailable' }
            if ($runtime.availability -eq 'observed') { $State.Counts.helperSamples++ }
            Write-ObserverRecord $State 'slow-stops' $recordType @{
                identity = $identity; targetAgeSeconds = $threshold; actualAgeSeconds = $age
                snapshotAtUtc = $SampleTime; runtime = $runtime
                target = Get-ObserverTarget $helper.arguments $Snapshot
                helperParentChain = Get-ObserverParentChain $identity $Snapshot
            }
        }
    }
}

function Invoke-ProcessLifetimeObserver
{
    param($State)

    $exitCode = 0
    try
    {
        [void][IO.Directory]::CreateDirectory($State.OutputDirectory)
        $State.ProcessWriter = Open-ObserverJsonLines ([IO.Path]::Combine($State.OutputDirectory, 'processes.jsonl'))
        $State.SlowStopWriter = Open-ObserverJsonLines ([IO.Path]::Combine($State.OutputDirectory, 'slow-stops.jsonl'))
        Write-ObserverRecord $State 'processes' 'observer-started' @{}
        Write-ObserverRecord $State 'slow-stops' 'observer-started' @{}
        $State.Status = 'running'
        Write-ObserverStatus $State
        $previous = @{}
        $inspected = [Collections.Generic.HashSet[string]]::new()
        $helpers = @{}
        while ($null -eq (Get-ObserverStopReason $State))
        {
            $nextSample = $State.Clock.ElapsedMilliseconds + $State.SampleMilliseconds
            $State.Counts.samplingAttempts++
            $current = @{}
            $sampleTime = $null
            try
            {
                # No ExecutablePath or CommandLine is requested for global metadata.
                $rows = @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, Name, CreationDate `
                    -OperationTimeoutSec 2 -ErrorAction Stop)
                $sampleTime = Get-ObserverUtcTimestamp
                foreach ($row in $rows)
                {
                    $identity = ConvertTo-ObserverIdentity $row
                    $current[$identity.processId] = @{
                        identity = $identity; firstObservedAtUtc = $sampleTime; lastObservedAtUtc = $sampleTime
                    }
                }
            }
            catch
            {
                $sampleTime = $null
                Write-ObserverDiagnosticError $State 'processes' 'process-snapshot' 'cim-snapshot-failed' $_
            }
            if ($null -ne $sampleTime)
            {
                Update-ObserverProcesses $State $previous $current $sampleTime ($State.Counts.successfulSamples -eq 0)
                $State.Counts.successfulSamples++
                $State.Counts.observedProcessCount = $current.Count
                $State.LastSuccessfulSampleAtUtc = $sampleTime
                Find-ObserverStopHelpers $State $current $inspected $helpers
                Update-ObserverStopHelpers $State $current $helpers $sampleTime
                $previous = $current
            }
            # A failed query never replaces the previous snapshot with an empty one.
            Write-ObserverStatus $State
            while ($State.Clock.ElapsedMilliseconds -lt $nextSample -and $null -eq (Get-ObserverStopReason $State))
            {
                Start-Sleep -Milliseconds ([Math]::Min(100, [Math]::Max(1, $nextSample - $State.Clock.ElapsedMilliseconds)))
            }
        }
        $State.StopReason = Get-ObserverStopReason $State
        $State.Status = if ($State.StopReason -eq 'stop-file') { 'stopped' } else { 'timed-out' }
        Write-ObserverRecord $State 'processes' 'observer-finished' @{ state = $State.Status; stopReason = $State.StopReason }
        if ($State.Counts.diagnosticErrors -gt 0) { $exitCode = 2 }
    }
    catch
    {
        $exitCode = 1
        $State.Status = 'failed'
        $State.StopReason = 'fatal-error'
        $State.FatalError = Get-ObserverErrorMetadata $_
        [Console]::Error.WriteLine("Process observer failed: $($State.FatalError.exceptionType); see observer-status.json if writable.")
    }
    finally
    {
        $State.FinishedAtUtc = Get-ObserverUtcTimestamp
        foreach ($writer in @($State.ProcessWriter, $State.SlowStopWriter))
        {
            if ($null -ne $writer)
            {
                try { $writer.Dispose() }
                catch
                {
                    $exitCode = 1
                    $State.Status = 'failed'
                    $State.StopReason = 'filesystem-error'
                    $State.FatalError = Get-ObserverErrorMetadata $_
                    [Console]::Error.WriteLine('Process observer could not flush/close a diagnostic file.')
                }
            }
        }
        # If opening the primary log failed, this run never acquired ownership.
        # In particular, don't replace a still-running observer's status on a
        # second accidental launch into the same output directory.
        if ($null -ne $State.ProcessWriter)
        {
            try { Write-ObserverStatus $State }
            catch
            {
                $exitCode = 1
                [Console]::Error.WriteLine("Process observer could not write final status: $($_.Exception.GetBaseException().GetType().FullName).")
            }
        }
    }
    return $exitCode
}

try
{
    $state = New-ObserverState $OutputDirectory $DcpDirectory $StopFile $SampleMilliseconds $MaximumSeconds
}
catch
{
    [Console]::Error.WriteLine("Invalid observer configuration ($($_.Exception.GetBaseException().GetType().FullName)). Require Windows, absolute paths, an existing DCP directory and sentinel parent, and a run-owned output directory.")
    exit 1
}
exit (Invoke-ProcessLifetimeObserver $state)
