<#
.SYNOPSIS
    Generates WinGet manifest files for Aspire CLI from templates.

.DESCRIPTION
    This script generates the required WinGet manifest files (version, locale, and installer)
    from templates by substituting version numbers, URLs, and computing SHA256 hashes.
    Installer URLs are derived from the version and RIDs using the ci.dot.net URL pattern.

.PARAMETER Version
    The version number for the package (e.g., "13.3.0-preview.1.26111.5").

.PARAMETER TemplateDir
    The directory containing the manifest templates to use.
    Use "microsoft.aspire" for release builds or "microsoft.aspire.prerelease" for prerelease builds.

.PARAMETER Rids
    Comma-separated list of Runtime Identifiers for the installer architectures.
    Defaults to "win-x64,win-arm64".

.PARAMETER OutputPath
    The directory where the manifest files will be written.
    Defaults to a path derived from the PackageIdentifier in the templates,
    e.g., "./manifests/m/Microsoft/Aspire/{Version}" for Microsoft.Aspire
    or "./manifests/m/Microsoft/Aspire/Prerelease/{Version}" for Microsoft.Aspire.Prerelease.

.PARAMETER ReleaseNotesUrl
    URL to the release notes page. If not specified, derived from the version
    (e.g., "13.2.0" -> "https://aspire.dev/whats-new/aspire-13-2/").

.PARAMETER ValidateUrls
    When specified, verifies that all installer URLs are accessible (HTTP HEAD request)
    before downloading them to compute SHA256 hashes.

.EXAMPLE
    ./generate-manifests.ps1 -Version "13.3.0-preview.1.26111.5" `
        -TemplateDir "./eng/winget/microsoft.aspire.prerelease"

.EXAMPLE
    ./generate-manifests.ps1 -Version "13.2.0" `
        -TemplateDir "./eng/winget/microsoft.aspire" `
        -Rids "win-x64,win-arm64" -ValidateUrls
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$TemplateDir,

    [Parameter(Mandatory = $false)]
    [string]$Rids = "win-x64,win-arm64",

    [Parameter(Mandatory = $false)]
    [string]$OutputPath,

    [Parameter(Mandatory = $false)]
    [string]$ReleaseNotesUrl,

    [Parameter(Mandatory = $false)]
    [switch]$ValidateUrls
)

$ErrorActionPreference = 'Stop'

# Determine script paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Validate template directory
if (-not (Test-Path $TemplateDir)) {
    Write-Error "Template directory not found: $TemplateDir"
    exit 1
}

# Extract PackageIdentifier from the version template
$versionTemplatePath = Join-Path $TemplateDir "Aspire.yaml.template"
if (-not (Test-Path $versionTemplatePath)) {
    Write-Error "Version template not found: $versionTemplatePath"
    exit 1
}

$PackageIdentifier = $null
foreach ($line in Get-Content -Path $versionTemplatePath) {
    if ($line -match '^\s*PackageIdentifier:\s*(.+)\s*$') {
        $PackageIdentifier = $Matches[1].Trim()
        break
    }
}

if (-not $PackageIdentifier) {
    Write-Error "Could not extract PackageIdentifier from $versionTemplatePath"
    exit 1
}

Write-Host "Package identifier: $PackageIdentifier"

# Derive the output directory from the PackageIdentifier
# Convention: manifests/{first-letter-lowercase}/{Segment1}/{Segment2}/.../{Version}
# e.g. Microsoft.Aspire -> manifests/m/Microsoft/Aspire/{Version}
# e.g. Microsoft.Aspire.Prerelease -> manifests/m/Microsoft/Aspire/Prerelease/{Version}
if (-not $OutputPath) {
    $idSegments = $PackageIdentifier.Split('.')
    $firstLetter = $idSegments[0].Substring(0, 1).ToLowerInvariant()
    $pathSegments = @("manifests", $firstLetter) + $idSegments + @($Version)
    $OutputPath = Join-Path $ScriptDir ($pathSegments -join [System.IO.Path]::DirectorySeparatorChar)
}

Write-Host "Generating WinGet manifests for Aspire version $Version"
Write-Host "Output directory: $OutputPath"

# Create output directory
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

# Parse RIDs
$ridList = $Rids.Split(',') | ForEach-Object { $_.Trim() }
Write-Host "RIDs: $($ridList -join ', ')"

# Map RID to winget architecture name
function Get-ArchitectureFromRid {
    param([string]$Rid)

    # Extract the architecture portion after the OS prefix (e.g., "win-x64" -> "x64")
    if ($Rid -match '^win-(.+)$') {
        return $Matches[1]
    }

    Write-Error "Unsupported RID format: $Rid (expected 'win-<arch>')"
    exit 1
}

# Build installer URL from version and RID
# Pattern: https://ci.dot.net/public/aspire/{version}/aspire-cli-{rid}-{version}.zip
function Get-InstallerUrl {
    param(
        [string]$Version,
        [string]$Rid
    )

    return "https://ci.dot.net/public/aspire/$Version/aspire-cli-$Rid-$Version.zip"
}

# Function to compute SHA256 hash of a file downloaded from URL
function Get-RemoteFileSha256 {
    param(
        [string]$Url,
        [string]$Description
    )

    Write-Host "Downloading $Description to compute SHA256..."
    Write-Host "  URL: $Url"

    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $tempFile -UseBasicParsing -TimeoutSec 120

        $hash = (Get-FileHash -Path $tempFile -Algorithm SHA256).Hash.ToUpperInvariant()
        Write-Host "  SHA256: $hash"
        return $hash
    }
    finally {
        if (Test-Path $tempFile) {
            Remove-Item $tempFile -Force
        }
    }
}

# Function to process a template file
function Process-Template {
    param(
        [string]$TemplatePath,
        [string]$OutputFile,
        [hashtable]$Substitutions
    )

    $templateName = Split-Path -Leaf $TemplatePath
    Write-Host "Processing template: $templateName"

    $content = Get-Content -Path $TemplatePath -Raw

    foreach ($key in $Substitutions.Keys) {
        $placeholder = "`${$key}"
        $value = $Substitutions[$key]
        $content = $content.Replace($placeholder, $value)
    }

    Set-Content -Path $OutputFile -Value $content -NoNewline
    Write-Host "  Created: $OutputFile"
}

# Build the Installers YAML block and compute hashes
Write-Host ""

# Build all installer URLs first
$installerEntries = @()
foreach ($rid in $ridList) {
    $arch = Get-ArchitectureFromRid -Rid $rid
    $url = Get-InstallerUrl -Version $Version -Rid $rid
    $installerEntries += @{ Rid = $rid; Architecture = $arch; Url = $url }
}

# Validate URLs are accessible before downloading (fast-fail)
if ($ValidateUrls) {
    Write-Host "Validating installer URLs..."
    $failed = $false
    foreach ($entry in $installerEntries) {
        Write-Host "  Checking: $($entry.Url)"
        try {
            $response = Invoke-WebRequest -Uri $entry.Url -Method Head -UseBasicParsing -TimeoutSec 30
            Write-Host "    Status: $($response.StatusCode) OK"
        }
        catch {
            Write-Host "    ERROR: URL not accessible: $($_.Exception.Message)"
            $failed = $true
        }
    }

    if ($failed) {
        Write-Error "One or more installer URLs are not accessible. Ensure the release artifacts have been published."
        exit 1
    }
    Write-Host ""
}

Write-Host "Computing SHA256 hashes..."

$installersYaml = "Installers:"
foreach ($entry in $installerEntries) {
    $sha256 = Get-RemoteFileSha256 -Url $entry.Url -Description "$($entry.Rid) installer"

    $installersYaml += "`n- Architecture: $($entry.Architecture)"
    $installersYaml += "`n  InstallerUrl: $($entry.Url)"
    $installersYaml += "`n  InstallerSha256: $sha256"
}

# Define substitutions
$today = Get-Date -Format "yyyy-MM-dd"
$year = Get-Date -Format "yyyy"
# Derive ReleaseNotesUrl from version if not specified
# Version format: "13.2.0" or "13.3.0-preview.1.26111.5"
# URL pattern: https://aspire.dev/whats-new/aspire-{major}-{minor}/
if (-not $ReleaseNotesUrl) {
    if ($Version -match '^(\d+)\.(\d+)') {
        $ReleaseNotesUrl = "https://aspire.dev/whats-new/aspire-$($Matches[1])-$($Matches[2])/"
    } else {
        $ReleaseNotesUrl = "https://aspire.dev/"
    }
    Write-Host "Derived ReleaseNotesUrl: $ReleaseNotesUrl"
}

$substitutions = @{
    "VERSION"           = $Version
    "INSTALLERS"        = $installersYaml
    "RELEASE_DATE"      = $today
    "YEAR"              = $year
    "RELEASE_NOTES_URL" = $ReleaseNotesUrl
}

Write-Host ""
Write-Host "Generating manifest files..."

# Process each template
# Output files are named {PackageIdentifier}.{type}.yaml per winget convention
$templates = @(
    @{ Template = "Aspire.yaml.template"; Output = "$PackageIdentifier.yaml" },
    @{ Template = "Aspire.locale.en-US.yaml.template"; Output = "$PackageIdentifier.locale.en-US.yaml" },
    @{ Template = "Aspire.installer.yaml.template"; Output = "$PackageIdentifier.installer.yaml" }
)

foreach ($template in $templates) {
    $templatePath = Join-Path $TemplateDir $template.Template
    $outputFile = Join-Path $OutputPath $template.Output

    if (-not (Test-Path $templatePath)) {
        Write-Error "Template not found: $templatePath"
        exit 1
    }

    Process-Template -TemplatePath $templatePath -OutputFile $outputFile -Substitutions $substitutions
}

Write-Host ""
Write-Host "Successfully generated WinGet manifests at: $OutputPath"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Validate manifests: winget validate --manifest `"$OutputPath`""
Write-Host "  2. Submit to winget-pkgs: wingetcreate submit --token YOUR_PAT `"$OutputPath`""

exit 0
