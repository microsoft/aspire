# Select one eligible RAW NVMe disk after host, disk and workspace safety checks,
# initialize GPT, and partition/format it as the D: ReFS Dev Drive. Redirect the
# workspace through a junction and place TEMP/TMP on D:. VerifyOnly checks the
# inherited build paths without provisioning storage.
param(
    [string]$RunnerEnvironment,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-DevDriveHost
{
    param([string]$RunnerEnvironment)

    if ($env:GITHUB_ACTIONS -ne 'true' -or $RunnerEnvironment -ne 'github-hosted' -or
        $env:OS -ne 'Windows_NT' -or $env:RUNNER_ARCH -ne 'ARM64')
    {
        throw 'Dev Drive provisioning requires a GitHub-hosted Windows ARM64 runner.'
    }
}

function Get-DevDriveDisk
{
    param(
        [object[]]$Disks,
        [bool]$DriveOccupied,
        [bool]$WorkspacePopulated,
        [bool]$WorkspaceIsLink
    )

    if ($DriveOccupied) { throw 'D: is already occupied.' }
    if ($WorkspacePopulated -or $WorkspaceIsLink)
    {
        throw 'Workspace must be empty and must not be a reparse point.'
    }

    # ARM hosted images expose an unused NVMe disk; its disk number is not stable.
    $candidates = @($Disks | Where-Object {
        $_.PartitionStyle -eq 'RAW' -and $_.BusType -eq 'NVMe' -and
        -not $_.IsBoot -and -not $_.IsSystem -and $_.Size -ge 50GB
    })
    if ($candidates.Count -ne 1)
    {
        throw "Expected one unused NVMe disk of at least 50 GiB; found $($candidates.Count)."
    }

    $disk = $candidates[0]
    # Do not trust RAW metadata alone. Enumerate all partitions rather than a
    # disk-filtered query that can report "not found" for an empty RAW disk.
    # Rejecting every partition also excludes its volume and folder/drive mounts:
    # https://learn.microsoft.com/windows-hardware/drivers/storage/msft-partition
    $partitions = @(Get-Partition | Where-Object DiskNumber -eq $disk.Number)
    if ($disk.NumberOfPartitions -ne 0 -or $partitions.Count -ne 0)
    {
        throw "Selected NVMe disk $($disk.Number) already has partitions, associated volumes or mount paths; refusing initialization."
    }

    return $disk
}

function Get-DevDriveStorageState
{
    $workspace = Get-Item -LiteralPath $env:GITHUB_WORKSPACE -Force
    return @{
        Disks = @(Get-Disk)
        DriveOccupied = (@(Get-PSDrive | Where-Object Name -eq 'D').Count -ne 0 -or
            @(Get-Volume | Where-Object DriveLetter -eq 'D').Count -ne 0)
        WorkspacePopulated = @(Get-ChildItem -LiteralPath $workspace.FullName -Force).Count -ne 0
        WorkspaceIsLink = ($workspace.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }
}

function Assert-DevDrivePlacement
{
    param(
        [object]$WorkspaceVolume,
        [int]$ExpectedDisk,
        [int]$ActualDisk,
        [string]$DevDriveQuery,
        [int]$DevDriveQueryExitCode
    )

    if ($WorkspaceVolume.DriveLetter -ne 'D' -or $WorkspaceVolume.FileSystem -ne 'ReFS')
    {
        throw 'Workspace is not on the D: ReFS volume.'
    }
    if ($ActualDisk -ne $ExpectedDisk)
    {
        throw 'Workspace volume is not on the selected NVMe disk.'
    }
    # English hosted images report "This is a trusted developer volume."
    # fsutil can also exit 0 with "This is not a developer volume."
    if ($DevDriveQueryExitCode -ne 0 -or
        $DevDriveQuery -notmatch '(?m)^This is (?:a trusted|an untrusted) developer volume\.?\s*$')
    {
        throw 'ReFS volume was not positively identified as a Dev Drive.'
    }
}

function Initialize-DevDrive
{
    param([object]$SelectedDisk)

    # Recheck the entire inventory and workspace immediately before any disk mutation.
    $state = Get-DevDriveStorageState
    $disk = Get-DevDriveDisk @state
    if ($disk.Number -ne $SelectedDisk.Number -or $disk.UniqueId -ne $SelectedDisk.UniqueId)
    {
        throw 'Disk eligibility changed before initialization.'
    }
    if ($disk.IsOffline) { $disk | Set-Disk -IsOffline $false }
    if ($disk.IsReadOnly) { $disk | Set-Disk -IsReadOnly $false }
    $partition = $disk | Initialize-Disk -PartitionStyle GPT -PassThru |
        New-Partition -DriveLetter D -UseMaximumSize
    # https://learn.microsoft.com/windows/dev-drive/#how-to-set-up-a-dev-drive
    $partition | Format-Volume -FileSystem ReFS -DevDrive -NewFileSystemLabel AspireBuild -Confirm:$false -Force | Out-Null
}

function Invoke-PrepareDevDrive
{
    param([string]$RunnerEnvironment)

    $reportDirectory = Join-Path $env:RUNNER_TEMP 'windows-arm-dev-drive'
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $report = [ordered]@{
        status = 'failed'
        sourceSha = $env:GITHUB_SHA
        runnerEnvironment = $RunnerEnvironment
        imageVersion = $env:ImageVersion
        workspace = $env:GITHUB_WORKSPACE
        runnerTemp = $env:RUNNER_TEMP
    }
    try
    {
        $report.stage = 'host validation'
        Assert-DevDriveHost -RunnerEnvironment $RunnerEnvironment
        $report.stage = 'inventory'
        $state = Get-DevDriveStorageState
        $report.disks = @($state.Disks | Select-Object Number, UniqueId, BusType, PartitionStyle, NumberOfPartitions, Size, IsBoot, IsSystem)
        $disk = Get-DevDriveDisk @state
        $report.selectedDisk = $disk.Number
        $report.stage = 'initialization'
        Initialize-DevDrive -SelectedDisk $disk

        $report.stage = 'workspace placement'
        $target = 'D:\aspire-build\workspace'
        $temp = 'D:\aspire-build\tmp'
        New-Item -ItemType Directory -Path $target, $temp | Out-Null
        Set-Location -LiteralPath $env:RUNNER_TEMP
        [Environment]::CurrentDirectory = $env:RUNNER_TEMP
        # Nonrecursive deletion fails if any files arrived since the safety check.
        [IO.Directory]::Delete($env:GITHUB_WORKSPACE)
        New-Item -ItemType Junction -Path $env:GITHUB_WORKSPACE -Target $target | Out-Null
        $marker = Join-Path $env:GITHUB_WORKSPACE 'storage-placement-probe'
        [IO.File]::WriteAllText($marker, $env:GITHUB_SHA)
        if ([IO.File]::ReadAllText((Join-Path $target 'storage-placement-probe')) -cne $env:GITHUB_SHA)
        {
            throw 'Workspace write did not reach the selected disk.'
        }
        $report.workspaceVolume = Get-Volume -FilePath $marker | Select-Object DriveLetter, FileSystem, UniqueId
        $report.workspaceDisk = (Get-Partition -DriveLetter D).DiskNumber
        $report.devDriveQuery = (& fsutil devdrv query D: 2>&1 | Out-String)
        $report.devDriveQueryExitCode = $LASTEXITCODE
        Assert-DevDrivePlacement -WorkspaceVolume $report.workspaceVolume `
            -ExpectedDisk $disk.Number -ActualDisk $report.workspaceDisk `
            -DevDriveQuery $report.devDriveQuery -DevDriveQueryExitCode $report.devDriveQueryExitCode
        Remove-Item -LiteralPath $marker
        $env:TEMP = $temp
        $env:TMP = $temp
        "TEMP=$temp", "TMP=$temp" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
        $report.temp = $temp
        $report.workspaceTarget = $target
        $report.status = 'ready'
        $report.stage = 'complete'
        Write-Output "Dev Drive ready: workspace -> $target; TEMP/TMP -> $temp"
    }
    catch
    {
        $report.error = $_.Exception.Message
        throw
    }
    finally
    {
        $report.provisioningSeconds = $timer.Elapsed.TotalSeconds
        $report | ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath (Join-Path $reportDirectory 'storage.json') -Encoding utf8
    }
}

function Assert-DevDriveBuildPath
{
    $target = 'D:\aspire-build\workspace'
    $temp = 'D:\aspire-build\tmp'
    $workspace = Get-Item -LiteralPath $env:GITHUB_WORKSPACE -Force
    if ($workspace.LinkType -ne 'Junction' -or @($workspace.Target).Count -ne 1 -or
        $workspace.Target -ne $target)
    {
        throw "Workspace must remain a junction to $target."
    }
    if ($env:TEMP -ne $temp) { throw "TEMP must be $temp; got '$env:TEMP'." }
    if ($env:TMP -ne $temp) { throw "TMP must be $temp; got '$env:TMP'." }

    # Probe the paths inherited by this later process, not setup's in-memory environment.
    foreach ($placement in @(
        @{ Path = $env:GITHUB_WORKSPACE; Target = $target },
        @{ Path = $env:TEMP; Target = $env:TMP }
    ))
    {
        $name = "dev-drive-probe-$([guid]::NewGuid())"
        $probe = Join-Path $placement.Path $name
        try
        {
            Set-Content -LiteralPath $probe -Value $name -NoNewline
            if ((Get-Content -LiteralPath (Join-Path $placement.Target $name) -Raw) -cne $name)
            {
                throw "Build path write did not reach $($placement.Target)."
            }
        }
        finally
        {
            if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe }
        }
    }
    Write-Output "Dev Drive build paths verified: workspace -> $target; TEMP/TMP -> $temp"
}

# Tests dot-source the production functions without accessing physical disks.
if ($MyInvocation.InvocationName -eq '.') { return }
if ($VerifyOnly)
{
    Assert-DevDriveBuildPath
}
else
{
    Invoke-PrepareDevDrive -RunnerEnvironment $RunnerEnvironment
}
# A diagnostic native command's exit code must not override successful verification.
exit 0
