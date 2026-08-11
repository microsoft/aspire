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
$metadataPath = Join-Path $embeddedDir 'aspire-skills.metadata.json'
$installerPath = Join-Path $repoRoot 'src\Aspire.Cli\Agents\AspireSkills\AspireSkillsInstaller.cs'
$cliProjectPath = Join-Path $repoRoot 'src\Aspire.Cli\Aspire.Cli.csproj'
$hooksDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\Hooks'

. (Join-Path $scriptDir 'aspire-skills-bundle.common.ps1')

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
    if (-not (Test-Path $metadataPath)) {
        throw "Embedded Aspire skills metadata was not found at '$metadataPath'. Pass -Version to choose the initial version."
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

    throw "Could not find an Aspire skills release for version '$NormalizedVersion' in '$Repository'."
}

function Get-ReleaseAsset($Release, [string]$NormalizedVersion) {
    $assetNameCandidates = foreach ($archiveExtension in @('.zip', '.tar.gz', '.tgz')) {
        "aspire-skills-v$NormalizedVersion$archiveExtension"
        "aspire-skills-$NormalizedVersion$archiveExtension"
    }

    foreach ($assetName in $assetNameCandidates) {
        $asset = $Release.assets | Where-Object { $_.name -ieq $assetName } | Select-Object -First 1
        if ($null -ne $asset) {
            return $asset
        }
    }

    throw "Release '$($Release.tagName)' does not contain a supported Aspire skills archive asset for version '$NormalizedVersion'."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI ('gh') is required to update the embedded Aspire skills bundle."
}

$normalizedVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
    Get-CurrentEmbeddedVersion
}
else {
    Get-UnprefixedVersion $Version
}

New-Item -ItemType Directory -Force -Path $embeddedDir | Out-Null

Write-Host "Resolving Aspire skills release '$normalizedVersion' from '$Repository'..."
$release = Get-GitHubRelease $normalizedVersion
$asset = Get-ReleaseAsset $release $normalizedVersion

$tempDir = [System.IO.Directory]::CreateTempSubdirectory('aspire-skills-update-').FullName
try {
    Write-Host "Downloading '$($asset.name)' from '$Repository' release '$($release.tagName)'..."
    Invoke-GitHubCli release download $release.tagName --repo $Repository --pattern $asset.name --dir $tempDir --clobber

    $archivePath = Join-Path $tempDir $asset.name
    if (-not (Test-Path $archivePath)) {
        throw "Expected downloaded asset '$archivePath' was not found."
    }

    $certIdentity = "https://github.com/$Repository/.github/workflows/publish.yml@refs/tags/$($release.tagName)"
    Write-Host "Verifying GitHub artifact attestation for '$($asset.name)'..."
    Invoke-GitHubCli attestation verify $archivePath --repo $Repository --cert-identity $certIdentity --cert-oidc-issuer 'https://token.actions.githubusercontent.com'

    $hash = (Get-FileHash -Algorithm SHA256 $archivePath).Hash.ToLowerInvariant()
    $targetArchivePath = Join-Path $embeddedDir $asset.name

    Get-ChildItem -Path $embeddedDir -File -Force |
        Where-Object { $_.Name -match '^aspire-skills-.*\.(zip|tar\.gz|tgz)$' -and $_.Name -ne $asset.name } |
        Remove-Item -Force

    Copy-Item -Path $archivePath -Destination $targetArchivePath -Force

    # Sync the telemetry hook scripts from the same release. Hooks are SOURCE files in aspire-skills
    # (hooks/scripts/track-telemetry.{sh,ps1}), so they are pinned to the immutable commit the release
    # tag points at and fetched via the contents API (see aspire-skills-bundle.common.ps1). Releases
    # that predate the telemetry hooks feature do not contain hooks/scripts/*, so a missing hook is a
    # warning + skip during the transition rather than a hard failure of the whole bundle update;
    # verification only enforces hooks once they are recorded in metadata.
    New-Item -ItemType Directory -Force -Path $hooksDir | Out-Null
    $hookMetadata = $null
    try {
        $hookCommitSha = Get-AspireSkillsReleaseCommitSha -Repository $Repository -Tag $release.tagName

        # Fetch every hook first and only write to disk + record metadata once all fetches succeed.
        # Writing inside the fetch loop could leave one fresh + one stale file (and no hooks metadata)
        # if a later fetch failed, after which verify-aspire-skills-bundle.ps1 would silently skip hook
        # verification. Collecting first makes the on-disk update atomic.
        $hookContents = [ordered]@{}
        $hookHashes = [ordered]@{}
        foreach ($hookFileName in Get-AspireSkillsHookFileNames) {
            Write-Host "Syncing hook script '$hookFileName' from '$Repository' at commit '$hookCommitSha'..."
            $hookBytes = Get-AspireSkillsHookContent -Repository $Repository -CommitSha $hookCommitSha -FileName $hookFileName
            $hookContents[$hookFileName] = $hookBytes
            $hookHashes[$hookFileName] = Get-AspireSkillsSha256Hex -Bytes $hookBytes
        }

        foreach ($hookFileName in $hookContents.Keys) {
            [System.IO.File]::WriteAllBytes((Join-Path $hooksDir $hookFileName), $hookContents[$hookFileName])
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
        }
        else {
            throw
        }
    }

    $metadata = [ordered]@{
        version = $normalizedVersion
        repository = $Repository
        tag = $release.tagName
        assetName = $asset.name
        sha256 = $hash
    }
    if ($null -ne $hookMetadata) {
        $metadata['hooks'] = $hookMetadata
    }
    Set-TextFile -Path $metadataPath -Content ($metadata | ConvertTo-Json -Depth 10)

    $installerContent = Get-Content -Raw -Path $installerPath
    $installerContent = [regex]::Replace(
        $installerContent,
        'internal const string Version = "[^"]+";',
        "internal const string Version = ""$normalizedVersion"";")
    Set-TextFile -Path $installerPath -Content $installerContent

    $cliProjectContent = Get-Content -Raw -Path $cliProjectPath
    $cliProjectContent = [regex]::Replace(
        $cliProjectContent,
        'Agents\\AspireSkills\\Embedded\\aspire-skills-[^"]+\.(zip|tar\.gz|tgz)',
        "Agents\AspireSkills\Embedded\$($asset.name)")
    Set-TextFile -Path $cliProjectPath -Content $cliProjectContent

    Write-Host "Embedded Aspire skills bundle updated to '$($asset.name)' with SHA-256 '$hash'."
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force
    }
}
