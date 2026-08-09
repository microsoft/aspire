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
#   -AppId        : GitHub App id for aspire-repo-bot. Required for live
#                   upload; optional in -DryRun (when omitted, the auth
#                   chain is skipped but every other check still runs).
#   -PrivateKeyPem: PEM private key for the App. Same rules as -AppId.
#   -Owner / -Repo: GitHub repo coordinates (default microsoft/aspire).
#   -DryRun       : Verify only, skip the gh release upload.
#
# Behavior:
#   1. Lists every aspire-cli-*.sha512 in $AssetsDir, verifies its archive's
#      hash, and fails fast on the first mismatch (corruption shouldn't ship).
#   2. Runs `gh --version` to confirm the GitHub CLI is installed on the
#      agent — the original failure that motivated this check was a missing
#      `gh` on the AzDO release pool image.
#   3. If credentials are provided, mints an installation access token via
#      Get-AspireBotInstallationToken.ps1 and runs `gh auth status` to
#      confirm the bot can authenticate to github.com. (This only validates
#      auth against github.com, not release-level permissions on a specific
#      tag; checking release-level perms reliably would require the release
#      to already exist, which is not guaranteed at dry-run time because
#      release-github-tasks.yml creates it in a sibling job.)
#   4. In DryRun mode, prints what would be uploaded and exits 0.
#      In live mode, runs `gh release upload --clobber` so re-runs are
#      idempotent. Credentials are required for live upload.
#
# Dry-run runs as much as possible without credentials so the script can be
# exercised on the AzDO agent itself to confirm host prerequisites (archive
# verification, gh on PATH) before a live publish run. When the pipeline
# invokes dry-run it does pass the credentials in, so CI dry-runs continue
# to exercise the full auth chain.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssetsDir,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter()][string]$AppId,
    [Parameter()][string]$PrivateKeyPem,
    [Parameter()][string]$Owner = 'microsoft',
    [Parameter()][string]$Repo = 'aspire',
    [Parameter()][switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$appIdProvided  = -not [string]::IsNullOrWhiteSpace($AppId)
$keyProvided    = -not [string]::IsNullOrWhiteSpace($PrivateKeyPem)
$hasCredentials = $appIdProvided -and $keyProvided

# Reject partial credentials before either branch below can swallow them.
# Without this check, an AzDO variable-group reference that silently resolves
# empty for just one of the two values would flip $hasCredentials to false,
# and a -DryRun invocation would take the credential-less path and exit 0 —
# masking a broken variable group behind a green dry-run.
if ($appIdProvided -ne $keyProvided) {
    $missing = if ($appIdProvided) { 'PrivateKeyPem' } else { 'AppId' }
    Write-Host "ERROR: $missing is empty but the other credential is set. AppId and PrivateKeyPem must be supplied together (or both omitted, only in -DryRun mode). This usually indicates a misconfigured AzDO variable group reference." -ForegroundColor Red
    exit 1
}

if (-not $DryRun -and -not $hasCredentials) {
    # Live upload requires real credentials. An AzDO variable group reference
    # that resolves to '' (rather than failing outright) would otherwise reach
    # the token mint with empty inputs.
    Write-Host "ERROR: AppId and PrivateKeyPem are required when not running in -DryRun mode." -ForegroundColor Red
    exit 1
}

Write-Host "=== Publish Release CLI Assets ==="
Write-Host "AssetsDir: $AssetsDir"
Write-Host "Tag:       $Tag"
Write-Host "Target:    $Owner/$Repo"
Write-Host "DryRun:    $DryRun"

if (-not (Test-Path $AssetsDir)) {
    Write-Host "ERROR: Assets directory '$AssetsDir' does not exist. Did the download step run?" -ForegroundColor Red
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
    Write-Host "ERROR: No aspire-cli-* archives found under '$AssetsDir'. This release likely predates the CLI binaries, or the download step pulled the wrong artifact." -ForegroundColor Red
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
        Write-Host "ERROR: No .sha512 companion found for $($archive.Name) (expected at $shaPath)." -ForegroundColor Red
        exit 1
    }

    # .sha512 files emitted by the build are a single line: <hex-hash>[whitespace<filename>].
    # Some build flavors emit just the hash; tolerate both by taking the first token.
    $expected = ((Get-Content $shaPath -Raw).Trim() -split '\s+')[0].ToLower()
    $actual = (Get-FileHash -Algorithm SHA512 $archive.FullName).Hash.ToLower()

    if ($expected -ne $actual) {
        Write-Host @"
ERROR: SHA512 mismatch for $($archive.Name):
  expected: $expected
  actual:   $actual
Aborting upload — corrupted artifacts should never ship.
"@ -ForegroundColor Red
        exit 1
    }

    $verified++
    Write-Host "  ✓ $($archive.Name)"
}
Write-Host "Verified $verified archive(s)."

# Confirm gh is installed and runnable on the agent. Placed after SHA512
# verification on purpose: corrupted artifacts are a higher-priority diagnostic
# than a missing CLI, so let the more important failure surface first.
#
# The pipeline installs gh and prepends it to PATH in the step preceding this
# script, so in CI this should always succeed. The try/catch + $LASTEXITCODE
# split is there to give a meaningful diagnostic if either the install step
# regresses or the script is invoked locally without gh on PATH.
Write-Host ""
Write-Host "Verifying gh CLI is installed and runnable..."
try {
    & gh --version
}
catch [System.Management.Automation.CommandNotFoundException] {
    Write-Host "ERROR: gh is not on PATH. Install GitHub CLI from https://cli.github.com/ (or, in CI, check the 'Install GitHub CLI' pipeline step)." -ForegroundColor Red
    exit 1
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: gh --version exited with code $LASTEXITCODE. The GitHub CLI is on PATH but failed to run." -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not $hasCredentials) {
    # Dry-run without credentials: skip token mint + auth check, but still
    # report what would be uploaded so a release manager gets useful output.
    Write-Host ""
    Write-Host "No credentials provided; skipping installation-token mint and gh auth check (DryRun)."
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
    Write-Host "ERROR: Failed to acquire installation access token from Get-AspireBotInstallationToken.ps1" -ForegroundColor Red
    exit 1
}

# Hand the token to gh via GH_TOKEN. --clobber on the live upload makes
# re-runs idempotent. The try/finally spans both the auth check and the
# live upload so the token is scrubbed from the environment even if we
# exit early.
$env:GH_TOKEN = $installationToken
try {
    Write-Host ""
    Write-Host "Verifying gh can authenticate to github.com..."
    & gh auth status --hostname github.com
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: gh auth status failed with exit code $LASTEXITCODE." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    if ($DryRun) {
        Write-Host ""
        Write-Host "🔍 [DRY RUN] Would upload the following to release $Tag in ${Owner}/${Repo}:"
        foreach ($f in $allFiles) {
            $sizeMB = [math]::Round($f.Length / 1MB, 2)
            Write-Host "  - $($f.Name) ($sizeMB MB)"
        }
        Write-Host ""
        Write-Host "[DRY RUN] Skipping gh release upload."
        # Exit inside the try block; PowerShell still runs the finally
        # so the token gets cleared from the environment.
        exit 0
    }

    Write-Host ""
    Write-Host "Uploading $($allFiles.Count) asset(s) to release $Tag..."
    $filePaths = $allFiles | ForEach-Object { $_.FullName }
    & gh release upload $Tag @filePaths --repo "$Owner/$Repo" --clobber
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: gh release upload failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "✓ Uploaded $($allFiles.Count) asset(s) to $Tag."
}
finally {
    # Don't leave the token in the environment after this script returns.
    Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
}
