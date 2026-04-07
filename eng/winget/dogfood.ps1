<#
.SYNOPSIS
    Installs the Aspire CLI from local WinGet manifest files for dogfooding.

.DESCRIPTION
    This script installs (or uninstalls) the Aspire CLI using local WinGet manifest files,
    allowing you to test builds before they are published to microsoft/winget-pkgs.

.PARAMETER ManifestPath
    Path to the directory containing the WinGet manifest YAML files.
    Defaults to auto-detecting the manifest directory relative to this script.

.PARAMETER Uninstall
    Uninstall a previously dogfooded Aspire CLI.

.EXAMPLE
    .\dogfood.ps1
    # Auto-detects manifests in the script directory and installs

.EXAMPLE
    .\dogfood.ps1 -ManifestPath .\manifests\m\Microsoft\Aspire\9.2.0
    # Install from a specific manifest directory

.EXAMPLE
    .\dogfood.ps1 -Uninstall
    # Uninstall the dogfooded Aspire CLI
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$ManifestPath,

    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($Uninstall) {
    Write-Host "Uninstalling dogfooded Aspire CLI..."
    Write-Host ""

    # Try to find the package via winget
    $packages = @("Microsoft.Aspire", "Microsoft.Aspire.Prerelease")
    foreach ($pkg in $packages) {
        Write-Host "Checking for $pkg..."
        $result = winget list --id $pkg --accept-source-agreements 2>&1
        if ($LASTEXITCODE -eq 0 -and $result -match $pkg) {
            Write-Host "  Found $pkg, uninstalling..."
            winget uninstall --id $pkg --accept-source-agreements
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Uninstalled $pkg."
            } else {
                Write-Warning "  Failed to uninstall $pkg (exit code: $LASTEXITCODE)"
            }
        }
    }

    Write-Host ""
    Write-Host "Done."
    exit 0
}

# Auto-detect manifest path if not specified
if (-not $ManifestPath) {
    # Look for versioned manifest directories under the script directory
    # Convention: manifests/m/Microsoft/Aspire/{Version}/ or manifests/m/Microsoft/Aspire/Prerelease/{Version}/
    $candidates = Get-ChildItem -Path $ScriptDir -Directory -Recurse -Depth 6 |
        Where-Object {
            Test-Path (Join-Path $_.FullName "*.installer.yaml")
        } |
        Select-Object -First 1

    if ($candidates) {
        $ManifestPath = $candidates.FullName
    } else {
        Write-Error "No manifest directory found under $ScriptDir. Specify -ManifestPath explicitly."
        exit 1
    }
}

if (-not (Test-Path $ManifestPath)) {
    Write-Error "Manifest path not found: $ManifestPath"
    exit 1
}

# Verify it contains manifest files
$manifestFiles = Get-ChildItem -Path $ManifestPath -Filter "*.yaml"
if ($manifestFiles.Count -eq 0) {
    Write-Error "No .yaml manifest files found in: $ManifestPath"
    exit 1
}

Write-Host "Aspire CLI WinGet Dogfood Installer"
Write-Host "====================================="
Write-Host "  Manifest path: $ManifestPath"
Write-Host "  Manifest files:"
foreach ($f in $manifestFiles) {
    Write-Host "    - $($f.Name)"
}
Write-Host ""

# Enable local manifest files
Write-Host "Enabling local manifest files in winget settings..."
winget settings --enable LocalManifestFiles
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Failed to enable local manifests. You may need to run this as Administrator."
}

# Validate
Write-Host ""
Write-Host "Validating manifests..."
winget validate --manifest $ManifestPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "Manifest validation failed. Fix the manifests and try again."
    exit $LASTEXITCODE
}
Write-Host "Validation passed."

# Install
Write-Host ""
Write-Host "Installing Aspire CLI from local manifest..."
winget install --manifest $ManifestPath --accept-package-agreements --accept-source-agreements
if ($LASTEXITCODE -ne 0) {
    Write-Error "Installation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")

# Verify in a new process to pick up PATH changes
Write-Host ""
Write-Host "Verifying installation..."
$verifyResult = pwsh -NoProfile -Command '
    $cmd = Get-Command aspire -ErrorAction SilentlyContinue
    if (-not $cmd) { Write-Error "aspire not found in PATH"; exit 1 }
    Write-Host "  Path:    $($cmd.Source)"
    $v = & aspire --version 2>&1
    if ($LASTEXITCODE -ne 0) { Write-Error "aspire --version failed: $v"; exit $LASTEXITCODE }
    Write-Host "  Version: $v"
' 2>&1

if ($LASTEXITCODE -eq 0) {
    Write-Host $verifyResult
    Write-Host ""
    Write-Host "Installed successfully!"
} else {
    Write-Host $verifyResult
    Write-Host ""
    Write-Warning "aspire command not found in PATH. You may need to restart your shell."
}

Write-Host ""
Write-Host "To uninstall: .\dogfood.ps1 -Uninstall"
