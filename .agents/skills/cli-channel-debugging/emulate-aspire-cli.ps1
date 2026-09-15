#!/usr/bin/env pwsh
#
# emulate-aspire-cli.ps1 — DOT-SOURCE this to make the current PowerShell session drive a
# locally built Aspire CLI that emulates a given build identity. It sets the ASPIRE_CLI_*
# identity overrides and defines an `aspire` function pointing at this repo's built CLI.
#
#   . .agents/skills/cli-channel-debugging/emulate-aspire-cli.ps1 <channel> [options]
#
# (Note the leading dot + space — dot-sourcing is required for the function/env to persist.)
#
# Channels:
#   stable | daily            version auto-resolved from the matching feed (override with -Version)
#   staging                   requires -Commit; version auto-resolved from the darc feed
#   pr-<N> | local            version NOT auto-resolved; pass -Version to set one
#
# After dot-sourcing, just run `aspire <command>` (e.g. `aspire --version`).

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Channel,

    [string] $Version,
    [string] $Commit,
    [string] $Packages,
    [string] $Config = 'Debug',
    [switch] $NoBuild
)

$repoRoot = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    Write-Error "emulate-aspire-cli: could not locate the repository root from $PSScriptRoot"
    return
}

# Auto-resolve the version for feed-backed channels unless the caller pinned one.
if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($Channel -in @('stable', 'daily', 'staging')) {
        $resolver = Join-Path $PSScriptRoot 'get-aspire-channel-version.ps1'
        $resolverArgs = @($Channel)
        if ($Channel -eq 'staging') {
            if ([string]::IsNullOrWhiteSpace($Commit)) {
                Write-Error 'emulate-aspire-cli: staging requires -Commit <sha> (see docs/cli-staging-validation.md)'
                return
            }
            $resolverArgs += @('-Commit', $Commit)
        }
        Write-Host "emulate-aspire-cli: resolving latest '$Channel' version..." -ForegroundColor DarkGray
        $Version = & $resolver @resolverArgs
        if ([string]::IsNullOrWhiteSpace($Version)) {
            Write-Error "emulate-aspire-cli: failed to resolve a version for '$Channel'"
            return
        }
    }
}

# Build (or locate) the CLI. The override only changes identity/decisions — it never
# materializes package bytes — so a normal local build of the CLI is all we need.
$cliDll = Join-Path $repoRoot "artifacts/bin/Aspire.Cli/$Config/net10.0/aspire.dll"
if (-not $NoBuild) {
    Write-Host "emulate-aspire-cli: building Aspire.Cli ($Config)..." -ForegroundColor DarkGray
    $env:MSBUILDTERMINALLOGGER = 'false'
    dotnet build (Join-Path $repoRoot 'src/Aspire.Cli/Aspire.Cli.csproj') -c $Config -p:SkipNativeBuild=true -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'emulate-aspire-cli: build failed'
        return
    }
}
if (-not (Test-Path $cliDll)) {
    Write-Error "emulate-aspire-cli: CLI binary not found at $cliDll (drop -NoBuild to build it)"
    return
}

$env:ASPIRE_CLI_CHANNEL = $Channel
if ($Version) { $env:ASPIRE_CLI_VERSION = $Version } else { Remove-Item Env:\ASPIRE_CLI_VERSION -ErrorAction SilentlyContinue }
if ($Commit) { $env:ASPIRE_CLI_COMMIT = $Commit } else { Remove-Item Env:\ASPIRE_CLI_COMMIT -ErrorAction SilentlyContinue }
if ($Packages) { $env:ASPIRE_CLI_PACKAGES = $Packages } else { Remove-Item Env:\ASPIRE_CLI_PACKAGES -ErrorAction SilentlyContinue }

# Define the `aspire` function for this session. $script:AspireEmulatedCliDll captures the
# resolved path so the function keeps working after this script returns.
$script:AspireEmulatedCliDll = $cliDll
function global:aspire { dotnet $script:AspireEmulatedCliDll @args }

$summary = "channel=$($env:ASPIRE_CLI_CHANNEL) version=$(if ($env:ASPIRE_CLI_VERSION) { $env:ASPIRE_CLI_VERSION } else { '<unset>' })"
if ($env:ASPIRE_CLI_COMMIT) { $summary += " commit=$($env:ASPIRE_CLI_COMMIT)" }
if ($env:ASPIRE_CLI_PACKAGES) { $summary += " packages=$($env:ASPIRE_CLI_PACKAGES)" }
Write-Host "emulate-aspire-cli: ready — $summary" -ForegroundColor DarkGray
Write-Host "emulate-aspire-cli: run 'aspire --version' to confirm the emulation banner." -ForegroundColor DarkGray
