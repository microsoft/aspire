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

Write-Host "Embedded Aspire skills bundle '$($metadata.assetName)' verified against GitHub artifact attestation."
