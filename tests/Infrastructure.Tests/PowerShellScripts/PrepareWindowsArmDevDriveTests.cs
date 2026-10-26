// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class PrepareWindowsArmDevDriveTests(ITestOutputHelper output) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);
    private readonly string _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "prepare-windows-arm-dev-drive.ps1");

    private const string DiskSetup = """
        $disk = [pscustomobject]@{
            Number = 3; UniqueId = 'nvme-3'; PartitionStyle = 'RAW'; BusType = 'NVMe'
            IsBoot = $false; IsSystem = $false; Size = 50GB
            IsOffline = $false; IsReadOnly = $false; NumberOfPartitions = 0
        }
        function Get-Partition { }
        $state = @{
            Disks = @($disk); DriveOccupied = $false
            WorkspacePopulated = $false; WorkspaceIsLink = $false
        }
        """;

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SelectsOnlyEligibleDiskAtMinimumSize()
    {
        var result = await RunFunctions(DiskSetup + """

            $system = [pscustomobject]@{
                Number = 0; UniqueId = 'system'; PartitionStyle = 'GPT'; BusType = 'NVMe'
                IsBoot = $true; IsSystem = $true; Size = 200GB
            }
            $state.Disks = @($system, $disk)
            (Get-DevDriveDisk @state).Number
            """);

        result.EnsureSuccessful();
        Assert.Equal("3", result.Output.Trim());
    }

    [Theory]
    [InlineData("$state.DriveOccupied = $true", "D: is already occupied.")]
    [InlineData("$state.WorkspacePopulated = $true", "Workspace must be empty")]
    [InlineData("$state.WorkspaceIsLink = $true", "must not be a reparse point")]
    [InlineData("$state.Disks = @()", "found 0")]
    [InlineData("$state.Disks = @($disk, $disk)", "found 2")]
    [InlineData("$disk.PartitionStyle = 'GPT'", "found 0")]
    [InlineData("$disk.BusType = 'SATA'", "found 0")]
    [InlineData("$disk.IsBoot = $true", "found 0")]
    [InlineData("$disk.IsSystem = $true", "found 0")]
    [InlineData("$disk.Size = 50GB - 1", "found 0")]
    [RequiresTools(["pwsh"])]
    public async Task RejectsUnsafeStorage(string change, string error)
    {
        var result = await RunFunctions($"{DiskSetup}\n{change}\nGet-DevDriveDisk @state");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.Output);
    }

    [Theory]
    [InlineData("$state.DriveOccupied = $true", "D: is already occupied.")]
    [InlineData("$state.WorkspacePopulated = $true", "Workspace must be empty")]
    [InlineData("$state.WorkspaceIsLink = $true", "must not be a reparse point")]
    [InlineData("$state.Disks = @($disk, $disk)", "found 2")]
    [InlineData("$disk.PartitionStyle = 'GPT'", "found 0")]
    [InlineData("$disk.IsBoot = $true", "found 0")]
    [InlineData("$disk.Number = 4", "Disk eligibility changed")]
    [InlineData("$disk.UniqueId = 'replacement-disk'", "Disk eligibility changed")]
    [RequiresTools(["pwsh"])]
    public async Task RechecksSafetyBeforeInitializing(string change, string error)
    {
        var result = await RunFunctions($$"""
            {{DiskSetup}}
            $selected = $disk.PSObject.Copy()
            {{change}}
            function Get-DevDriveStorageState { return $state }
            function Initialize-Disk { throw 'Unexpected disk mutation' }
            function Set-Disk { throw 'Unexpected disk mutation' }
            Initialize-DevDrive -SelectedDisk $selected
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.Output);
    }

    [Theory]
    [InlineData("$disk.NumberOfPartitions = 1", "already has partitions")]
    [InlineData("function Get-Partition { [pscustomobject]@{ DiskNumber = 3; AccessPaths = @() } }", "already has partitions")]
    [InlineData("function Get-Partition { [pscustomobject]@{ DiskNumber = 3; AccessPaths = @('E:\\') } }", "already has partitions")]
    [InlineData("function Get-Partition { [pscustomobject]@{ DiskNumber = 3; AccessPaths = @('C:\\mounted-data\\') } }", "already has partitions")]
    [InlineData("function Get-Partition { [pscustomobject]@{ DiskNumber = 3; AccessPaths = @('\\\\?\\Volume{existing}\\') } }", "already has partitions")]
    [InlineData("function Get-Partition { Write-Error 'Partition enumeration failed' }", "Partition enumeration failed")]
    [RequiresTools(["pwsh"])]
    public async Task RefusesOccupiedRawDiskBeforeMutation(string change, string error)
    {
        var result = await RunFunctions($$"""
            {{DiskSetup}}
            function Get-DevDriveStorageState { return $state }
            $selected = Get-DevDriveDisk @state
            {{change}}
            function Initialize-Disk { throw 'Unexpected disk mutation' }
            function Set-Disk { throw 'Unexpected disk mutation' }
            Initialize-DevDrive -SelectedDisk $selected
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.Output);
    }

    [Theory]
    [InlineData("$disk.NumberOfPartitions = 1", "already has partitions")]
    [InlineData("function Get-Partition { [pscustomobject]@{ DiskNumber = 3; AccessPaths = @('C:\\mounted-data\\') } }", "already has partitions")]
    [InlineData("function Get-Partition { Write-Error 'Partition enumeration failed' }", "Partition enumeration failed")]
    [RequiresTools(["pwsh"])]
    public async Task InventoryRefusesOccupiedRawDisk(string change, string error)
    {
        var result = await RunFunctions($$"""
            {{DiskSetup}}
            $env:GITHUB_WORKSPACE = New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP 'empty-workspace')
            function Get-Disk { $disk }
            function Get-Volume { }
            {{change}}
            $inventory = Get-DevDriveStorageState
            Get-DevDriveDisk @inventory
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task EmptyRawDiskIgnoresOtherDisksPartitions()
    {
        var result = await RunFunctions(DiskSetup + """

            $env:GITHUB_WORKSPACE = New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP 'empty-workspace')
            function Get-Disk { $disk }
            function Get-Volume { }
            function Get-Partition { [pscustomobject]@{ DiskNumber = 0; AccessPaths = @('C:\') } }
            $inventory = Get-DevDriveStorageState
            (Get-DevDriveDisk @inventory).Number
            """);

        result.EnsureSuccessful();
        Assert.Equal("3", result.Output.Trim());
    }

    [Theory]
    [InlineData("GITHUB_ACTIONS", "false")]
    [InlineData("OS", "Linux")]
    [InlineData("RUNNER_ARCH", "X64")]
    [InlineData("DEV_DRIVE_RUNNER_ENVIRONMENT", "self-hosted")]
    [InlineData("DEV_DRIVE_RUNNER_ENVIRONMENT", "")]
    [RequiresTools(["pwsh"])]
    public async Task RejectsUntrustedOrInapplicableHost(string variable, string value)
    {
        var result = await RunFunctions(
            "Assert-DevDriveHost -RunnerEnvironment $env:DEV_DRIVE_RUNNER_ENVIRONMENT",
            variable, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("requires a GitHub-hosted Windows ARM64 runner", result.Output);
    }

    [Theory]
    [InlineData("pull_request", "microsoft/aspire")]
    [InlineData("push", "microsoft/aspire")]
    [InlineData("workflow_dispatch", "microsoft/aspire")]
    [RequiresTools(["pwsh"])]
    public async Task AcceptsHostedArmWithoutRestrictingCallerEvent(string eventName, string repository)
    {
        var result = await RunFunctions($$"""
            $env:GITHUB_EVENT_NAME = '{{eventName}}'
            $env:GITHUB_REPOSITORY = '{{repository}}'
            Assert-DevDriveHost -RunnerEnvironment $env:DEV_DRIVE_RUNNER_ENVIRONMENT
            """);

        result.EnsureSuccessful();
    }

    [Theory]
    [InlineData("D", "ReFS", 3, "This is a trusted developer volume.", 0, true)]
    [InlineData("D", "ReFS", 3, "This is an untrusted developer volume.", 0, true)]
    [InlineData("D", "ReFS", 3, "Volume information:\r\nThis is a trusted developer volume.\r\n", 0, true)]
    [InlineData("C", "ReFS", 3, "This is a trusted developer volume.", 0, false)]
    [InlineData("D", "NTFS", 3, "This is a trusted developer volume.", 0, false)]
    [InlineData("D", "ReFS", 4, "This is a trusted developer volume.", 0, false)]
    [InlineData("D", "ReFS", 3, "This is not a developer volume.", 0, false)]
    [InlineData("D", "ReFS", 3, "", 0, false)]
    [InlineData("D", "ReFS", 3, "Error: This is a trusted developer volume.", 0, false)]
    [InlineData("D", "ReFS", 3, "This is a trusted developer volume.", 1, false)]
    [RequiresTools(["pwsh"])]
    public async Task RequiresPositivePlacementVerification(
        string drive, string fileSystem, int actualDisk, string query, int queryExitCode, bool valid)
    {
        var result = await RunFunctions($$"""
            Assert-DevDrivePlacement `
                -WorkspaceVolume ([pscustomobject]@{ DriveLetter = '{{drive}}'; FileSystem = '{{fileSystem}}' }) `
                -ExpectedDisk 3 -ActualDisk {{actualDisk}} `
                -DevDriveQuery $env:DEV_DRIVE_QUERY -DevDriveQueryExitCode {{queryExitCode}}
            """, "DEV_DRIVE_QUERY", query);

        if (valid)
        {
            result.EnsureSuccessful();
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(actualDisk != 3 ? "selected NVMe disk" :
                drive != "D" || fileSystem != "ReFS" ? "D: ReFS volume" :
                "not positively identified as a Dev Drive", result.Output);
        }
    }

    [Theory]
    [InlineData("Initialize-Disk")]
    [InlineData("Format-Volume")]
    [RequiresTools(["pwsh"])]
    public async Task PropagatesProvisioningFailureAndRetainsDiagnostics(string failingCommand)
    {
        var result = await RunFunctions($$"""
            {{DiskSetup}}
            function Get-DevDriveStorageState { return $state }
            function Initialize-Disk { $disk }
            function New-Partition { $disk }
            function {{failingCommand}} { throw 'Storage module operation failed' }
            Invoke-PrepareDevDrive -RunnerEnvironment $env:DEV_DRIVE_RUNNER_ENVIRONMENT
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Storage module operation failed", result.Output);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_workspace.Path, "windows-arm-dev-drive", "storage.json")));
        Assert.Equal("failed", report.RootElement.GetProperty("status").GetString());
        Assert.Equal("initialization", report.RootElement.GetProperty("stage").GetString());
        Assert.Equal("Storage module operation failed", report.RootElement.GetProperty("error").GetString());
        Assert.Equal(3, report.RootElement.GetProperty("selectedDisk").GetInt32());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task EntryPointRefusesSelfHostedRunnerAndRetainsDiagnostics()
    {
        using var command = CreateCommand(_scriptPath)
            .WithEnvironmentVariable("DEV_DRIVE_RUNNER_ENVIRONMENT", "self-hosted");
        var result = await command.ExecuteAsync("-RunnerEnvironment", "self-hosted");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("requires a GitHub-hosted Windows ARM64 runner", result.Output);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_workspace.Path, "windows-arm-dev-drive", "storage.json")));
        Assert.Equal("failed", report.RootElement.GetProperty("status").GetString());
        Assert.Equal("host validation", report.RootElement.GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("$env:TEMP = 'C:\\fallback'", "TEMP must be")]
    [InlineData("$env:TMP = ''", "TMP must be")]
    [InlineData("$env:TMP = 'C:\\fallback'", "TMP must be")]
    [InlineData("$global:linkType = 'SymbolicLink'", "Workspace must remain a junction")]
    [InlineData("$global:linkTarget = 'C:\\fallback'", "Workspace must remain a junction")]
    [InlineData("$env:GITHUB_WORKSPACE = Join-Path $env:RUNNER_TEMP 'missing'", "Cannot find path")]
    [InlineData("$env:GITHUB_WORKSPACE = $env:RUNNER_TEMP", "Cannot find path")]
    [InlineData("$global:denyTempWrite = $true", "Temporary directory is not writable")]
    [RequiresTools(["pwsh"])]
    public async Task VerificationEntryPointChecksInheritedBuildPaths(string change, string error)
    {
        var result = await RunFunctions($$"""
            # Use a process-local provider drive and real file I/O, without touching physical disks.
            Remove-PSDrive D -ErrorAction SilentlyContinue
            New-PSDrive -Name D -PSProvider FileSystem -Root $env:RUNNER_TEMP | Out-Null
            New-Item -ItemType Directory -Path D:\aspire-build\workspace, D:\aspire-build\tmp | Out-Null
            $env:GITHUB_WORKSPACE = 'D:\aspire-build\workspace'
            $env:TEMP = 'D:\aspire-build\tmp'
            $env:TMP = 'D:\aspire-build\tmp'
            $global:linkType = 'Junction'
            $global:linkTarget = 'D:\aspire-build\workspace'
            $global:denyTempWrite = $false
            # Junction metadata is Windows-specific; writes and cleanup still use the real provider.
            function Get-Item {
                param([string]$LiteralPath, [switch]$Force)
                Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force | Out-Null
                return [pscustomobject]@{ LinkType = $global:linkType; Target = $global:linkTarget }
            }
            function Set-Content {
                param([string]$LiteralPath, [string]$Value, [switch]$NoNewline)
                if ($global:denyTempWrite -and $LiteralPath.Replace('\', '/').StartsWith($env:TEMP.Replace('\', '/'))) {
                    throw [UnauthorizedAccessException]::new('Temporary directory is not writable')
                }
                Microsoft.PowerShell.Management\Set-Content -LiteralPath $LiteralPath -Value $Value -NoNewline:$NoNewline
            }
            function Get-Disk { throw 'Verification must not access disk provisioning' }
            function Initialize-Disk { throw 'Verification must not initialize disks' }
            {{change}}
            & $env:DEV_DRIVE_SCRIPT -VerifyOnly
            """);

        if (change.Length == 0)
        {
            result.EnsureSuccessful();
            Assert.Contains("Dev Drive build paths verified", result.Output);
            Assert.Empty(Directory.GetFiles(Path.Combine(_workspace.Path, "aspire-build", "workspace")));
            Assert.Empty(Directory.GetFiles(Path.Combine(_workspace.Path, "aspire-build", "tmp")));
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(error, result.Output);
        }
    }

    private async Task<CommandResult> RunFunctions(string body, string? variable = null, string? value = null)
    {
        var harness = Path.Combine(_workspace.Path, "harness.ps1");
        File.WriteAllText(harness, ". $env:DEV_DRIVE_SCRIPT\n" + body);
        using var command = CreateCommand(harness);
        if (variable is not null)
        {
            command.WithEnvironmentVariable(variable, value!);
        }
        return await command.ExecuteAsync();
    }

    private PowerShellCommand CreateCommand(string scriptPath) =>
        new PowerShellCommand(scriptPath, output)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("DEV_DRIVE_SCRIPT", _scriptPath)
            .WithEnvironmentVariable("GITHUB_ACTIONS", "true")
            .WithEnvironmentVariable("OS", "Windows_NT")
            .WithEnvironmentVariable("RUNNER_ARCH", "ARM64")
            .WithEnvironmentVariable("DEV_DRIVE_RUNNER_ENVIRONMENT", "github-hosted")
            .WithEnvironmentVariable("RUNNER_TEMP", _workspace.Path);
}
