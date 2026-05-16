# Verifies SHA512 hashes for every aspire-cli-* archive in a staging
# directory and uploads them as assets to a GitHub release as the
# aspire-repo-bot GitHub App.
#
# This replaces the manual `Sync-ReleaseCliAssets.ps1` script that release
# managers used to run from their workstation. The pipeline now downloads
# the same files from the signed source build's BlobArtifacts via the
# standard `- download: aspire-build` step, so this script only has to
# handle verification + upload.
#
# Inputs:
#   -AssetsDir    : Directory containing aspire-cli-* archives and their
#                   matching .sha512 companion files.
#   -Tag          : The release tag to upload to (e.g. v13.0.0).
#   -AppId        : GitHub App id for aspire-repo-bot.
#   -PrivateKeyPem: PEM private key for the App.
#   -Owner / -Repo: GitHub repo coordinates (default microsoft/aspire).
#   -DryRun       : Verify only, skip the upload.
#
# Behavior:
#   - Lists every aspire-cli-*.sha512 in $AssetsDir, verifies its archive's
#     hash, and fails fast on the first mismatch (corruption shouldn't ship).
#   - In DryRun mode, prints what would be uploaded and exits 0.
#   - Otherwise mints an installation token via Get-AspireBotInstallationToken.ps1
#     and uses `gh release upload --clobber` so re-runs are idempotent.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssetsDir,
    [Parameter(Mandatory = $true)][string]$Tag,
    # AppId / PrivateKeyPem are only required for the actual upload — a dry-run
    # only verifies SHA512s and never needs to mint a token.
    [Parameter()][string]$AppId,
    [Parameter()][string]$PrivateKeyPem,
    [Parameter()][string]$Owner = 'microsoft',
    [Parameter()][string]$Repo = 'aspire',
    [Parameter()][switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $DryRun) {
    if ([string]::IsNullOrWhiteSpace($AppId) -or [string]::IsNullOrWhiteSpace($PrivateKeyPem)) {
        Write-Error "AppId and PrivateKeyPem are required when not running in -DryRun mode."
        exit 1
    }
}

Write-Host "=== Publish Release CLI Assets ==="
Write-Host "AssetsDir: $AssetsDir"
Write-Host "Tag:       $Tag"
Write-Host "Target:    $Owner/$Repo"
Write-Host "DryRun:    $DryRun"

if (-not (Test-Path $AssetsDir)) {
    Write-Error "Assets directory '$AssetsDir' does not exist. Did the download step run?"
    exit 1
}

# Enumerate archives. The download step uses pattern 'aspire-cli-*' which pulls
# both the archives (aspire-cli-<rid>-<version>.zip / .tar.gz) and their
# .sha512 companion files. The archives are everything matching aspire-cli-*
# minus the .sha512 files.
$allFiles = @(Get-ChildItem -Path $AssetsDir -Recurse -File -Filter 'aspire-cli-*')
$shaFiles = @($allFiles | Where-Object { $_.Name -like '*.sha512' })
$archives = @($allFiles | Where-Object { $_.Name -notlike '*.sha512' })

if ($archives.Count -eq 0) {
    Write-Error "No aspire-cli-* archives found under '$AssetsDir'. This release likely predates the CLI binaries, or the download step pulled the wrong artifact."
    exit 1
}

Write-Host "Found $($archives.Count) archive(s) and $($shaFiles.Count) .sha512 companion(s)."

# Verify every archive against its .sha512 companion. Fail fast on the first
# mismatch — we shouldn't be uploading corrupted bits.
Write-Host ""
Write-Host "Verifying SHA512 hashes..."
$verified = 0
foreach ($archive in $archives) {
    $shaPath = "$($archive.FullName).sha512"
    if (-not (Test-Path $shaPath)) {
        Write-Error "No .sha512 companion found for $($archive.Name) (expected at $shaPath)."
        exit 1
    }

    # .sha512 files emitted by the build are a single line: <hex-hash>[whitespace<filename>].
    # Some build flavors emit just the hash; tolerate both by taking the first token.
    $expected = ((Get-Content $shaPath -Raw).Trim() -split '\s+')[0].ToLower()
    $actual = (Get-FileHash -Algorithm SHA512 $archive.FullName).Hash.ToLower()

    if ($expected -ne $actual) {
        Write-Error @"
SHA512 mismatch for $($archive.Name):
  expected: $expected
  actual:   $actual
Aborting upload — corrupted artifacts should never ship.
"@
        exit 1
    }

    $verified++
    Write-Host "  ✓ $($archive.Name)"
}
Write-Host "Verified $verified archive(s)."

if ($DryRun) {
    Write-Host ""
    Write-Host "🔍 [DRY RUN] Would upload the following to release $Tag in ${Owner}/${Repo}:"
    foreach ($f in $allFiles) {
        $sizeMB = [math]::Round($f.Length / 1MB, 2)
        Write-Host "  - $($f.Name) ($sizeMB MB)"
    }
    Write-Host ""
    Write-Host "[DRY RUN] Skipping gh release upload."
    exit 0
}

# Mint an installation token for the bot via the shared helper.
$tokenScript = Join-Path $PSScriptRoot 'Get-AspireBotInstallationToken.ps1'
$installationToken = & $tokenScript -AppId $AppId -PrivateKeyPem $PrivateKeyPem -Owner $Owner -Repo $Repo
if ([string]::IsNullOrWhiteSpace($installationToken)) {
    Write-Error "Failed to acquire installation access token from Get-AspireBotInstallationToken.ps1"
    exit 1
}

# Hand the token to gh via GH_TOKEN. --clobber overwrites existing assets so
# this step is idempotent (matches the local script's behavior).
$env:GH_TOKEN = $installationToken
try {
    Write-Host ""
    Write-Host "Uploading $($allFiles.Count) asset(s) to release $Tag..."
    $filePaths = $allFiles | ForEach-Object { $_.FullName }
    & gh release upload $Tag @filePaths --repo "$Owner/$Repo" --clobber
    if ($LASTEXITCODE -ne 0) {
        Write-Error "gh release upload failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "✓ Uploaded $($allFiles.Count) asset(s) to $Tag."
}
finally {
    # Don't leave the token in the environment after this script returns.
    Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
}
