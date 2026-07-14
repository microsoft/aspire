#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$Repository = 'microsoft/aspire-skills'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$scriptDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$embeddedDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\AspireSkills\Embedded'
$metadataPath = Join-Path $embeddedDir 'aspire-skills.metadata.json'
$hooksDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\Hooks'

. (Join-Path $scriptDir 'aspire-skills-bundle.common.ps1')

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI ('gh') is required to verify the embedded Aspire skills bundle."
}

if (-not (Test-Path $metadataPath)) {
    throw "Embedded Aspire skills metadata was not found at '$metadataPath'."
}

$metadata = Get-Content -Raw -Path $metadataPath | ConvertFrom-Json

if ($metadata.repository -ne $Repository) {
    throw "Unexpected embedded bundle repository '$($metadata.repository)'. Expected '$Repository'."
}

if ([string]::IsNullOrWhiteSpace($metadata.tag)) {
    throw "Embedded Aspire skills metadata must specify a GitHub release tag."
}

if ([string]::IsNullOrWhiteSpace($metadata.assetName)) {
    throw "Embedded Aspire skills metadata must specify a release asset name."
}

if ($metadata.assetName -ne [System.IO.Path]::GetFileName($metadata.assetName)) {
    throw "Embedded Aspire skills asset name '$($metadata.assetName)' must not contain path separators."
}

if ([string]::IsNullOrWhiteSpace($metadata.sha256)) {
    throw "Embedded Aspire skills metadata must specify the release asset SHA-256 hash."
}

$archivePath = Join-Path $embeddedDir $metadata.assetName
if (-not (Test-Path $archivePath)) {
    throw "Embedded Aspire skills archive was not found at '$archivePath'."
}

$actualHash = (Get-FileHash -Algorithm SHA256 $archivePath).Hash.ToLowerInvariant()
if ($actualHash -ne $metadata.sha256) {
    throw "Embedded bundle SHA-256 mismatch. Expected '$($metadata.sha256)', got '$actualHash'."
}

$certIdentity = "https://github.com/$($metadata.repository)/.github/workflows/publish.yml@refs/tags/$($metadata.tag)"
gh attestation verify $archivePath `
    --repo $metadata.repository `
    --cert-identity $certIdentity `
    --cert-oidc-issuer 'https://token.actions.githubusercontent.com'
# Explicitly fail on a non-zero exit. This is the security-critical gate, and the native
# command error-action auto-throw is not honored on older hosts (Windows PowerShell 5.1), where
# a failed or abstained attestation would otherwise fall through and be reported as verified.
if ($LASTEXITCODE -ne 0) {
    throw "GitHub artifact attestation verification failed for '$archivePath' (exit code $LASTEXITCODE)."
}

Write-Host "Embedded Aspire skills bundle '$($metadata.assetName)' verified against GitHub artifact attestation."

# Verify the embedded telemetry hook scripts when the bundle records them. The hooks block is only
# present once update-aspire-skills-bundle.ps1 has synced hooks from a release that contains them, so
# older bundles (which predate the feature) skip this check. When present, cross-check both that the
# embedded file matches the recorded hash AND that the recorded hash matches the canonical source at
# the pinned aspire-skills commit, so a hand-edit that also updates the metadata hash cannot pass.
if ($metadata.PSObject.Properties.Name -contains 'hooks') {
    $hooks = $metadata.hooks

    if ([string]::IsNullOrWhiteSpace($hooks.commitSha)) {
        throw "Embedded Aspire skills metadata 'hooks' block must specify the aspire-skills commit SHA the hooks were pinned to."
    }

    if (-not ($hooks.PSObject.Properties.Name -contains 'files')) {
        throw "Embedded Aspire skills metadata 'hooks' block must record a 'files' map of hook hashes."
    }

    foreach ($hookFileName in Get-AspireSkillsHookFileNames) {
        if (-not ($hooks.files.PSObject.Properties.Name -contains $hookFileName)) {
            throw "Embedded Aspire skills metadata 'hooks' block is missing a recorded hash for '$hookFileName'."
        }

        $recordedHash = $hooks.files.$hookFileName

        $embeddedHookPath = Join-Path $hooksDir $hookFileName
        if (-not (Test-Path $embeddedHookPath)) {
            throw "Embedded telemetry hook script was not found at '$embeddedHookPath'."
        }

        # Hash over LF-normalized bytes so .ps1 (text=auto) checked out with CRLF on Windows matches.
        $embeddedHash = Get-AspireSkillsSha256Hex -Bytes (ConvertTo-LfUtf8Bytes -Bytes ([System.IO.File]::ReadAllBytes($embeddedHookPath)))
        if ($embeddedHash -ne $recordedHash) {
            throw "Embedded telemetry hook '$hookFileName' SHA-256 mismatch. Expected '$recordedHash', got '$embeddedHash'. Re-run update-aspire-skills-bundle.ps1."
        }

        $sourceHash = Get-AspireSkillsSha256Hex -Bytes (Get-AspireSkillsHookContent -Repository $metadata.repository -CommitSha $hooks.commitSha -FileName $hookFileName)
        if ($sourceHash -ne $recordedHash) {
            throw "Telemetry hook '$hookFileName' does not match '$($metadata.repository)' at commit '$($hooks.commitSha)'. Expected '$recordedHash', got '$sourceHash'."
        }
    }

    Write-Host "Embedded telemetry hook scripts verified against '$($metadata.repository)' at commit '$($hooks.commitSha)'."
}
