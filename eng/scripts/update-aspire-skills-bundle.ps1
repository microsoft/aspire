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

    $metadata = [ordered]@{
        version = $normalizedVersion
        repository = $Repository
        tag = $release.tagName
        assetName = $asset.name
        sha256 = $hash
    }
    Set-TextFile -Path $metadataPath -Content ($metadata | ConvertTo-Json)

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
