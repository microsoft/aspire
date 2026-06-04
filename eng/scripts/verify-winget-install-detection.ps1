<#
.SYNOPSIS
    Verifies an already-installed Aspire CLI looks like a winget install end-to-end.

.DESCRIPTION
    Run AFTER 'winget install --manifest ...' has placed the CLI on PATH and AFTER
    at least one CLI command has been invoked (so the first-run probe and the
    bundle extraction have both had a chance to run). Asserts:

      1. 'aspire doctor --format json --self' reports the running install with
         route == "winget". This is the highest-signal assertion: the route is
         only "winget" when WindowsRegistryReader matched a winget ARP entry and
         WingetFirstRunProbe stamped {"source":"winget"} into the sidecar.

      2. The .aspire-install.json sidecar exists next to the resolved binary
         (or the resolved-target binary if it's a symlink) with source=winget.

      3. The Aspire bundle was extracted under the winget install location, NOT
         under $HOME\.aspire — i.e. the route-driven extract-dir logic in
         BundleService.ComputeDefaultExtractDir picked the winget colocation
         branch instead of the sidecar-less fallback.

    Any failed assertion exits non-zero with a diagnostic dump so the CI log
    captures both the JSON and the surrounding filesystem state. This script
    guards specifically against silent regressions in winget-install detection:
    the smoke test alone passes even when the bundle ends up in the wrong place,
    because aspire new + restore work fine from the $HOME fallback layout too.

.PARAMETER ExpectedVersion
    Optional. When supplied, also asserts installations[0].version contains the
    expected version string. Useful for confirming the PR build (not a stale
    machine-wide install) is the one that produced the report.
#>

[CmdletBinding()]
param(
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Resolve-AspireBinary {
    $cmd = Get-Command aspire -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw "'aspire' is not on PATH. Did the winget install step run?"
    }
    # Follow one level of symlink/junction so we land on the real exe winget
    # extracted, not the alias winget may have layered in WinGet\Links. Use
    # Get-Item -Force to resolve reparse points without throwing on plain files.
    $item = Get-Item -LiteralPath $cmd.Source -Force
    if ($item.Target) {
        $real = $item.Target | Select-Object -First 1
        if (-not [System.IO.Path]::IsPathRooted($real)) {
            $real = Join-Path (Split-Path $cmd.Source -Parent) $real
        }
        return [System.IO.Path]::GetFullPath($real)
    }
    return [System.IO.Path]::GetFullPath($cmd.Source)
}

function Get-DoctorSelfJson {
    Write-Host "Running: aspire doctor --format json --self"
    $raw = aspire doctor --format json --self
    if ($LASTEXITCODE -ne 0) {
        throw "aspire doctor --format json --self exited $LASTEXITCODE; output: $raw"
    }
    try {
        return $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Failed to parse aspire doctor JSON output. Raw output:`n$raw"
    }
}

function Format-Object($obj) {
    return ($obj | ConvertTo-Json -Depth 8)
}

$failures = New-Object System.Collections.Generic.List[string]
$report = [ordered]@{}

$realBinary = Resolve-AspireBinary
$binaryDir = Split-Path $realBinary -Parent
$report['ResolvedBinary'] = $realBinary
$report['BinaryDirectory'] = $binaryDir

$doctor = Get-DoctorSelfJson
$report['DoctorJson'] = $doctor

$installs = $doctor.installations
if (-not $installs -or $installs.Count -eq 0) {
    $failures.Add("aspire doctor --self returned no installations entry.")
}
else {
    $self = $installs[0]
    $report['SelfInstallation'] = $self

    if ($self.route -ne 'winget') {
        $failures.Add("Expected installations[0].route == 'winget', got '$($self.route)'. This means WindowsRegistryReader did not match the winget ARP entry and the sidecar was never stamped.")
    }

    if ($ExpectedVersion -and $self.version -notlike "*$ExpectedVersion*") {
        $failures.Add("Expected installations[0].version to contain '$ExpectedVersion', got '$($self.version)'. The reporting CLI may not be the freshly-installed one.")
    }
}

# Sidecar assertion. The probe writes {"source":"winget"} into a file named
# .aspire-install.json beside the binary. If route reported winget but this file
# is missing, the probe took a non-canonical write path and we need to know.
$sidecarPath = Join-Path $binaryDir '.aspire-install.json'
$report['SidecarPath'] = $sidecarPath
$report['SidecarExists'] = Test-Path -LiteralPath $sidecarPath -PathType Leaf
if (-not $report['SidecarExists']) {
    $failures.Add("Expected sidecar file '$sidecarPath' to exist after first run, but it was not found.")
}
else {
    $sidecarText = Get-Content -LiteralPath $sidecarPath -Raw
    $report['SidecarContent'] = $sidecarText
    try {
        $sidecar = $sidecarText | ConvertFrom-Json -ErrorAction Stop
        if ($sidecar.source -ne 'winget') {
            $failures.Add("Sidecar source field expected 'winget', got '$($sidecar.source)'.")
        }
    }
    catch {
        $failures.Add("Sidecar at '$sidecarPath' is not valid JSON: $sidecarText")
    }
}

# Bundle-location assertion. After ComputeDefaultExtractDir picks the winget
# colocation branch, the bundle/ link (or directory) lives alongside the binary.
# If it instead lives under $HOME\.aspire, the route detection silently fell
# back even if route happens to be reported correctly through a different path.
$wingetBundleDir = Join-Path $binaryDir 'bundle'
$report['ExpectedBundleDir'] = $wingetBundleDir
$report['ExpectedBundleDirExists'] = Test-Path -LiteralPath $wingetBundleDir
$aspireHomeBundleDir = Join-Path $env:USERPROFILE '.aspire\bundle'
$report['FallbackBundleDir'] = $aspireHomeBundleDir
$report['FallbackBundleDirExists'] = Test-Path -LiteralPath $aspireHomeBundleDir

if (-not $report['ExpectedBundleDirExists']) {
    $failures.Add("Expected the Aspire bundle to be extracted under the winget install directory at '$wingetBundleDir', but that directory does not exist. Fallback location '$aspireHomeBundleDir' exists: $($report['FallbackBundleDirExists']).")
}

# A bundle under $HOME\.aspire after a winget install means BundleService
# colocation did not select the winget branch. Even if the winget directory
# also exists (e.g. from a previous run), the fallback being populated
# indicates the route detection regressed.
if ($report['FallbackBundleDirExists']) {
    $failures.Add("Fallback bundle directory '$aspireHomeBundleDir' must not exist after a winget install; BundleService should colocate the bundle under '$wingetBundleDir'. Its presence indicates winget install detection regressed.")
}

Write-Host ''
Write-Host '=== verify-winget-install-detection report ==='
Write-Host (Format-Object $report)

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host '=== Failures ==='
    # $ErrorActionPreference is 'Stop' for this script; Write-Error would
    # throw on the first failure and abort the loop, surfacing only one
    # failure to the operator. Use Write-Host so every failure is printed
    # before the explicit `exit 1` returns the non-zero status to CI.
    foreach ($f in $failures) {
        Write-Host -ForegroundColor Red $f
    }
    exit 1
}

Write-Host ''
Write-Host 'All winget install-detection assertions passed.'
