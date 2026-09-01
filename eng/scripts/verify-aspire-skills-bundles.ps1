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
$hooksDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\Hooks'
$installerPath = Join-Path $repoRoot 'src\Aspire.Cli\Agents\AspireSkills\AspireSkillsInstaller.cs'

. (Join-Path $scriptDir 'aspire-skills-bundles.common.ps1')

$bundleDefinitions = Get-AspireSkillsBundleDefinitions

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI ('gh') is required to verify the embedded Aspire Skills bundles."
}

$verifiedBundles = @(
    foreach ($definition in $bundleDefinitions) {
        [pscustomobject]@{
            Definition = $definition
            Metadata = Get-AspireSkillsVerifiedBundleMetadata `
                -Repository $Repository `
                -EmbeddedDirectory $embeddedDir `
                -Definition $definition
        }
    }
)

$installerContent = Get-Content -Raw -Path $installerPath
$versionMatches = [regex]::Matches($installerContent, 'internal const string Version = "([^"]+)";')
if ($versionMatches.Count -ne 1) {
    throw "Expected exactly one Aspire Skills bundle version constant in '$installerPath', but found $($versionMatches.Count)."
}
$expectedVersion = $versionMatches[0].Groups[1].Value
$embeddedVersions = @($verifiedBundles.Metadata.version | Select-Object -Unique)
if ($embeddedVersions.Count -ne 1 -or $embeddedVersions[0] -ne $expectedVersion) {
    throw "Embedded Aspire Skills bundle versions '$($embeddedVersions -join ', ')' must match AspireSkillsInstaller.Version '$expectedVersion'."
}

# Verify the embedded telemetry hook scripts when the bundle records them. The hooks block is only
# present once update-aspire-skills-bundles.ps1 has synced hooks from a release that contains them, so
# older bundles (which predate the feature) skip this check. When present, cross-check both that the
# embedded file matches the recorded hash AND that the recorded hash matches the canonical source at
# the pinned aspire-skills commit, so a hand-edit that also updates the metadata hash cannot pass.
# Hook scripts are shared companions sourced alongside both bundles, not files inside either payload.
$bundlesWithHooks = @($verifiedBundles | Where-Object {
    $_.Metadata.PSObject.Properties.Name -contains 'hooks'
})
if ($bundlesWithHooks.Count -ne 0 -and $bundlesWithHooks.Count -ne $verifiedBundles.Count) {
    throw "Embedded Aspire bundle metadata must record shared telemetry hooks for every sibling bundle or none of them."
}

$metadata = $verifiedBundles[0].Metadata
if ($metadata.PSObject.Properties.Name -contains 'hooks') {
    $recordedHooks = $metadata.hooks | ConvertTo-Json -Compress -Depth 10
    foreach ($bundle in $bundlesWithHooks | Select-Object -Skip 1) {
        $bundleHooks = $bundle.Metadata.hooks | ConvertTo-Json -Compress -Depth 10
        if ($bundleHooks -ne $recordedHooks) {
            throw "Embedded $($bundle.Definition.DisplayName) metadata must record the same shared telemetry hook provenance as the other sibling bundles."
        }
    }

    $hooks = $metadata.hooks

    if ([string]::IsNullOrWhiteSpace($hooks.commitSha)) {
        throw "Embedded Aspire bundle metadata 'hooks' block must specify the aspire-skills commit SHA the hooks were pinned to."
    }

    if (-not ($hooks.PSObject.Properties.Name -contains 'files')) {
        throw "Embedded Aspire bundle metadata 'hooks' block must record a 'files' map of hook hashes."
    }

    foreach ($hookFileName in Get-AspireSkillsHookFileNames) {
        if (-not ($hooks.files.PSObject.Properties.Name -contains $hookFileName)) {
            throw "Embedded Aspire bundle metadata 'hooks' block is missing a recorded hash for '$hookFileName'."
        }

        $recordedHash = $hooks.files.$hookFileName

        $embeddedHookPath = Join-Path $hooksDir $hookFileName
        if (-not (Test-Path $embeddedHookPath)) {
            throw "Embedded telemetry hook script was not found at '$embeddedHookPath'."
        }

        # Hash over LF-normalized bytes so .ps1 (text=auto) checked out with CRLF on Windows matches.
        $embeddedHash = Get-AspireSkillsSha512Hex -Bytes (ConvertTo-LfUtf8Bytes -Bytes ([System.IO.File]::ReadAllBytes($embeddedHookPath)))
        if ($embeddedHash -ne $recordedHash) {
            throw "Embedded telemetry hook '$hookFileName' SHA-512 mismatch. Expected '$recordedHash', got '$embeddedHash'. Re-run update-aspire-skills-bundles.ps1."
        }

        $sourceHash = Get-AspireSkillsSha512Hex -Bytes (Get-AspireSkillsHookContent -Repository $metadata.repository -CommitSha $hooks.commitSha -FileName $hookFileName)
        if ($sourceHash -ne $recordedHash) {
            throw "Telemetry hook '$hookFileName' does not match '$($metadata.repository)' at commit '$($hooks.commitSha)'. Expected '$recordedHash', got '$sourceHash'."
        }
    }

    Write-Host "Embedded telemetry hook scripts verified against '$($metadata.repository)' at commit '$($hooks.commitSha)'."
}
