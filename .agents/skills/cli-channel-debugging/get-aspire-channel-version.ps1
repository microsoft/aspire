#!/usr/bin/env pwsh
#
# get-aspire-channel-version.ps1 — resolve the latest Aspire package version for a
# CLI-emulation channel, so it can feed ASPIRE_CLI_VERSION (see this skill's SKILL.md).
#
# Each value maps to the feed the Aspire CLI's built-in package channels resolve
# `Aspire*` from (see src/Aspire.Cli/Packaging/PackagingService.cs):
#   stable   -> nuget.org                              (https://api.nuget.org/v3-flatcontainer)
#   daily    -> dnceng/public "dotnet9" feed           (.../_packaging/dotnet9/nuget/v3/flat2)
#   staging  -> dnceng/public "darc-pub-microsoft-aspire-<sha8>" feed (needs -Commit)
#
# Only the resolved version is written to stdout (diagnostics go to the host/stderr), so:
#   $env:ASPIRE_CLI_VERSION = (./get-aspire-channel-version.ps1 daily)
#
# Usage:
#   ./get-aspire-channel-version.ps1 stable
#   ./get-aspire-channel-version.ps1 daily
#   ./get-aspire-channel-version.ps1 staging -Commit <sha>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('stable', 'daily', 'staging')]
    [string] $Channel,

    [string] $Package = 'Aspire.Hosting.AppHost',

    # Required for staging; the staging build's source commit.
    [string] $Commit,

    # For daily/staging, restrict to stable-shaped (non-prerelease) versions.
    [switch] $StableOnly,

    # For stable, allow prerelease versions from nuget.org.
    [switch] $Prerelease
)

$ErrorActionPreference = 'Stop'

function Compare-AspireVersion {
    # Returns -1, 0, or 1 comparing two version strings by SemVer precedence.
    # A stable version sorts ABOVE a prerelease with the same MAJOR.MINOR.PATCH.
    param([string] $A, [string] $B)

    $rx = '^(\d+)\.(\d+)\.(\d+)(?:-(.+?))?(?:\+.*)?$'
    $ma = [regex]::Match($A, $rx)
    $mb = [regex]::Match($B, $rx)
    foreach ($i in 1..3) {
        $x = [int] $ma.Groups[$i].Value
        $y = [int] $mb.Groups[$i].Value
        if ($x -ne $y) { return [Math]::Sign($x - $y) }
    }

    $preA = if ($ma.Groups[4].Success) { $ma.Groups[4].Value } else { $null }
    $preB = if ($mb.Groups[4].Success) { $mb.Groups[4].Value } else { $null }
    if ($null -eq $preA -and $null -eq $preB) { return 0 }
    if ($null -eq $preA) { return 1 }   # stable > prerelease
    if ($null -eq $preB) { return -1 }

    $idsA = $preA.Split('.')
    $idsB = $preB.Split('.')
    for ($i = 0; $i -lt [Math]::Max($idsA.Count, $idsB.Count); $i++) {
        if ($i -ge $idsA.Count) { return -1 }
        if ($i -ge $idsB.Count) { return 1 }
        $pa = $idsA[$i]; $pb = $idsB[$i]
        $na = 0; $nb = 0
        $isNumA = [int]::TryParse($pa, [ref] $na)
        $isNumB = [int]::TryParse($pb, [ref] $nb)
        if ($isNumA -and $isNumB) {
            if ($na -ne $nb) { return [Math]::Sign($na - $nb) }
        }
        elseif ($isNumA) { return -1 }   # numeric identifiers sort below alphanumeric
        elseif ($isNumB) { return 1 }
        else {
            $c = [string]::CompareOrdinal($pa, $pb)
            if ($c -ne 0) { return [Math]::Sign($c) }
        }
    }
    return 0
}

$pkgLower = $Package.ToLowerInvariant()
switch ($Channel) {
    'stable' {
        $feedUrl = "https://api.nuget.org/v3-flatcontainer/$pkgLower/index.json"
        $latestStable = -not $Prerelease
    }
    'daily' {
        $feedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/flat2/$pkgLower/index.json"
        $latestStable = [bool] $StableOnly
    }
    'staging' {
        if ([string]::IsNullOrWhiteSpace($Commit)) {
            throw 'staging requires -Commit <sha> (the staging build''s commit; see docs/cli-staging-validation.md)'
        }
        # The CLI truncates the commit to 8 lowercase hex chars to build the darc feed name.
        $sha8 = $Commit.ToLowerInvariant().Substring(0, [Math]::Min(8, $Commit.Length))
        $feedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-$sha8/nuget/v3/flat2/$pkgLower/index.json"
        $latestStable = [bool] $StableOnly
    }
}

$modeLabel = if ($latestStable) { 'latest-stable' } else { 'latest-any' }
Write-Host "Resolving '$Package' on '$Channel' feed ($modeLabel):" -ForegroundColor DarkGray
Write-Host "  $feedUrl" -ForegroundColor DarkGray

try {
    $data = Invoke-RestMethod -Uri $feedUrl -TimeoutSec 30
}
catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -eq 404) {
        throw 'feed returned 404 — package or feed does not exist (check id/commit)'
    }
    throw "failed to fetch/parse feed: $($_.Exception.Message)"
}

# A NuGet v3 flat-container/flat2 index returns { "versions": [ ... ] }. The array is NOT
# reliably sorted (the dnceng feeds interleave old 9.x and current 13.x builds), so sort here.
$versions = @($data.versions)
if ($versions.Count -eq 0) { throw 'feed returned no versions for this package' }

if ($latestStable) {
    $versions = @($versions | Where-Object { $_ -notmatch '-' })
    if ($versions.Count -eq 0) { throw 'no versions matched the requested filter' }
}

$max = $versions[0]
foreach ($v in $versions) {
    if ((Compare-AspireVersion $v $max) -gt 0) { $max = $v }
}
Write-Output $max
