#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Download and unpack the Aspire CLI from a specific PR's build artifacts

.DESCRIPTION
    Downloads and installs the Aspire CLI from a specific pull request's latest successful build.
    Automatically detects the current platform (OS and architecture) and downloads the appropriate artifact.

    The script queries the GitHub API to find the latest successful run of the 'ci.yml' workflow
    for the specified PR, then downloads and extracts the CLI archive for your platform using 'gh run download'.

    Alternatively, you can specify a workflow run ID directly to download from a specific build.

.PARAMETER PRNumber
    Pull request number (required)

.PARAMETER WorkflowRunId
    Workflow run ID to download from (optional)

.PARAMETER LocalDir
    Use pre-downloaded artifacts from a local directory instead of downloading from GitHub.
    Mutually exclusive with PRNumber and WorkflowRunId.

.PARAMETER HiveLabel
    Override the NuGet hive label (default: pr-PRNUMBER, run-RUNID, or run-GITHUB_RUN_ID for LocalDir).

.PARAMETER InstallPath
    Directory prefix to install (default: $HOME/.aspire on Unix, %USERPROFILE%\.aspire on Windows)
    CLI will be installed to InstallPath\bin (or InstallPath/bin on Unix)
    NuGet packages will be installed to InstallPath\hives\pr-PRNUMBER\packages

.PARAMETER OS
    Override OS detection (win, linux, linux-musl, osx)

.PARAMETER Architecture
    Override architecture detection (x64, arm64)

.PARAMETER HiveOnly
    Only install NuGet packages to the hive, skip CLI download

.PARAMETER SkipPath
    Do not add the install path to PATH environment variable (useful for portable installs)

.PARAMETER KeepArchive
    Keep downloaded archive files after installation

.PARAMETER Help
    Show this help message

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -WorkflowRunId 12345678

.EXAMPLE
    .\get-aspire-cli-pr.ps1 -LocalDir "C:\path\to\artifacts"

.EXAMPLE
    .\get-aspire-cli-pr.ps1 -LocalDir "C:\path\to\artifacts" -HiveLabel my-build

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -InstallPath "C:\my-aspire"

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -OS linux -Architecture arm64 -Verbose

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -HiveOnly

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -WhatIf

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -SkipExtension

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -UseInsiders

.EXAMPLE
    .\get-aspire-cli-pr.ps1 1234 -SkipPath

.EXAMPLE
    Piped execution
    iex "& { $(irm https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.ps1) } <PR_NUMBER>

.NOTES
    Requires GitHub CLI (gh) to be installed and authenticated
    Requires appropriate permissions to download artifacts from target repository
    VS Code extension installation requires VS Code CLI (code) to be available in PATH

.PARAMETER ASPIRE_REPO (environment variable)
    Override repository (owner/name). Default: microsoft/aspire
    Example: $env:ASPIRE_REPO = 'myfork/aspire'
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position = 0, HelpMessage = "Pull request number")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$PRNumber,

    [Parameter(HelpMessage = "Workflow run ID to download from")]
    [ValidateRange(1, [long]::MaxValue)]
    [long]$WorkflowRunId,

    [Parameter(HelpMessage = "Use pre-downloaded artifacts from a local directory instead of downloading from GitHub")]
    [string]$LocalDir = "",

    [Parameter(HelpMessage = "Override the NuGet hive label (default: pr-<PR>, run-<RUN_ID>, or run-<GITHUB_RUN_ID> (run-local when GITHUB_RUN_ID is unset))")]
    [string]$HiveLabel = "",

    [Parameter(HelpMessage = "Directory prefix to install")]
    [string]$InstallPath = "",

    [Parameter(HelpMessage = "Override OS detection")]
    [ValidateSet("", "win", "linux", "linux-musl", "osx")]
    [string]$OS = "",

    [Parameter(HelpMessage = "Override architecture detection")]
    [ValidateSet("", "x64", "arm64")]
    [string]$Architecture = "",

    [Parameter(HelpMessage = "Only install NuGet packages to the hive, skip CLI download")]
    [switch]$HiveOnly,

    [Parameter(HelpMessage = "Skip VS Code extension download and installation")]
    [switch]$SkipExtension,

    [Parameter(HelpMessage = "Install extension to VS Code Insiders instead of VS Code")]
    [switch]$UseInsiders,

    [Parameter(HelpMessage = "Do not add the install path to PATH environment variable (useful for portable installs)")]
    [switch]$SkipPath,

    [Parameter(HelpMessage = "Keep downloaded archive files after installation")]
    [switch]$KeepArchive,

    [Parameter(HelpMessage = "Show help information")]
    [switch]$Help
)

# Global constants
$Script:BuiltNugetsArtifactName = "built-nugets"
$Script:BuiltNugetsRidArtifactName = "built-nugets-for"
$Script:CliArchiveArtifactNamePrefix = "cli-native-archives"
$Script:AspireCliArtifactNamePrefix = "aspire-cli"
$Script:ExtensionArtifactName = "aspire-extension"
$Script:IsModernPowerShell = $PSVersionTable.PSVersion.Major -ge 6 -and $PSVersionTable.PSEdition -eq "Core"
$Script:HostOS = "unset"
$Script:Repository = if ($env:ASPIRE_REPO -and $env:ASPIRE_REPO.Trim()) { $env:ASPIRE_REPO.Trim() } else { 'microsoft/aspire' }
$Script:GHReposBase = "repos/$($Script:Repository)"

# True if the script is executed from a file (pwsh -File … or .\get-aspire-cli-pr.ps1)
# False if the body is piped / dot‑sourced / iex'd into the current session.
$InvokedFromFile = -not [string]::IsNullOrEmpty($PSCommandPath)

# =============================================================================
# START: Shared code
# =============================================================================

# Consolidated output function with fallback for platforms that don't support Write-Host
function Write-Message {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Message,

        [Parameter()]
        [ValidateSet("Verbose", "Info", "Success", "Warning", "Error")]
        [string]$Level = "Info"
    )

    $hasWriteHost = Get-Command Write-Host -ErrorAction SilentlyContinue

    switch ($Level) {
        "Verbose" {
            if ($VerbosePreference -ne "SilentlyContinue") {
                Write-Verbose $Message
            }
        }
        "Info" {
            if ($hasWriteHost) {
                Write-Host $Message -ForegroundColor White
            } else {
                Write-Output $Message
            }
        }
        "Success" {
            if ($hasWriteHost) {
                Write-Host $Message -ForegroundColor Green
            } else {
                Write-Output "SUCCESS: $Message"
            }
        }
        "Warning" {
            Write-Warning $Message
        }
        "Error" {
            Write-Error $Message
        }
    }
}

# Helper function for PowerShell version-specific operations
function Invoke-WithPowerShellVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$ModernAction,

        [Parameter(Mandatory = $true)]
        [scriptblock]$LegacyAction
    )

    if ($Script:IsModernPowerShell) {
        & $ModernAction
    } else {
        & $LegacyAction
    }
}

# Function to detect OS
function Get-OperatingSystem {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    Write-Message "Detecting OS" -Level Verbose
    try {
        return Invoke-WithPowerShellVersion -ModernAction {
            if ($IsWindows) {
                return "win"
            }
            elseif ($IsLinux) {
                try {
                    $lddOutput = & ldd --version 2>&1 | Out-String
                    return if ($lddOutput -match "musl") { "linux-musl" } else { "linux" }
                }
                catch { return "linux" }
            }
            elseif ($IsMacOS) {
                return "osx"
            }
            else {
                return "unsupported"
            }
        } -LegacyAction {
            # PowerShell 5.1 and earlier - more reliable Windows detection
            if ($env:OS -eq "Windows_NT" -or [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
                return "win"
            }

            $platform = [System.Environment]::OSVersion.Platform
            switch ($platform) {
                { $_ -in @([System.PlatformID]::Unix, 4, 6) } { return "linux" }
                { $_ -in @([System.PlatformID]::MacOSX, 128) } { return "osx" }
                default { return "unsupported" }
            }
        }
    }
    catch {
        Write-Message "Failed to detect operating system: $($_.Exception.Message)" -Level Warning
        return "unsupported"
    }
}

# Enhanced function for cross-platform architecture detection
function Get-MachineArchitecture {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    Write-Message "Detecting machine architecture" -Level Verbose

    try {
        # On Windows PowerShell, use environment variables
        if (-not $Script:IsModernPowerShell -or $IsWindows) {
            # On PS x86, PROCESSOR_ARCHITECTURE reports x86 even on x64 systems.
            # To get the correct architecture, we need to use PROCESSOR_ARCHITEW6432.
            # PS x64 doesn't define this, so we fall back to PROCESSOR_ARCHITECTURE.
            # Possible values: amd64, x64, x86, arm64, arm
            if ( $null -ne $ENV:PROCESSOR_ARCHITEW6432 ) {
                return $ENV:PROCESSOR_ARCHITEW6432
            }

            try {
                $osInfo = Get-CimInstance -ClassName CIM_OperatingSystem -ErrorAction Stop
                if ($osInfo.OSArchitecture -like "ARM*") {
                    if ([Environment]::Is64BitOperatingSystem) {
                        return "arm64"
                    }
                    return "arm"
                }
            }
            catch {
                Write-Message "Failed to get CIM instance: $($_.Exception.Message)" -Level Verbose
            }

            if ( $null -ne $ENV:PROCESSOR_ARCHITECTURE ) {
                return $ENV:PROCESSOR_ARCHITECTURE
            }
        }

        # For PowerShell 6+ on Unix systems, use .NET runtime information
        if ($Script:IsModernPowerShell) {
            try {
                $runtimeArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
                switch ($runtimeArch) {
                    "X64" { return "x64" }
                    "Arm64" { return "arm64" }
                    default {
                        Write-Message "Unknown runtime architecture: $runtimeArch" -Level Verbose
                        # Fall back to uname if available
                        if (Get-Command uname -ErrorAction SilentlyContinue) {
                            $unameArch = & uname -m
                            switch ($unameArch) {
                                { @("x86_64", "amd64") -contains $_ } { return "x64" }
                                { @("aarch64", "arm64") -contains $_ } { return "arm64" }
                                default {
                                    throw "Architecture '$unameArch' not supported. If you think this is a bug, report it at https://github.com/microsoft/aspire/issues"
                                }
                            }
                        } else {
                            throw "Architecture '$runtimeArch' not supported (uname unavailable). If you think this is a bug, report it at https://github.com/microsoft/aspire/issues"
                        }
                    }
                }
            }
            catch {
                throw "Architecture detection failed: $($_.Exception.Message)"
            }
        }

        throw "Architecture detection failed (no supported detection path). If you think this is a bug, report it at https://github.com/microsoft/aspire/issues"
    }
    catch {
        throw "Architecture detection failed: $($_.Exception.Message)"
    }
}

# Convert architecture to CLI architecture format
function Get-CLIArchitectureFromArchitecture {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Architecture
    )

    if ($Architecture -eq "<auto>") {
        $Architecture = Get-MachineArchitecture
    }

    $normalizedArch = $Architecture.ToLowerInvariant()
    switch ($normalizedArch) {
        { @("amd64", "x64") -contains $_ } {
            return "x64"
        }
        { $_ -eq "arm64" } {
            return "arm64"
        }
        default {
            throw "Architecture '$Architecture' not supported. If you think this is a bug, report it at https://github.com/microsoft/aspire/issues"
        }
    }
}

function Get-RuntimeIdentifier {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [string]$_OS,
        [string]$_Architecture
    )

    # Determine OS and architecture (either detected or user-specified)
    $computedTargetOS = if ([string]::IsNullOrWhiteSpace($_OS)) { $Script:HostOS } else { $_OS }

    # Check for unsupported OS
    if ($computedTargetOS -eq "unsupported") {
        throw "Unsupported operating system. Current platform: $([System.Environment]::OSVersion.Platform)"
    }

    $computedTargetArch = if ([string]::IsNullOrWhiteSpace($_Architecture)) { Get-CLIArchitectureFromArchitecture "<auto>" } else { Get-CLIArchitectureFromArchitecture $_Architecture }

    return "${computedTargetOS}-${computedTargetArch}"
}

# Function to get the CLI executable path based on host OS
function Get-CliExecutablePath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $exeName = if ($Script:HostOS -eq "win") { "aspire.exe" } else { "aspire" }
    return Join-Path $DestinationPath $exeName
}

# Function to back up an existing CLI executable before overwriting it.
# This matches self-update semantics by deleting stale *.old.* backups first.
# On Windows, a running process can still block the rename.
function Backup-ExistingCliExecutable {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetExePath
    )
    
    if (Test-Path $TargetExePath) {
        $unixTimestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $backupPath = "$TargetExePath.old.$unixTimestamp"
        
        if ($PSCmdlet.ShouldProcess($TargetExePath, "Backup to $backupPath")) {
            Write-Message "Backing up existing CLI: $TargetExePath -> $backupPath" -Level Verbose

            Remove-OldCliBackupFiles -TargetExePath $TargetExePath

            # Rename existing executable to .old.[timestamp]
            try {
                Move-Item -Path $TargetExePath -Destination $backupPath -Force -ErrorAction Stop
            }
            catch {
                throw "Failed to back up existing CLI at '$TargetExePath'. The file may be in use by another process. Please close any running Aspire CLI instances and try again. Error: $($_.Exception.Message)"
            }
            return $backupPath
        }
    }
    
    return $null
}

# Function to restore CLI executable from backup if installation fails
function Restore-CliExecutableFromBackup {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BackupPath,
        
        [Parameter(Mandatory = $true)]
        [string]$TargetExePath
    )
    
    if ($PSCmdlet.ShouldProcess($BackupPath, "Restore to $TargetExePath")) {
        Write-Message "Restoring CLI from backup: $BackupPath -> $TargetExePath" -Level Warning
        
        if (Test-Path $TargetExePath) {
            Remove-Item -Path $TargetExePath -Force -ErrorAction SilentlyContinue
        }
        
        Move-Item -Path $BackupPath -Destination $TargetExePath -Force -ErrorAction Stop
    }
}

# Function to clean up old backup files (aspire.exe.old.* or aspire.old.*)
function Remove-OldCliBackupFiles {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetExePath
    )
    
    $directory = Split-Path -Parent $TargetExePath
    if ([string]::IsNullOrEmpty($directory)) {
        return
    }
    
    $exeName = Split-Path -Leaf $TargetExePath
    $searchPattern = "$exeName.old.*"
    
    $oldBackupFiles = Get-ChildItem -Path $directory -Filter $searchPattern -ErrorAction SilentlyContinue
    foreach ($backupFile in $oldBackupFiles) {
        if ($PSCmdlet.ShouldProcess($backupFile.FullName, "Delete old backup")) {
            try {
                Remove-Item -Path $backupFile.FullName -Force -ErrorAction Stop
                Write-Message "Deleted old backup file: $($backupFile.FullName)" -Level Verbose
            }
            catch {
                Write-Message "Failed to delete old backup file: $($backupFile.FullName) - $($_.Exception.Message)" -Level Verbose
            }
        }
    }
}

function Expand-AspireCliArchive {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string]$ArchiveFile,
        [string]$DestinationPath
    )

    if (-not $PSCmdlet.ShouldProcess($DestinationPath, "Expand archive $ArchiveFile to $DestinationPath")) {
        return
    }

    Write-Message "Unpacking archive to: $DestinationPath" -Level Verbose

    # Get the target executable path using shared function
    $targetExePath = Get-CliExecutablePath -DestinationPath $DestinationPath
    $backupPath = $null

    try {
        # Create destination directory if it doesn't exist
        if (-not (Test-Path $DestinationPath)) {
            Write-Message "Creating destination directory: $DestinationPath" -Level Verbose
            New-Item -ItemType Directory -Path $DestinationPath -Force -ErrorAction Stop | Out-Null
        }
        else {
            # Back up the existing executable before extraction.
            # On Windows, this can still fail if the file is locked by a running process.
            $backupPath = Backup-ExistingCliExecutable -TargetExePath $targetExePath
        }

        Write-Message "Extracting archive: $ArchiveFile" -Level Verbose
        # Check archive format based on file extension and extract accordingly
        if ($ArchiveFile -match "\.zip$") {
            # Use Expand-Archive for ZIP files
            if (-not (Get-Command Expand-Archive -ErrorAction SilentlyContinue)) {
                throw "Expand-Archive cmdlet not found. Please use PowerShell 5.0 or later to extract ZIP files."
            }

            Expand-Archive -Path $ArchiveFile -DestinationPath $DestinationPath -Force -ErrorAction Stop
        }
        elseif ($ArchiveFile -match "\.tar\.gz$") {
            # Use tar for tar.gz files
            if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
                throw "tar command not found. Please install tar to extract tar.gz files."
            }

            $currentLocation = Get-Location
            try {
                Set-Location $DestinationPath
                $tarOutput = & tar -xzf $ArchiveFile 2>&1
                if ($LASTEXITCODE -ne 0) {
                    $tarMessage = ($tarOutput | ForEach-Object { $_.ToString() } | Out-String).Trim()
                    if ([string]::IsNullOrWhiteSpace($tarMessage)) {
                        throw "Failed to extract tar.gz archive: $ArchiveFile. tar command returned exit code $LASTEXITCODE"
                    }

                    throw "Failed to extract tar.gz archive: $ArchiveFile. tar command returned exit code $LASTEXITCODE`: $tarMessage"
                }
            }
            finally {
                Set-Location $currentLocation
            }
        }
        else {
            throw "Unsupported archive format: $ArchiveFile. Only .zip and .tar.gz files are supported."
        }

        # Clean up old backup files on successful extraction
        if (Test-Path $targetExePath) {
            Remove-OldCliBackupFiles -TargetExePath $targetExePath
        }

        Write-Message "Successfully unpacked archive" -Level Verbose
    }
    catch {
        $unpackErrorMessage = $_.Exception.Message

        # If anything goes wrong and we have a backup, restore it
        if ($backupPath -and (Test-Path $backupPath)) {
            try {
                Restore-CliExecutableFromBackup -BackupPath $backupPath -TargetExePath $targetExePath
            }
            catch {
                throw "Failed to unpack archive: $unpackErrorMessage. Restore from backup also failed: $($_.Exception.Message)"
            }
        }

        throw "Failed to unpack archive: $unpackErrorMessage"
    }
}

# Simplified installation path determination
function Get-DefaultInstallPrefix {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    # Get home directory cross-platform
    $homeDirectory = Invoke-WithPowerShellVersion -ModernAction {
        if ($env:HOME) {
            $env:HOME
        } elseif ($IsWindows -and $env:USERPROFILE) {
            $env:USERPROFILE
        } elseif ($env:USERPROFILE) {
            $env:USERPROFILE
        } else {
            $null
        }
    } -LegacyAction {
        if ($env:USERPROFILE) {
            $env:USERPROFILE
        } elseif ($env:HOME) {
            $env:HOME
        } else {
            $null
        }
    }

    if ([string]::IsNullOrWhiteSpace($homeDirectory)) {
        throw "Unable to determine user home directory. Please specify -InstallPath parameter."
    }

    $defaultPath = Join-Path $homeDirectory ".aspire"
    return [System.IO.Path]::GetFullPath($defaultPath)
}

# Simplified PATH environment update
function Update-PathEnvironment {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$CliBinDir
    )

    $pathSeparator = [System.IO.Path]::PathSeparator

    # Update current session PATH
    $currentPathArray = $env:PATH.Split($pathSeparator, [StringSplitOptions]::RemoveEmptyEntries)
    if ($currentPathArray -notcontains $CliBinDir) {
        if ($PSCmdlet.ShouldProcess("PATH environment variable", "Add $CliBinDir to current session")) {
            $env:PATH = (@($CliBinDir) + $currentPathArray) -join $pathSeparator
            Write-Message "Added $CliBinDir to PATH for current session" -Level Info
        }
    }

    # Update persistent PATH for Windows
    if ($Script:HostOS -eq "win") {
        try {
            $userPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::User)
            if (-not $userPath) { $userPath = "" }
            $userPathArray = if ($userPath) { $userPath.Split($pathSeparator, [StringSplitOptions]::RemoveEmptyEntries) } else { @() }
            if ($userPathArray -notcontains $CliBinDir) {
                if ($PSCmdlet.ShouldProcess("User PATH environment variable", "Add $CliBinDir")) {
                    $newUserPath = (@($CliBinDir) + $userPathArray) -join $pathSeparator
                    [Environment]::SetEnvironmentVariable("PATH", $newUserPath, [EnvironmentVariableTarget]::User)
                    Write-Message "Added $CliBinDir to user PATH environment variable" -Level Info
                }
            }

            Write-Message "" -Level Info
            Write-Message "The aspire cli is now available for use in this and new sessions." -Level Success
        }
        catch {
            Write-Message "Failed to update persistent PATH environment variable: $($_.Exception.Message)" -Level Warning
            Write-Message "You may need to manually add $CliBinDir to your PATH environment variable" -Level Info
        }
    }

    # GitHub Actions support
    if ($env:GITHUB_ACTIONS -eq "true" -and $env:GITHUB_PATH) {
        try {
            if ($PSCmdlet.ShouldProcess("GITHUB_PATH environment variable", "Add $CliBinDir to GITHUB_PATH")) {
                Add-Content -Path $env:GITHUB_PATH -Value $CliBinDir
                Write-Message "Added $CliBinDir to GITHUB_PATH for GitHub Actions" -Level Success
            }
        }
        catch {
            Write-Message "Failed to update GITHUB_PATH: $($_.Exception.Message)" -Level Warning
        }
    }
}

# Function to create a temporary directory with conflict resolution
function New-TempDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Prefix
    )

    if ($PSCmdlet.ShouldProcess("temporary directory", "Create temporary directory with prefix '$Prefix'")) {
        # Create a temporary directory for downloads with conflict resolution
        $tempBaseName = "$Prefix-$([System.Guid]::NewGuid().ToString("N").Substring(0, 8))"
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) $tempBaseName

        # Handle potential conflicts
        $attempt = 1
        while (Test-Path $tempDir) {
            $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "$tempBaseName-$attempt"
            $attempt++
            if ($attempt -gt 10) {
                throw "Unable to create temporary directory after 10 attempts"
            }
        }

        Write-Message "Creating temporary directory: $tempDir" -Level Verbose
        try {
            New-Item -ItemType Directory -Path $tempDir -Force -ErrorAction Stop | Out-Null
            return $tempDir
        }
        catch {
            throw "Failed to create temporary directory: $tempDir - $($_.Exception.Message)"
        }
    }
    else {
        # Return a WhatIf path when -WhatIf is used
        return Join-Path ([System.IO.Path]::GetTempPath()) "$Prefix-whatif"
    }
}

# Cleanup function for temporary directory
function Remove-TempDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter()]
        [string]$TempDir
    )

    if (-not [string]::IsNullOrWhiteSpace($TempDir) -and (Test-Path $TempDir)) {
        if (-not $KeepArchive) {
            Write-Message "Cleaning up temporary files..." -Level Verbose
            try {
                if ($PSCmdlet.ShouldProcess($TempDir, "Remove temporary directory")) {
                    Remove-Item $TempDir -Recurse -Force -ErrorAction Stop
                }
            }
            catch {
                Write-Message "Failed to clean up temporary directory: $TempDir - $($_.Exception.Message)" -Level Warning
            }
        }
        else {
            Write-Message "Archive files kept in: $TempDir" -Level Info
        }
    }
}

# =============================================================================
# END: Shared code
# =============================================================================

# Function to save global settings using the aspire CLI
# Uses 'aspire config set -g' to set global configuration values
# Expected schema of ~/.aspire/globalsettings.json:
# {
#   "channel": "string"  // The channel name (e.g., "daily", "staging", "pr-1234")
# }
function Save-GlobalSettings {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$CliPath,
        
        [Parameter(Mandatory = $true)]
        [string]$Key,
        
        [Parameter(Mandatory = $true)]
        [string]$Value
    )
    
    if ($PSCmdlet.ShouldProcess("$Key = $Value", "Set global config via aspire CLI")) {
        Write-Message "Setting global config: $Key = $Value" -Level Verbose
        
        $output = & $CliPath config set -g $Key $Value 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Message "Failed to set global config via aspire CLI" -Level Warning
            return
        }
        Write-Message "Global config saved: $Key = $Value" -Level Verbose
    }
}

# Function to check if gh command is available
function Test-GitHubCLIDependency {
    [CmdletBinding()]
    param()

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Message "GitHub CLI (gh) is required but not installed. Please install it first." -Level Error
        Write-Message "Installation instructions: https://cli.github.com/" -Level Info
        throw "GitHub CLI (gh) dependency not met"
    }

    $ghVersion = & gh --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI (gh) command failed with exit code $LASTEXITCODE`: $ghVersion"
    } else {
        $firstLine = ($ghVersion | Select-Object -First 1)
        Write-Message "GitHub CLI (gh) found: $firstLine" -Level Verbose
    }
}

# Function to check VS Code CLI dependency
function Test-VSCodeCLIDependency {
    [CmdletBinding()]
    param(
        [switch]$UseInsiders
    )

    $vscodeCmd = if ($UseInsiders) { "code-insiders" } else { "code" }
    $vscodeName = if ($UseInsiders) { "VS Code Insiders" } else { "VS Code" }

    if (-not (Get-Command $vscodeCmd -ErrorAction SilentlyContinue)) {
        Write-Message "$vscodeName CLI ($vscodeCmd) is not available in PATH. Extension installation will be skipped." -Level Warning
        Write-Message "To install $vscodeName extensions, ensure $vscodeName is installed and the '$vscodeCmd' command is available." -Level Info
        return $false
    }

    Write-Message "$vscodeName CLI ($vscodeCmd) found" -Level Verbose
    return $true
}

# Simplified installation path determination
function Get-InstallPrefix {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter()]
        [string]$InstallPrefix
    )

    if (-not [string]::IsNullOrWhiteSpace($InstallPrefix)) {
        # Validate that the path is not just whitespace and can be created
        try {
            $resolvedPath = [System.IO.Path]::GetFullPath($InstallPrefix)
            return $resolvedPath
        }
        catch {
            throw "Invalid installation path: $InstallPrefix - $($_.Exception.Message)"
        }
    }

    return Get-DefaultInstallPrefix
}

# Function to make GitHub API calls with proper error handling
function Invoke-GitHubAPICall {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Endpoint,

        [Parameter()]
        [string]$JqFilter = "",

        [Parameter()]
        [string]$ErrorMessage = "Failed to call GitHub API"
    )

    $ghCommand = @("gh", "api", $Endpoint)

    if (-not [string]::IsNullOrWhiteSpace($JqFilter)) {
        $ghCommand += @("--jq", $JqFilter)
    }

    Write-Message "Calling GitHub API: $($ghCommand -join ' ')" -Level Verbose

    $output = & $ghCommand[0] $ghCommand[1..($ghCommand.Length-1)] 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "$ErrorMessage (API endpoint: $Endpoint): $output"
    }

    return $output
}

# Function to get PR head SHA
function Get-PRHeadSHA {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$PRNumber
    )

    Write-Message "Getting HEAD SHA for PR #$PRNumber" -Level Verbose

    if ($Script:Repository -notmatch '^([^/]+)/([^/]+)$') {
        throw "Invalid repository format '$Script:Repository'. Expected 'owner/name'."
    }
    $owner = $Matches[1]
    $name = $Matches[2]

    $graphqlQuery = 'query($owner:String!, $name:String!, $number:Int!) { repository(owner:$owner, name:$name) { pullRequest(number:$number) { headRefOid } } }'
    $ghCommand = @(
        "gh", "api", "graphql",
        "-f", "query=$graphqlQuery",
        "-f", "owner=$owner",
        "-f", "name=$name",
        "-F", "number=$PRNumber",
        "--jq", ".data.repository.pullRequest.headRefOid"
    )

    Write-Message "Calling GitHub API: $($ghCommand -join ' ')" -Level Verbose

    $graphQlError = $null
    try {
        $headSha = & $ghCommand[0] $ghCommand[1..($ghCommand.Length-1)] 2>$null
        if ($LASTEXITCODE -ne 0) {
            $graphQlError = "gh exited with code $LASTEXITCODE"
        } elseif ([string]::IsNullOrWhiteSpace($headSha) -or $headSha -eq "null") {
            $graphQlError = "GraphQL returned empty or null result"
        } else {
            # Normalize to a single trimmed string in case of unexpected multi-line output
            $headSha = ($headSha | Select-Object -First 1).Trim()
        }
    }
    catch {
        $graphQlError = $_.Exception.Message
    }

    if ($graphQlError) {
        Write-Message "GraphQL PR head lookup failed, falling back to REST API: $graphQlError" -Level Verbose
        try {
            $headSha = Invoke-GitHubAPICall -Endpoint "$Script:GHReposBase/pulls/$PRNumber" -JqFilter ".head.sha" -ErrorMessage "Failed to get HEAD SHA for PR #$PRNumber using REST fallback"
        }
        catch {
            throw "Failed to get HEAD SHA for PR #$PRNumber with GraphQL query: $graphQlError`nREST fallback error: $($_.Exception.Message)"
        }
    }

    if ([string]::IsNullOrWhiteSpace($headSha) -or $headSha -eq "null") {
        Write-Message "This could mean:" -Level Info
        Write-Message "  - The PR number does not exist" -Level Info
        Write-Message "  - You don't have access to the repository" -Level Info
        throw "Could not retrieve HEAD SHA for PR #$PRNumber"
    }

    Write-Message "PR #$PRNumber HEAD SHA: $headSha" -Level Verbose
    return $headSha.Trim()
}

# Function to extract version suffix from downloaded NuGet packages
function Get-VersionSuffixFromPackages {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DownloadDir
    )
    
    if ($PSCmdlet.ShouldProcess("packages", "Extract version suffix from packages") -and $WhatIfPreference) {
        # Return a mock version for WhatIf
        return "pr.1234.a1b2c3d4"
    }
    
    # Look for any .nupkg file and extract version from its name
    $nupkgFiles = Get-ChildItem -Path $DownloadDir -Filter "*.nupkg" -Recurse | Select-Object -First 1
    
    if (-not $nupkgFiles) {
        Write-Message "No .nupkg files found to extract version from" -Level Verbose
        throw "No NuGet packages found to extract version information from"
    }
    
    $filename = $nupkgFiles.Name
    Write-Message "Extracting version from package: $filename" -Level Verbose
    
    # Extract version from package name using a more robust approach
    # Remove .nupkg extension first, then look for the specific version pattern
    $baseName = $filename -replace '\.nupkg$', ''
    
    # Look for semantic version pattern with PR suffix (more specific and robust)
    if ($baseName -match '.*\.(\d+\.\d+\.\d+-pr\.\d+\.[0-9a-g]+)$') {
        $version = $Matches[1]
        Write-Message "Extracted version: $version" -Level Verbose
        
        # Extract just the PR suffix part using more specific regex
        if ($version -match '(pr\.[0-9]+\.[0-9a-g]+)') {
            $versionSuffix = $Matches[1]
            Write-Message "Extracted version suffix: $versionSuffix" -Level Verbose
            return $versionSuffix
        } else {
            Write-Message "Package version does not contain PR suffix: $version" -Level Verbose
            throw "Package version does not contain expected PR suffix format"
        }
    } else {
        Write-Message "Could not extract version from package name: $filename" -Level Verbose
        throw "Could not extract version from package name: $filename"
    }
}

# Function to find workflow run for SHA
function Find-WorkflowRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$HeadSHA
    )

    Write-Message "Finding ci.yml workflow run for SHA: $HeadSHA" -Level Verbose

    $runId = Invoke-GitHubAPICall -Endpoint "$Script:GHReposBase/actions/workflows/ci.yml/runs?event=pull_request&head_sha=$HeadSHA" -JqFilter ".workflow_runs | sort_by(.created_at, .updated_at) | reverse | .[0].id" -ErrorMessage "Failed to query workflow runs for SHA: $HeadSHA"

    if ([string]::IsNullOrWhiteSpace($runId) -or $runId -eq "null") {
        throw "No ci.yml workflow run found for PR SHA: $HeadSHA. This could mean no workflow has been triggered for this SHA $HeadSHA . Check at https://github.com/microsoft/aspire/actions/workflows/ci.yml"
    }

    Write-Message "Found workflow run ID: $runId" -Level Verbose
    return $runId.Trim()
}

# Function to download artifact using gh run download
function Invoke-ArtifactDownload {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunId,

        [Parameter(Mandatory = $true)]
        [string]$ArtifactName,

        [Parameter(Mandatory = $true)]
        [string]$DownloadDirectory
    )

    $downloadCommand = @("gh", "run", "download", $RunId, "-R", $Script:Repository, "--name", $ArtifactName, "-D", $DownloadDirectory)

    if ($PSCmdlet.ShouldProcess($ArtifactName, "Download $ArtifactName with $($downloadCommand -join ' ')")) {
        Write-Message "Downloading with: $($downloadCommand -join ' ')" -Level Verbose

        & $downloadCommand[0] $downloadCommand[1..($downloadCommand.Length-1)]

        if ($LASTEXITCODE -ne 0) {
            Write-Message "gh run download command failed with exit code $LASTEXITCODE . Command: $($downloadCommand -join ' ')" -Level Verbose
            throw "Failed to download artifact '$ArtifactName' from run: $RunId . If the workflow is still running then the artifact named '$ArtifactName' may not be available yet. Check at https://github.com/microsoft/aspire/actions/runs/$RunId#artifacts"
        }
    }
}

# Function to download VS Code extension artifact
function Get-AspireExtensionFromArtifact {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunId,

        [Parameter(Mandatory = $true)]
        [string]$TempDir
    )

    $downloadDir = Join-Path $TempDir "extension"
    Write-Message "Downloading VS Code extension from GitHub - $Script:ExtensionArtifactName ..." -Level Info

    try {
        Invoke-ArtifactDownload -RunId $RunId -ArtifactName $Script:ExtensionArtifactName -DownloadDirectory $downloadDir
        return $downloadDir
    }
    catch {
        Write-Message "Failed to download VS Code extension artifact: $($_.Exception.Message)" -Level Warning
        Write-Message "This could mean the extension artifact is not available for this build." -Level Info
        return $null
    }
}

# Function to install VS Code extension
function Install-AspireExtensionFromDownload {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DownloadDir,

        [switch]$UseInsiders
    )

    $vscodeCmd = if ($UseInsiders) { "code-insiders" } else { "code" }
    $vscodeName = if ($UseInsiders) { "VS Code Insiders" } else { "VS Code" }

    if (!$PSCmdlet.ShouldProcess($vscodeName, "Installing Aspire extension")) {
        return
    }

    # Find the .vsix file directly (the artifact contains the .vsix file, not a zip)
    $vsixFile = Get-ChildItem -Path $DownloadDir -Filter "*.vsix" -Recurse | Select-Object -First 1

    if (-not $vsixFile) {
        Write-Message "No .vsix file found in downloaded artifact" -Level Warning
        Write-Message "Files found in download directory:" -Level Verbose
        Get-ChildItem -Path $DownloadDir -Recurse | ForEach-Object { Write-Message "  $($_.Name)" -Level Verbose }
        return
    }

    try {
        # Install the extension using VS Code CLI
        Write-Message "Installing $vscodeName extension: $($vsixFile.Name)" -Level Info
        $installCommand = @($vscodeCmd, "--install-extension", $vsixFile.FullName)

        & $installCommand[0] $installCommand[1..($installCommand.Length-1)]

        if ($LASTEXITCODE -eq 0) {
            Write-Message "$vscodeName extension successfully installed" -Level Success
        } else {
            Write-Message "Failed to install $vscodeName extension (exit code: $LASTEXITCODE)" -Level Warning
        }
    }
    catch {
        Write-Message "Failed to install $vscodeName extension: $($_.Exception.Message)" -Level Warning
    }
}

# Function to download built-nugets artifact
function Get-BuiltNugets {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunId,

        [Parameter(Mandatory = $true)]
        [string]$RID,

        [Parameter(Mandatory = $true)]
        [string]$TempDir
    )

    $downloadDir = Join-Path $TempDir $Script:BuiltNugetsArtifactName
    Write-Message "Downloading built nugets artifact - $Script:BuiltNugetsArtifactName ..." -Level Info
    Invoke-ArtifactDownload -RunId $RunId -ArtifactName $Script:BuiltNugetsArtifactName -DownloadDirectory $downloadDir

    $builtNugetRidName = "$($Script:BuiltNugetsRidArtifactName)-$RID"
    Write-Message "Downloading rid specific built nugets artifact - $builtNugetRidName ..." -Level Info
    Invoke-ArtifactDownload -RunId $RunId -ArtifactName $builtNugetRidName -DownloadDirectory $downloadDir

    return $downloadDir
}

# Function to install built-nugets artifact
function Install-BuiltNugets {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DownloadDir,

        [Parameter(Mandatory = $true)]
        [string]$NugetHiveDir
    )

    if (!$PSCmdlet.ShouldProcess($NugetHiveDir, "Copying built nugets")) {
        return
    }

    # Remove and recreate the target directory to ensure clean state
    if (Test-Path $NugetHiveDir) {
        Write-Message "Removing existing nuget directory: $NugetHiveDir" -Level Verbose
        if ($PSCmdlet.ShouldProcess($NugetHiveDir, "Remove existing directory")) {
            Remove-Item $NugetHiveDir -Recurse -Force -ErrorAction Stop
        }
    }

    if ($PSCmdlet.ShouldProcess($NugetHiveDir, "Create directory")) {
        New-Item -ItemType Directory -Path $NugetHiveDir -Force -ErrorAction Stop | Out-Null
    }

    Write-Message "Copying nugets from $DownloadDir to $NugetHiveDir" -Level Verbose

    # Copy all .nupkg files from the artifact directory to the target directory
    try {
        $nupkgFiles = Get-ChildItem -Path $DownloadDir -Filter "*.nupkg" -Recurse

        if ($nupkgFiles.Count -eq 0) {
            Write-Message "No .nupkg files found in downloaded artifact" -Level Warning
            return
        }

        foreach ($file in $nupkgFiles) {
            if ($PSCmdlet.ShouldProcess($file.FullName, "Copy to $NugetHiveDir")) {
                Copy-Item $file.FullName -Destination $NugetHiveDir -ErrorAction Stop
            }
        }

        Write-Message "Successfully installed nuget packages to: $NugetHiveDir" -Level Verbose
        Write-Message "NuGet packages successfully installed to: $NugetHiveDir" -Level Success
    }
    catch {
        Write-Message "Failed to copy nuget artifact files: $($_.Exception.Message)" -Level Error
        throw
    }
}

# Function to download Aspire CLI artifact
function Get-AspireCliFromArtifact {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunId,

        [Parameter(Mandatory = $true)]
        [string]$RID,

        [Parameter(Mandatory = $true)]
        [string]$TempDir
    )

    $cliArchiveName = "$($Script:CliArchiveArtifactNamePrefix)-$RID"
    $downloadDir = Join-Path $TempDir "cli"
    Write-Message "Downloading CLI from GitHub - $cliArchiveName ..." -Level Info
    Invoke-ArtifactDownload -RunId $RunId -ArtifactName $cliArchiveName -DownloadDirectory $downloadDir

    return $downloadDir
}

# Function to install downloaded Aspire CLI
function Install-AspireCliFromDownload {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DownloadDir,

        [Parameter(Mandatory = $true)]
        [string]$CliBinDir
    )

    if (!$PSCmdlet.ShouldProcess($CliBinDir, "Installing Aspire CLI to $CliBinDir")) {
        return
    }

    $cliFiles = Get-ChildItem -Path $DownloadDir -File -Recurse | Where-Object { $_.Name -match "^$Script:AspireCliArtifactNamePrefix-.*\.(tar\.gz|zip)$" }

    if ($cliFiles.Count -eq 0) {
        Write-Message "No CLI archive found. Expected a single $(${Script:AspireCliArtifactNamePrefix})-*.tar.gz or $(${Script:AspireCliArtifactNamePrefix})-*.zip file in artifact root: $DownloadDir" -Level Error
        Write-Message "Candidate files present (root only):" -Level Info
        Get-ChildItem -Path $DownloadDir -File -Recurse | Select-Object -First 20 | ForEach-Object { Write-Message "  $($_.Name)" -Level Info }
        throw "CLI archive not found"
    }
    elseif ($cliFiles.Count -gt 1) {
        Write-Message "Multiple CLI archives found (expected exactly one):" -Level Error
        $cliFiles | ForEach-Object { Write-Message "  $($_.FullName)" -Level Error }
        throw "Multiple CLI archives found"
    }

    $cliArchivePath = $cliFiles[0].FullName

    # Install the archive
    Expand-AspireCliArchive -ArchiveFile $cliArchivePath -DestinationPath $CliBinDir

    # Check which aspire executable exists and set the path accordingly
    $aspireExePath = Join-Path $CliBinDir "aspire.exe"
    $aspirePath = Join-Path $CliBinDir "aspire"

    if (Test-Path $aspireExePath) {
        $cliPath = $aspireExePath
    }
    elseif (Test-Path $aspirePath) {
        $cliPath = $aspirePath
    }
    else {
        throw "Neither aspire.exe nor aspire executable found in $CliBinDir"
    }

    Write-Message "Aspire CLI successfully installed to: $cliPath" -Level Success
}

# Main function to install from a local directory of pre-built artifacts
function Start-InstallFromLocalDir {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LocalDirPath
    )

    if (-not (Test-Path $LocalDirPath -PathType Container)) {
        Write-Message "Local directory does not exist: $LocalDirPath" -Level Error
        throw "Local directory not found"
    }

    Write-Message "Installing from local directory: $LocalDirPath" -Level Info

    # Set installation paths
    $cliBinDir = Join-Path $resolvedInstallPrefix "bin"
    $resolvedHiveLabel = if ($HiveLabel) {
        $HiveLabel
    } elseif ($env:GITHUB_RUN_ID) {
        "run-$($env:GITHUB_RUN_ID)"
    } else {
        "run-local"
    }
    $nugetHiveDir = Join-Path $resolvedInstallPrefix "hives" $resolvedHiveLabel "packages"

    Write-Message "Using hive label: $resolvedHiveLabel" -Level Info

    $rid = Get-RuntimeIdentifier $OS $Architecture

    # Install CLI from local directory
    if ($HiveOnly) {
        Write-Message "Skipping CLI installation due to -HiveOnly flag" -Level Info
    } else {
        Install-AspireCliFromDownload -DownloadDir $LocalDirPath -CliBinDir $cliBinDir
    }

    # Install NuGet packages from local directory
    Install-BuiltNugets -DownloadDir $LocalDirPath -NugetHiveDir $nugetHiveDir

    # Extract and print the version suffix from packages
    try {
        $versionSuffix = Get-VersionSuffixFromPackages -DownloadDir $LocalDirPath
        Write-Message "Package version suffix: $versionSuffix" -Level Info
    }
    catch {
        Write-Message "Could not extract version suffix from local packages: $($_.Exception.Message)" -Level Warning
    }

    # Update PATH environment variables
    if (-not $HiveOnly) {
        if ($SkipPath) {
            Write-Message "Skipping PATH configuration due to -SkipPath flag" -Level Info
        } else {
            Update-PathEnvironment -CliBinDir $cliBinDir
        }
    }
}

# Main function to download and install from PR or workflow run ID
function Start-DownloadAndInstall {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TempDir
    )

    if ($WorkflowRunId) {
        # When workflow ID is provided, use it directly
        if ($PRNumber -gt 0) {
            Write-Message "Starting download and installation for PR #$PRNumber with workflow run ID: $WorkflowRunId" -Level Info
        } else {
            Write-Message "Starting download and installation for workflow run ID: $WorkflowRunId" -Level Info
        }
        $runId = $WorkflowRunId.ToString()
    }
    else {
        # When only PR number is provided, find the workflow run
        Write-Message "Starting download and installation for PR #$PRNumber" -Level Info

        # Get the PR head SHA
        $headSha = Get-PRHeadSHA -PRNumber $PRNumber

        # Find the workflow run
        $runId = Find-WorkflowRun -HeadSHA $headSha
    }

    Write-Message "Using workflow run https://github.com/$Script:Repository/actions/runs/$runId" -Level Info

    # Set installation paths
    $cliBinDir = Join-Path $resolvedInstallPrefix "bin"
    $resolvedHiveLabel = if ($HiveLabel) {
        $HiveLabel
    } elseif ($PRNumber -gt 0) {
        "pr-$PRNumber"
    } else {
        "run-$runId"
    }
    $nugetHiveDir = Join-Path $resolvedInstallPrefix "hives" $resolvedHiveLabel "packages"

    $rid = Get-RuntimeIdentifier $OS $Architecture

    # First, download artifacts
    if ($HiveOnly) {
        Write-Message "Skipping CLI download due to -HiveOnly flag" -Level Info
    } else {
        $cliDownloadDir = Get-AspireCliFromArtifact -RunId $runId -RID $rid -TempDir $TempDir
    }
    $nugetDownloadDir = Get-BuiltNugets -RunId $runId -RID $rid -TempDir $TempDir

    # Extract and print the version suffix from downloaded packages
    try {
        $versionSuffix = Get-VersionSuffixFromPackages -DownloadDir $nugetDownloadDir
        Write-Message "Package version suffix: $versionSuffix" -Level Info
    }
    catch {
        Write-Message "Could not extract version suffix from downloaded packages: $($_.Exception.Message)" -Level Warning
    }

    # Download VS Code extension if not skipped
    $extensionDownloadDir = $null
    if (-not $SkipExtension) {
        $extensionDownloadDir = Get-AspireExtensionFromArtifact -RunId $runId -TempDir $TempDir
    } else {
        Write-Message "Skipping VS Code extension download due to -SkipExtension flag" -Level Info
    }

    # Then, install artifacts
    Write-Message "Installing artifacts..." -Level Info
    if ($HiveOnly) {
        Write-Message "Skipping CLI installation due to -HiveOnly flag" -Level Info
    } else {
        Install-AspireCliFromDownload -DownloadDir $cliDownloadDir -CliBinDir $cliBinDir
    }
    Install-BuiltNugets -DownloadDir $nugetDownloadDir -NugetHiveDir $nugetHiveDir

    # Install VS Code extension if downloaded
    if ($extensionDownloadDir -and -not $SkipExtension) {
        if (Test-VSCodeCLIDependency -UseInsiders:$UseInsiders) {
            Install-AspireExtensionFromDownload -DownloadDir $extensionDownloadDir -UseInsiders:$UseInsiders
        }
    }

    # Update PATH environment variables
    if (-not $HiveOnly) {
        if ($SkipPath) {
            Write-Message "Skipping PATH configuration due to -SkipPath flag" -Level Info
        } else {
            Update-PathEnvironment -CliBinDir $cliBinDir
        }
    }
}

# =============================================================================
# Main Execution
# =============================================================================

try {
    # Show help if requested
    if ($Help) {
        Write-Message @"
Aspire CLI PR Download Script

DESCRIPTION:
    Downloads and installs the Aspire CLI from a specific pull request's latest successful build.
    Automatically detects the current platform and architecture.

Usage:
    get-aspire-cli-pr.ps1 <PRNumber> [OPTIONS]
    get-aspire-cli-pr.ps1 -WorkflowRunId <RunId> [OPTIONS]
    get-aspire-cli-pr.ps1 -LocalDir <Path> [OPTIONS]
    iex "& { `$(irm <url>/get-aspire-cli-pr.ps1) } <PRNumber> [OPTIONS]"

OPTIONS:
    -PRNumber <int>         Pull request number (required, positional)
    -WorkflowRunId <long>   Workflow run ID to download from (optional)
    -LocalDir <string>      Use pre-downloaded artifacts from a local directory
    -HiveLabel <string>     Override the NuGet hive label
    -InstallPath <string>   Directory prefix to install
    -OS <string>            Override OS detection (win, linux, linux-musl, osx)
    -Architecture <string>  Override architecture detection (x64, arm64)
    -HiveOnly               Only install NuGet packages to the hive, skip CLI download
    -SkipExtension          Skip VS Code extension download and installation
    -UseInsiders            Install extension to VS Code Insiders instead of VS Code
    -SkipPath               Do not add the install path to PATH environment variable
    -KeepArchive            Keep downloaded archive files after installation
    -Help                   Show this help information
"@ -Level Info
        if ($InvokedFromFile) { exit 0 } else { return 0 }
    }

    # Validate PRNumber is provided when not showing help or using --local-dir
    if ($LocalDir) {
        if ($PRNumber -gt 0 -or $WorkflowRunId -gt 0) {
            Write-Message "Error: -LocalDir is mutually exclusive with -PRNumber and -WorkflowRunId" -Level Error
            if ($InvokedFromFile) { exit 1 } else { return 1 }
        }
    } elseif ($PRNumber -le 0 -and $WorkflowRunId -le 0) {
        Write-Message "Error: PRNumber, -WorkflowRunId, or -LocalDir parameter is required" -Level Error
        Write-Message "Use -Help for usage information" -Level Info
        if ($InvokedFromFile) { exit 1 } else { return 1 }
    }

    # Set host OS for PATH environment updates
    $script:HostOS = Get-OperatingSystem

    # Check gh dependency (not needed for -LocalDir mode)
    if (-not $LocalDir) {
        Test-GitHubCLIDependency
    }

    # Set default install prefix if not provided
    $resolvedInstallPrefix = Get-InstallPrefix -InstallPrefix $InstallPath

    # Create a temporary directory for downloads
    $tempDir = New-TempDirectory -Prefix "aspire-cli-pr-download"

    try {
        # Download and install from PR/workflow run ID, or install from local directory
        if ($LocalDir) {
            Start-InstallFromLocalDir -LocalDirPath $LocalDir
        } else {
            Start-DownloadAndInstall -TempDir $tempDir
        }

        $exitCode = 0
    }
    finally {
        # Clean up temporary directory
        Remove-TempDirectory -TempDir $tempDir
    }
}
catch {
    Write-Message "Error: $($_.Exception.Message)" -Level Error
    if ($VerbosePreference -ne 'SilentlyContinue') {
        Write-Message "StackTrace: $($_.Exception.StackTrace)" -Level Verbose
    }
    $exitCode = 1
}

if ($InvokedFromFile) {
    exit $exitCode
}
else {
    if ($exitCode -ne 0) {
        return $exitCode
    }
}
