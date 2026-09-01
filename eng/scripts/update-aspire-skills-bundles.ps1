#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Repository = 'microsoft/aspire-skills'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$scriptDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$embeddedDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\AspireSkills\Embedded'
$installerPath = Join-Path $repoRoot 'src\Aspire.Cli\Agents\AspireSkills\AspireSkillsInstaller.cs'
$cliProjectPath = Join-Path $repoRoot 'src\Aspire.Cli\Aspire.Cli.csproj'
$hooksDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\Hooks'

. (Join-Path $scriptDir 'aspire-skills-bundles.common.ps1')

$bundleDefinitions = Get-AspireSkillsBundleDefinitions

function Invoke-GitHubCli {
    param(
        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-UnprefixedVersion([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw 'A version is required.'
    }

    if ($Value.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Value.Substring(1)
    }

    return $Value
}

function Get-CurrentEmbeddedVersion {
    param([Parameter(Mandatory = $true)]$Definition)

    $metadataPath = Join-Path $embeddedDir $Definition.MetadataFileName
    if (-not (Test-Path $metadataPath)) {
        throw "Embedded $($Definition.DisplayName) metadata was not found at '$metadataPath'."
    }

    $metadata = Get-Content -Raw -Path $metadataPath | ConvertFrom-Json
    return Get-UnprefixedVersion $metadata.version
}

function Set-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content.TrimEnd("`r", "`n") + [System.Environment]::NewLine, $utf8NoBom)
}

function Get-GitHubRelease([string]$NormalizedVersion) {
    $tagCandidates = @("v$NormalizedVersion", $NormalizedVersion) | Select-Object -Unique

    foreach ($tag in $tagCandidates) {
        try {
            $json = Invoke-GitHubCli release view $tag --repo $Repository --json 'tagName,assets'
            return $json | ConvertFrom-Json
        }
        catch {
            Write-Host "Release '$tag' was not found in '$Repository'."
        }
    }

    throw "Could not find an Aspire Skills release for version '$NormalizedVersion' in '$Repository'."
}

function Get-ReleaseAsset($Release, [string]$NormalizedVersion, $Definition) {
    $assetNameCandidates = foreach ($archiveExtension in @('.zip', '.tar.gz', '.tgz')) {
        "$($Definition.AssetPrefix)-v$NormalizedVersion$archiveExtension"
        "$($Definition.AssetPrefix)-$NormalizedVersion$archiveExtension"
    }

    foreach ($assetName in $assetNameCandidates) {
        $asset = $Release.assets | Where-Object { $_.name -ieq $assetName } | Select-Object -First 1
        if ($null -ne $asset) {
            return $asset
        }
    }

    throw "Release '$($Release.tagName)' does not contain a supported $($Definition.DisplayName) archive asset for version '$NormalizedVersion'."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI ('gh') is required to update the embedded Aspire Skills bundles."
}

$normalizedVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
    $embeddedVersions = @(
        $bundleDefinitions |
            ForEach-Object { Get-CurrentEmbeddedVersion $_ } |
            Sort-Object -Unique
    )
    if ($embeddedVersions.Count -ne 1) {
        throw "Embedded Aspire bundle metadata versions must match, but found: $($embeddedVersions -join ', ')."
    }

    $embeddedVersions[0]
}
else {
    Get-UnprefixedVersion $Version
}

Write-Host "Resolving Aspire Skills release '$normalizedVersion' from '$Repository'..."
$release = Get-GitHubRelease $normalizedVersion

# Resolve every sibling asset before downloading or changing tracked files. A partially
# published release must not update one embedded bundle while leaving the other stale.
$resolvedBundles = @($bundleDefinitions | ForEach-Object {
    [pscustomobject]@{
        Definition = $_
        Asset = Get-ReleaseAsset $release $normalizedVersion $_
    }
})

$tempDir = [System.IO.Directory]::CreateTempSubdirectory('aspire-skills-update-').FullName
try {
    $preparedBundles = @($resolvedBundles | ForEach-Object {
        $definition = $_.Definition
        $asset = $_.Asset

        Write-Host "Downloading '$($asset.name)' from '$Repository' release '$($release.tagName)'..."
        Invoke-GitHubCli release download $release.tagName --repo $Repository --pattern $asset.name --dir $tempDir --clobber

        $archivePath = Join-Path $tempDir $asset.name
        if (-not (Test-Path $archivePath)) {
            throw "Expected downloaded asset '$archivePath' was not found."
        }

        $certIdentity = "https://github.com/$Repository/.github/workflows/publish.yml@refs/tags/$($release.tagName)"
        Write-Host "Verifying GitHub artifact attestation for '$($asset.name)'..."
        Invoke-GitHubCli attestation verify $archivePath --repo $Repository --cert-identity $certIdentity --cert-oidc-issuer 'https://token.actions.githubusercontent.com' |
            Out-Host

        [pscustomobject]@{
            Definition = $definition
            Asset = $asset
            ArchivePath = $archivePath
            Sha512 = (Get-FileHash -Algorithm SHA512 $archivePath).Hash.ToLowerInvariant()
        }
    })

    # Sync the telemetry hook scripts from the same release. Hooks are SOURCE files in aspire-skills
    # (hooks/scripts/track-telemetry.{sh,ps1}), so they are pinned to the immutable commit the release
    # tag points at and fetched via the contents API (see aspire-skills-bundles.common.ps1). Releases
    # that predate the telemetry hooks feature do not contain hooks/scripts/*, so a missing hook is a
    # warning + skip during the transition rather than a hard failure of the whole bundle update;
    # verification only enforces hooks once they are recorded in metadata.
    # Fetch every hook before modifying tracked files. Hooks are not a sibling bundle, but they stay
    # pinned to the same immutable release commit.
    $hookMetadata = $null
    $hookContents = [ordered]@{}
    try {
        $hookCommitSha = Get-AspireSkillsReleaseCommitSha -Repository $Repository -Tag $release.tagName

        # Fetch every hook first and only write to disk + record metadata once all fetches succeed.
        # Writing inside the fetch loop could leave one fresh + one stale file (and no hooks metadata)
        # if a later fetch failed, after which verify-aspire-skills-bundles.ps1 would silently skip hook
        # verification. Collecting first makes the on-disk update atomic.
        $hookHashes = [ordered]@{}
        foreach ($hookFileName in Get-AspireSkillsHookFileNames) {
            Write-Host "Syncing hook script '$hookFileName' from '$Repository' at commit '$hookCommitSha'..."
            $hookBytes = Get-AspireSkillsHookContent -Repository $Repository -CommitSha $hookCommitSha -FileName $hookFileName
            $hookContents[$hookFileName] = $hookBytes
            $hookHashes[$hookFileName] = Get-AspireSkillsSha512Hex -Bytes $hookBytes
        }

        $hookMetadata = [ordered]@{
            commitSha = $hookCommitSha
            files = $hookHashes
        }
    }
    catch {
        # A release that predates the telemetry hooks feature has no hooks/scripts/* (HTTP 404); that
        # is the only expected soft-skip during the transition. Any other failure (transient network,
        # auth, rate limit) stays fatal so a real error can never silently ship a hook-less bundle.
        if ($_.Exception.Message -match 'HTTP 404|Not Found') {
            Write-Warning "Skipping telemetry hook sync for release '$($release.tagName)': hooks not present in this release."
            $hookContents.Clear()
        }
        else {
            throw
        }
    }

    New-Item -ItemType Directory -Force -Path $embeddedDir | Out-Null
    New-Item -ItemType Directory -Force -Path $hooksDir | Out-Null

    foreach ($preparedBundle in $preparedBundles) {
        $definition = $preparedBundle.Definition
        $asset = $preparedBundle.Asset

        Get-ChildItem -Path $embeddedDir -File -Force |
            Where-Object {
                $_.Name -match "^$([regex]::Escape($definition.AssetPrefix))-.*\.(zip|tar\.gz|tgz)$" -and
                $_.Name -ne $asset.name
            } |
            Remove-Item -Force

        Copy-Item -Path $preparedBundle.ArchivePath -Destination (Join-Path $embeddedDir $asset.name) -Force

        $metadata = [ordered]@{
            version = $normalizedVersion
            repository = $Repository
            tag = $release.tagName
            assetName = $asset.name
            sha512 = $preparedBundle.Sha512
        }
        # The telemetry scripts observe both Skills and Extension tools, so every sibling bundle
        # records the same shared hook provenance rather than assigning it to one asset kind.
        if ($null -ne $hookMetadata) {
            $metadata['hooks'] = $hookMetadata
        }

        Set-TextFile -Path (Join-Path $embeddedDir $definition.MetadataFileName) -Content ($metadata | ConvertTo-Json -Depth 10)
        Write-Host "Embedded $($definition.DisplayName) bundle updated to '$($asset.name)' with SHA-512 '$($preparedBundle.Sha512)'."
    }

    foreach ($hookFileName in $hookContents.Keys) {
        [System.IO.File]::WriteAllBytes((Join-Path $hooksDir $hookFileName), $hookContents[$hookFileName])
    }

    $installerContent = Get-Content -Raw -Path $installerPath
    $versionPattern = 'internal const string Version = "[^"]+";'
    $versionMatches = [regex]::Matches($installerContent, $versionPattern)
    if ($versionMatches.Count -ne 1) {
        throw "Expected exactly one Aspire Skills bundle version constant in '$installerPath', but found $($versionMatches.Count)."
    }
    $installerContent = [regex]::Replace(
        $installerContent,
        $versionPattern,
        "internal const string Version = ""$normalizedVersion"";")
    Set-TextFile -Path $installerPath -Content $installerContent

    $cliProjectContent = Get-Content -Raw -Path $cliProjectPath
    foreach ($preparedBundle in $preparedBundles) {
        $prefix = [regex]::Escape($preparedBundle.Definition.AssetPrefix)
        $cliProjectContent = [regex]::Replace(
            $cliProjectContent,
            "Agents\\AspireSkills\\Embedded\\$prefix-[^""]+\.(zip|tar\.gz|tgz)",
            "Agents\AspireSkills\Embedded\$($preparedBundle.Asset.name)")
    }
    Set-TextFile -Path $cliProjectPath -Content $cliProjectContent
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force
    }
}
