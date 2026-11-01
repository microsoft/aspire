<#
.SYNOPSIS
  Lightweight background resource sampler for Windows CI jobs.

.DESCRIPTION
  Diagnostics only: records machine pressure while a job runs so slow, hung, or
  lost Windows runners can be correlated with CPU, memory, commit, page file, and
  disk usage. See https://github.com/microsoft/aspire/issues/21570.

  -Mode Start   Launches a detached, below-normal-priority pwsh sampler and returns.
  -Mode Sample  The sampler loop itself (started by -Mode Start).
  -Mode Stop    Stops the sampler and prints a peak/minimum summary to the log and
                the job summary.

  Output goes to -OutputDirectory, which callers place under a directory that an
  existing `if: always()` upload already collects:
    resources.csv  One row per sample with system-wide counters.
    processes.log  Top processes by CPU (since the previous sample) and by memory.
    sampler.log    Sampler errors, if any.

  Every mode always exits 0: telemetry must never change the job result.
  A runner that loses communication uploads nothing, so this cannot capture the
  last minutes of a lost runner. It covers slow, failed, cancelled, and passing jobs.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Start', 'Sample', 'Stop')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateRange(5, 600)]
    [int]$IntervalSeconds = 20,

    [ValidateRange(1, 50)]
    [int]$TopProcessCount = 5
)

$ErrorActionPreference = 'Continue'
$pidFile = Join-Path $OutputDirectory 'sampler.pid'
$csvFile = Join-Path $OutputDirectory 'resources.csv'
$processFile = Join-Path $OutputDirectory 'processes.log'
$errorFile = Join-Path $OutputDirectory 'sampler.log'

$counterPaths = [ordered]@{
    CpuPercent          = '\Processor(_Total)\% Processor Time'
    ProcessorQueue      = '\System\Processor Queue Length'
    AvailableMB         = '\Memory\Available MBytes'
    CommittedPercent    = '\Memory\% Committed Bytes In Use'
    CommittedBytes      = '\Memory\Committed Bytes'
    CommitLimitBytes    = '\Memory\Commit Limit'
    PagesPerSec         = '\Memory\Pages/sec'
    PageFilePercent     = '\Paging File(_Total)\% Usage'
    DiskQueueLength     = '\PhysicalDisk(_Total)\Avg. Disk Queue Length'
    DiskPercentTime     = '\PhysicalDisk(_Total)\% Disk Time'
}

$script:loggedCounterErrors = [System.Collections.Generic.HashSet[string]]::new()

function Write-SamplerError([string]$message) {
    try { Add-Content -LiteralPath $errorFile -Value "[$([DateTime]::UtcNow.ToString('o'))] $message" } catch { }
}

function Invoke-Start {
    try {
        New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
        $arguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', "`"$PSCommandPath`"",
            '-Mode', 'Sample',
            '-OutputDirectory', "`"$OutputDirectory`"",
            '-IntervalSeconds', $IntervalSeconds,
            '-TopProcessCount', $TopProcessCount
        )
        $startArgs = @{ FilePath = 'pwsh'; ArgumentList = $arguments; PassThru = $true }
        if ($IsWindows) { $startArgs.WindowStyle = 'Hidden' }
        $process = Start-Process @startArgs
        try { $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
        Set-Content -LiteralPath $pidFile -Value $process.Id
        Write-Host "Resource monitor started (pid $($process.Id), every $IntervalSeconds s) -> $OutputDirectory"
    }
    catch {
        Write-Host "::warning::Resource monitor failed to start: $_"
    }
}

function Get-CounterValues {
    $values = @{}
    try {
        # Counters that do not exist on this machine are skipped rather than failing the sample.
        $counterErrors = $null
        $samples = (Get-Counter -Counter @($counterPaths.Values) -ErrorAction SilentlyContinue -ErrorVariable counterErrors).CounterSamples
        foreach ($counterError in @($counterErrors | Where-Object { $_ })) {
            # Log each distinct failure once instead of on every sample.
            if ($script:loggedCounterErrors.Add("$counterError")) {
                Write-SamplerError "Get-Counter error: $counterError"
            }
        }
        foreach ($entry in $counterPaths.GetEnumerator()) {
            $leaf = $entry.Value.Split('\')[-1]
            $object = $entry.Value.Split('\')[1].Split('(')[0]
            $match = $samples | Where-Object { $_.Path -like "*\$object*\$leaf" } | Select-Object -First 1
            if ($match) { $values[$entry.Key] = $match.CookedValue }
        }
    }
    catch {
        if ($script:loggedCounterErrors.Add("$_")) {
            Write-SamplerError "Get-Counter failed: $_"
        }
    }
    return $values
}

function Format-Number($value, [string]$format = 'F1') {
    if ($null -eq $value) { return '' }
    return ([double]$value).ToString($format, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Invoke-Sample {
    $logicalProcessors = [Environment]::ProcessorCount
    $columns = @('TimestampUtc', 'CpuPercent', 'ProcessorQueue', 'AvailableMB', 'CommittedPercent', 'CommittedGB', 'CommitLimitGB',
        'PagesPerSec', 'PageFilePercent', 'DiskQueueLength', 'DiskPercentTime', 'SystemDriveFreeGB', 'WorkspaceDriveFreeGB', 'ProcessCount')
    if (-not (Test-Path -LiteralPath $csvFile)) {
        Set-Content -LiteralPath $csvFile -Value ($columns -join ',')
    }

    $systemRoot = if ($env:SystemDrive) { "$($env:SystemDrive)\" } else { [System.IO.Path]::GetPathRoot($PSHOME) }
    $workspaceRoot = if ($env:GITHUB_WORKSPACE) { [System.IO.Path]::GetPathRoot($env:GITHUB_WORKSPACE) } else { $systemRoot }
    $previousCpu = @{}
    $previousTime = $null

    while ($true) {
        $sampleStart = [DateTime]::UtcNow
        try {
            $counters = Get-CounterValues

            $systemFree = $null
            $workspaceFree = $null
            try { $systemFree = [System.IO.DriveInfo]::new($systemRoot).AvailableFreeSpace / 1GB } catch { }
            try { $workspaceFree = [System.IO.DriveInfo]::new($workspaceRoot).AvailableFreeSpace / 1GB } catch { }

            $processes = @(Get-Process -ErrorAction SilentlyContinue)
            $now = [DateTime]::UtcNow
            $currentCpu = @{}
            $cpuRows = foreach ($p in $processes) {
                $total = $null
                try { $total = $p.TotalProcessorTime.TotalSeconds } catch { }
                if ($null -eq $total) { continue }
                $key = "$($p.Id):$($p.ProcessName)"
                $currentCpu[$key] = $total
                if ($null -eq $previousTime) { continue }
                $delta = $null
                if ($previousCpu.ContainsKey($key)) {
                    $delta = $total - $previousCpu[$key]
                }
                else {
                    # A process that started during this interval spent all of its CPU time in it.
                    try {
                        if ($p.StartTime.ToUniversalTime() -ge $previousTime) { $delta = $total }
                    }
                    catch { }
                }
                $elapsed = ($now - $previousTime).TotalSeconds
                if ($null -ne $delta -and $elapsed -gt 0) {
                    [pscustomobject]@{ Process = $p; Percent = 100 * $delta / $elapsed / $logicalProcessors }
                }
            }
            $previousCpu = $currentCpu
            $previousTime = $now

            $row = @(
                $sampleStart.ToString('o'),
                (Format-Number $counters.CpuPercent),
                (Format-Number $counters.ProcessorQueue 'F0'),
                (Format-Number $counters.AvailableMB 'F0'),
                (Format-Number $counters.CommittedPercent),
                (Format-Number $(if ($null -ne $counters.CommittedBytes) { $counters.CommittedBytes / 1GB }) 'F2'),
                (Format-Number $(if ($null -ne $counters.CommitLimitBytes) { $counters.CommitLimitBytes / 1GB }) 'F2'),
                (Format-Number $counters.PagesPerSec 'F0'),
                (Format-Number $counters.PageFilePercent),
                (Format-Number $counters.DiskQueueLength 'F2'),
                (Format-Number $counters.DiskPercentTime),
                (Format-Number $systemFree 'F1'),
                (Format-Number $workspaceFree 'F1'),
                $processes.Count
            )
            Add-Content -LiteralPath $csvFile -Value ($row -join ',')

            $topCpu = $cpuRows | Sort-Object Percent -Descending | Select-Object -First $TopProcessCount |
                ForEach-Object { "$($_.Process.ProcessName)[$($_.Process.Id)] $(Format-Number $_.Percent)%" }
            $topMemory = $processes | Sort-Object WorkingSet64 -Descending | Select-Object -First $TopProcessCount |
                ForEach-Object { "$($_.ProcessName)[$($_.Id)] ws=$(Format-Number ($_.WorkingSet64 / 1MB) 'F0')MB private=$(Format-Number ($_.PrivateMemorySize64 / 1MB) 'F0')MB" }
            Add-Content -LiteralPath $processFile -Value @(
                "[$($sampleStart.ToString('o'))]",
                "  cpu: $($topCpu -join '; ')",
                "  mem: $($topMemory -join '; ')"
            )
        }
        catch {
            Write-SamplerError "Sample failed: $_"
        }

        $remaining = $IntervalSeconds - ([DateTime]::UtcNow - $sampleStart).TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]($remaining * 1000)) }
    }
}

function Get-Stat([object[]]$rows, [string]$column, [string]$kind) {
    $values = @($rows | ForEach-Object { $_.$column } | Where-Object { $_ -ne '' -and $null -ne $_ } |
        ForEach-Object { [double]::Parse($_, [System.Globalization.CultureInfo]::InvariantCulture) })
    if ($values.Count -eq 0) { return 'n/a' }
    $measure = $values | Measure-Object -Maximum -Minimum -Average
    switch ($kind) {
        'max' { return Format-Number $measure.Maximum }
        'min' { return Format-Number $measure.Minimum }
        'avg' { return Format-Number $measure.Average }
    }
}

function Invoke-Stop {
    try {
        if (Test-Path -LiteralPath $pidFile) {
            $samplerPid = [int](Get-Content -LiteralPath $pidFile -Raw)
            try { [System.Diagnostics.Process]::GetProcessById($samplerPid).Kill($true) } catch { }
            Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
        }

        if (-not (Test-Path -LiteralPath $csvFile)) {
            Write-Host 'Resource monitor produced no samples.'
            return
        }

        $rows = @(Import-Csv -LiteralPath $csvFile)
        if ($rows.Count -eq 0) {
            Write-Host 'Resource monitor produced no samples.'
            return
        }
        $maxGap = 0
        for ($i = 1; $i -lt $rows.Count; $i++) {
            $gap = ([DateTime]::Parse($rows[$i].TimestampUtc, $null, 'RoundtripKind') - [DateTime]::Parse($rows[$i - 1].TimestampUtc, $null, 'RoundtripKind')).TotalSeconds
            if ($gap -gt $maxGap) { $maxGap = $gap }
        }
        # Surface collection failures: an empty core counter column means Get-Counter is not working.
        $missingCounters = @('CpuPercent', 'AvailableMB', 'CommittedPercent', 'CommittedGB', 'CommitLimitGB', 'PageFilePercent', 'DiskQueueLength' | Where-Object {
                $column = $_
                -not ($rows | Where-Object { $_.$column -ne '' -and $null -ne $_.$column })
            })
        if ($missingCounters.Count -gt 0) {
            Write-Host "::warning::Resource monitor collected no values for: $($missingCounters -join ', '). See sampler.log in the artifact."
        }
        $highCpuSamples = @($rows | Where-Object { $_.CpuPercent -ne '' -and [double]::Parse($_.CpuPercent, [System.Globalization.CultureInfo]::InvariantCulture) -ge 90 }).Count

        $summary = @(
            '### Windows runner resource monitor',
            '',
            "Samples: $($rows.Count), first $($rows[0].TimestampUtc), last $($rows[-1].TimestampUtc), largest gap between samples $(Format-Number $maxGap 'F0') s (a gap far above the interval means the machine stalled).",
            '',
            '| Metric | Average | Peak / minimum |',
            '|---|---|---|',
            "| CPU % | $(Get-Stat $rows CpuPercent avg) | max $(Get-Stat $rows CpuPercent max) ($highCpuSamples samples >= 90%) |",
            "| Processor queue length | $(Get-Stat $rows ProcessorQueue avg) | max $(Get-Stat $rows ProcessorQueue max) |",
            "| Available memory MB | $(Get-Stat $rows AvailableMB avg) | min $(Get-Stat $rows AvailableMB min) |",
            "| Commit charge % | $(Get-Stat $rows CommittedPercent avg) | max $(Get-Stat $rows CommittedPercent max) (limit $(Get-Stat $rows CommitLimitGB max) GB) |",
            "| Page file usage % | $(Get-Stat $rows PageFilePercent avg) | max $(Get-Stat $rows PageFilePercent max) |",
            "| Hard-fault paging pages/s | $(Get-Stat $rows PagesPerSec avg) | max $(Get-Stat $rows PagesPerSec max) |",
            "| Disk queue length | $(Get-Stat $rows DiskQueueLength avg) | max $(Get-Stat $rows DiskQueueLength max) |",
            "| System drive free GB | $(Get-Stat $rows SystemDriveFreeGB avg) | min $(Get-Stat $rows SystemDriveFreeGB min) |",
            "| Workspace drive free GB | $(Get-Stat $rows WorkspaceDriveFreeGB avg) | min $(Get-Stat $rows WorkspaceDriveFreeGB min) |",
            '',
            "Raw samples and top processes are in the job's diagnostics artifact under ``$(Split-Path -Leaf $OutputDirectory)``."
        )
        $summary | ForEach-Object { Write-Host $_ }
        if ($env:GITHUB_STEP_SUMMARY) {
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary
        }
    }
    catch {
        Write-Host "::warning::Resource monitor summary failed: $_"
    }
}

try {
    switch ($Mode) {
        'Start' { Invoke-Start }
        'Sample' { Invoke-Sample }
        'Stop' { Invoke-Stop }
    }
}
catch {
    Write-Host "::warning::Resource monitor $Mode failed: $_"
}
exit 0
