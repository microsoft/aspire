#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Shared implementation for debug-staging.ps1 and debug-stable.ps1.

.DESCRIPTION
    Makes an EASY-TO-GET Aspire CLI build behave like an official release-branch
    staging build for validating package feed routing, WITHOUT producing a real
    official build or stamping a binary locally.

    The recommended carrier is a PR build (a real, full self-extracting ~/.aspire
    install) acquired with eng/scripts/get-aspire-cli-pr.ps1 <PR>. Any installed
    'aspire' (or a locally built one via -Cli) works just as well, because the
    behavior is driven entirely by two diagnostic config overrides read by
    PackagingService (see docs/cli-staging-validation.md):

      overrideCliIdentityChannel       - forces the identity used for staging-feed
                                         routing decisions (here: 'staging').
      overrideCliInformationalVersion  - forces the informational version the SHA
                                         derivation and version-shape (quality)
                                         checks read, e.g. 13.4.0-preview.1.x+<sha>.

    Both flow into IConfiguration from environment variables (used here) OR from
    aspire.config.json, and are scoped to staging feed routing only. A CLI run
    with them set emits a one-time warning so they can never silently mis-route a
    normal invocation.

    The script runs 'aspire add <package> --debug' in a throwaway directory whose
    aspire.config.json pins channel: staging, then asserts the debug log contains
      Resolved 'staging' channel: feed=<expected darc feed>, quality=<expected>
    The 'aspire add' step is expected to fail later (there is no real apphost
    project in the scratch directory); only the feed-routing log line is validated.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Default version shapes for the 13.4 release branch. Override with -Version.
$script:DefaultStagingVersion = '13.4.0-preview.1.26280.6'
$script:DefaultStableVersion = '13.4.0'

function Invoke-DebugChannel {
    [CmdletBinding()]
    param(
        # 'staging' (prerelease-shaped) | 'stable' (stable-shaped)
        [Parameter(Mandatory = $true)][ValidateSet('staging', 'stable')][string]$Kind,
        [string]$Sha,
        [string]$Pr,
        [string]$Cli,
        [string]$Version,
        [string]$Identity = 'staging',
        [string]$Package = 'foundry',
        [switch]$Shell,
        [switch]$PrintEnv,
        [string[]]$PassThrough = @()
    )

    switch ($Kind) {
        'staging' { $kindLabel = 'staging (prerelease-shaped)'; $defaultVersion = $script:DefaultStagingVersion; $expectedQuality = 'Both' }
        'stable'  { $kindLabel = 'staging (stable-shaped)';     $defaultVersion = $script:DefaultStableVersion;  $expectedQuality = 'Stable' }
    }

    if ([string]::IsNullOrEmpty($Sha)) {
        Write-Error '-Sha is required.'
        return
    }

    # The darc feed name is built from the first 8 chars of the commit hash, so
    # require at least that many hex characters (full hashes are accepted).
    if ($Sha -notmatch '^[0-9a-fA-F]{8,40}$') {
        Write-Error "-Sha must be 8-40 hexadecimal characters (got '$Sha')."
        return
    }

    if ([string]::IsNullOrEmpty($Version)) { $Version = $defaultVersion }

    $sha8 = $Sha.Substring(0, 8).ToLowerInvariant()
    $expectedFeed = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-$sha8/nuget/v3/index.json"
    $infoVersion = "$Version+$Sha"

    # -PrintEnv: emit shell-applicable env assignments and stop. CLI-agnostic on
    # purpose -- the three keys drive ANY 'aspire' on PATH. Intended use:
    #   ./debug-staging.ps1 -Sha <commit> -PrintEnv | Invoke-Expression
    if ($PrintEnv) {
        Write-Output "# $kindLabel build (sha $sha8, feed darc-pub-microsoft-aspire-$sha8, quality $expectedQuality)."
        Write-Output "# Apply to your current PowerShell session, then run aspire commands normally."
        Write-Output "`$env:channel = 'staging'"
        Write-Output "`$env:overrideCliIdentityChannel = '$Identity'"
        Write-Output "`$env:overrideCliInformationalVersion = '$infoVersion'"
        Write-Output "# To revert:"
        Write-Output "# Remove-Item Env:channel, Env:overrideCliIdentityChannel, Env:overrideCliInformationalVersion"
        return
    }

    $scriptDir = Split-Path -Parent $PSCommandPath

    # Optionally install the PR build first; it becomes the default target CLI.
    if (-not [string]::IsNullOrEmpty($Pr)) {
        Write-Host ">> Installing PR #$Pr build via get-aspire-cli-pr.ps1 ..."
        & (Join-Path $scriptDir 'get-aspire-cli-pr.ps1') $Pr
    }

    if ([string]::IsNullOrEmpty($Cli)) {
        $onPath = Get-Command aspire -ErrorAction SilentlyContinue
        $installed = Join-Path $HOME '.aspire/bin/aspire'
        if ($onPath) {
            $Cli = $onPath.Source
        }
        elseif (Test-Path $installed) {
            $Cli = $installed
        }
        else {
            Write-Error "No aspire CLI found. Install a PR build (-Pr <N>), pass -Cli <path>, or put 'aspire' on PATH."
            return
        }
    }
    if (-not (Test-Path $Cli)) {
        Write-Error "CLI path '$Cli' does not exist."
        return
    }
    # Resolve to an absolute path because the validation step runs from a scratch
    # working directory, where a relative -Cli would no longer resolve.
    $Cli = (Resolve-Path $Cli).Path

    Write-Host ''
    Write-Host "Simulating an official $kindLabel build"
    Write-Host "  CLI:               $Cli"
    Write-Host "  identity override: $Identity"
    Write-Host "  version override:  $infoVersion"
    Write-Host "  expected feed:     $expectedFeed"
    Write-Host "  expected quality:  $expectedQuality"
    Write-Host ''

    # -Shell: start a child PowerShell where the target CLI behaves like this build
    # for every 'aspire' command. The overrides live only in the child process'
    # environment, so closing it fully restores normal behavior. The CLI's directory
    # is put first on PATH so a bare 'aspire' resolves to the target build.
    if ($Shell) {
        $cliDir = Split-Path -Parent $Cli
        # Redirect NuGet's global packages folder to an isolated, per-sha directory
        # so packages restored from the simulated staging feed (which can collide in
        # version with packages already cached from real feeds) never contaminate the
        # developer's real global cache (~/.nuget/packages by default). The directory
        # is keyed by the simulated sha so repeat sessions reuse the same isolated
        # cache, and is left in place on exit (it lives under the system temp dir).
        $nugetPackages = Join-Path ([System.IO.Path]::GetTempPath()) (Join-Path 'aspire-debug-nuget' $sha8)
        New-Item -ItemType Directory -Path $nugetPackages -Force | Out-Null
        Write-Host '>> Launching a child PowerShell. Run aspire new, aspire add, etc.'
        Write-Host "   'aspire' resolves to: $Cli"
        Write-Host "   NuGet packages cache: $nugetPackages (isolated from your global cache)"
        Write-Host "   Type 'exit' to leave and restore normal CLI behavior."
        Write-Host ''
        $env:channel = 'staging'
        $env:overrideCliIdentityChannel = $Identity
        $env:overrideCliInformationalVersion = $infoVersion
        $env:NUGET_PACKAGES = $nugetPackages
        $env:PATH = "$cliDir$([System.IO.Path]::PathSeparator)$env:PATH"
        & (Get-Process -Id $PID).Path -NoExit -NoLogo
        return
    }

    # Throwaway working directory pinned to channel: staging so 'aspire add'
    # filters to the synthesized staging channel. No real apphost project lives
    # here, so 'add' will ultimately fail after feed routing has already been
    # logged -- that is expected.
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("aspire-debug-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        @'
{
  "channel": "staging"
}
'@ | Set-Content -Path (Join-Path $scratch 'aspire.config.json') -Encoding utf8

        $log = Join-Path $scratch 'aspire-debug.log'
        Write-Host ">> Running: aspire add $Package --debug $($PassThrough -join ' ')"
        Write-Host '   (feed routing is logged before the add step fails on the missing apphost)'
        Write-Host ''

        # The overrides are scoped to THIS invocation only (set then removed), so
        # they can't leak into the developer's other aspire commands. 'aspire add'
        # is allowed to exit non-zero; success is decided by the log.
        $previousChannel = $env:overrideCliIdentityChannel
        $previousVersion = $env:overrideCliInformationalVersion
        $previousLocation = Get-Location
        try {
            $env:overrideCliIdentityChannel = $Identity
            $env:overrideCliInformationalVersion = $infoVersion
            Set-Location $scratch
            $cliArgs = @('add', $Package, '--debug') + $PassThrough
            & $Cli @cliArgs *> $log
        }
        finally {
            Set-Location $previousLocation
            $env:overrideCliIdentityChannel = $previousChannel
            $env:overrideCliInformationalVersion = $previousVersion
        }

        $logText = Get-Content -Path $log -Raw -ErrorAction SilentlyContinue
        if ($null -eq $logText) { $logText = '' }

        # Echo the resolution + override-warning lines for visibility.
        Get-Content -Path $log -ErrorAction SilentlyContinue |
            Where-Object { $_ -match "diagnostic overrides are active|Resolved 'staging' channel|Refusing to synthesize|Could not synthesize" } |
            ForEach-Object { Write-Host $_ }
        Write-Host ''

        $expectedLine = "Resolved 'staging' channel: feed=$expectedFeed"
        if ($logText -notmatch [regex]::Escape($expectedLine)) {
            Write-Host $logText
            Write-Error "FAILED: did not resolve the expected darc feed. Expected: $expectedLine, quality=$expectedQuality"
            return
        }
        if ($logText -notmatch [regex]::Escape("$expectedLine, quality=$expectedQuality")) {
            Write-Error "FAILED: resolved the darc feed but quality was not '$expectedQuality'."
            return
        }

        Write-Host "PASSED: $kindLabel build resolves Aspire.* from the darc feed with quality=$expectedQuality."
        Write-Host ''
        Write-Host "Equivalent persistent 'config options' (drop into the apphost's aspire.config.json"
        Write-Host 'to simulate this build interactively with an installed PR build):'
        Write-Host ''
        Write-Host @"
{
  "channel": "staging",
  "overrideCliIdentityChannel": "$Identity",
  "overrideCliInformationalVersion": "$infoVersion"
}
"@
        Write-Host ''
        Write-Host 'Remove those override keys when you are done -- they are for local validation only.'
    }
    finally {
        Remove-Item -Path $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
