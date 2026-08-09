<#
.SYNOPSIS
    Downloads and displays asciinema recordings from CLI E2E test runs.

.DESCRIPTION
    This script downloads test artifacts from GitHub Actions CI runs and 
    optionally plays asciinema recordings for debugging CLI E2E test failures.

.PARAMETER RunId
    Specific GitHub Actions run ID. If not specified, uses the latest run on the current branch.

.PARAMETER TestName
    Test class name (default: SmokeTests)

.PARAMETER OutputDir
    Output directory for downloaded artifacts (default: $env:TEMP\cli-e2e-recordings)

.PARAMETER Play
    Play the recording after download (requires asciinema to be installed)

.PARAMETER List
    List available recordings without downloading

.PARAMETER Branch
    Branch name (default: current git branch)

.EXAMPLE
    .\get-cli-e2e-recording.ps1
    Downloads SmokeTests recording from the latest CI run on the current branch.

.EXAMPLE
    .\get-cli-e2e-recording.ps1 -Play
    Downloads and plays the recording.

.EXAMPLE
    .\get-cli-e2e-recording.ps1 -TestName SmokeTests -Play
    Downloads the SmokeTests recording and plays it.

.EXAMPLE
    .\get-cli-e2e-recording.ps1 -RunId 20944531393 -Play
    Downloads recording from a specific run.

.EXAMPLE
    .\get-cli-e2e-recording.ps1 -List
    Lists available test recordings without downloading.
#>

param(
    [string]$RunId = "",
    [string]$TestName = "SmokeTests",
    [string]$OutputDir = "$env:TEMP\cli-e2e-recordings",
    [switch]$Play,
    [switch]$List,
    [string]$Branch = ""
)

$ErrorActionPreference = "Stop"
$Repo = "microsoft/aspire"

# Check for gh CLI
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) is required. Install from https://cli.github.com/"
    exit 1
}

# Get branch name if not specified
if ([string]::IsNullOrEmpty($Branch)) {
    try {
        $Branch = git branch --show-current 2>$null
    } catch {
        $Branch = ""
    }
    
    if ([string]::IsNullOrEmpty($Branch)) {
        Write-Error "Could not determine current branch. Use -Branch to specify."
        exit 1
    }
}

Write-Host "Branch: $Branch"

# Get run ID if not specified
if ([string]::IsNullOrEmpty($RunId)) {
    Write-Host "Finding latest CI run..."
    $runJson = gh run list --branch $Branch --workflow CI --limit 1 --json databaseId -R $Repo 2>$null | ConvertFrom-Json
    
    if ($runJson -and $runJson.Count -gt 0) {
        $RunId = $runJson[0].databaseId
    }
    
    if ([string]::IsNullOrEmpty($RunId)) {
        Write-Error "No CI runs found for branch '$Branch'"
        exit 1
    }
}

Write-Host "Run ID: $RunId"
Write-Host "Run URL: https://github.com/$Repo/actions/runs/$RunId"

# List available artifacts
Write-Host ""
Write-Host "Fetching available CLI E2E test artifacts..."
$artifactsJson = gh api --paginate "repos/$Repo/actions/runs/$RunId/artifacts" 2>$null | ConvertFrom-Json
$allArtifacts = $artifactsJson.artifacts | Where-Object { $_.name -match "^logs-.*-ubuntu-latest$" } | Select-Object -ExpandProperty name | Sort-Object

# Filter for CLI E2E related artifacts
$cliArtifacts = $allArtifacts | Where-Object { $_ -match "smoke|e2e|cli" }
if (-not $cliArtifacts) {
    $cliArtifacts = $allArtifacts
}

if ($List) {
    Write-Host ""
    Write-Host "Available test artifacts:"
    foreach ($artifact in $cliArtifacts) {
        Write-Host "  - $artifact"
    }
    
    Write-Host ""
    Write-Host "To download a specific test, run:"
    Write-Host "  $PSCommandPath -RunId $RunId -TestName <TestClassName>"
    exit 0
}

# Find the artifact for the requested test
$ArtifactName = "logs-$TestName-ubuntu-latest"

# Check if artifact exists
if ($ArtifactName -notin $allArtifacts) {
    Write-Host ""
    Write-Error "Artifact '$ArtifactName' not found."
    Write-Host ""
    Write-Host "Available artifacts:"
    foreach ($artifact in ($cliArtifacts | Select-Object -First 20)) {
        Write-Host "  - $artifact"
    }
    exit 1
}

Write-Host "Artifact: $ArtifactName"

# Create output directory
$DownloadDir = Join-Path $OutputDir "$RunId\$TestName"
if (Test-Path $DownloadDir) {
    Remove-Item -Recurse -Force $DownloadDir
}
New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null

Write-Host ""
Write-Host "Downloading to: $DownloadDir"
gh run download $RunId -n $ArtifactName -D $DownloadDir -R $Repo

# Find recordings
$RecordingsDir = Join-Path $DownloadDir "testresults\recordings"
if (Test-Path $RecordingsDir) {
    Write-Host ""
    Write-Host "Available recordings:"
    $recordings = Get-ChildItem -Path $RecordingsDir -Filter "*.cast" -File
    foreach ($recording in $recordings) {
        Write-Host "  - $($recording.Name)"
    }
    
    # Get the first recording for playback
    $FirstRecording = $recordings | Select-Object -First 1
    
    if ($Play -and $FirstRecording) {
        Write-Host ""
        if (Get-Command asciinema -ErrorAction SilentlyContinue) {
            Write-Host "Playing: $($FirstRecording.FullName)"
            Write-Host "Press 'q' to quit, space to pause, +/- to adjust speed"
            Write-Host ""
            asciinema play $FirstRecording.FullName
        } else {
            Write-Host "asciinema not installed. To view recordings:"
            Write-Host "  1. Install asciinema: pip install asciinema"
            Write-Host "  2. Run: asciinema play $($FirstRecording.FullName)"
            Write-Host ""
            Write-Host "Or view raw content:"
            Write-Host "  Get-Content $($FirstRecording.FullName) | Select-Object -First 50"
        }
    }
} else {
    Write-Host ""
    Write-Host "No recordings found in artifact."
    Write-Host "Contents:"
    Get-ChildItem -Path $DownloadDir -Recurse -File | Select-Object -First 20 | ForEach-Object { Write-Host "  - $($_.FullName)" }
}

Write-Host ""
Write-Host "Download complete: $DownloadDir"
