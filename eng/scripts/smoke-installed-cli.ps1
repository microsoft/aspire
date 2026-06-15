<#
.SYNOPSIS
    Smoke-tests the already-installed aspire CLI.

.DESCRIPTION
    Scaffolds an aspire-starter project and runs its restore, against whatever
    'aspire' is first on PATH. Assumes the CLI has already been installed (via
    WinGet manifest, dotnet-tool, Homebrew cask, archive script, etc.).

    Catches regressions that only show up once the installed bits actually
    launch — broken launcher resolution, missing layout assets, packaging-time
    PATH issues, etc.
#>

[CmdletBinding()]
param(
    [string]$WorkDir,
    [string]$ProjectName = 'SmokeApp',
    [string]$LogLevel = 'trace'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
}
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

# Always scaffold into a fresh subdirectory created under $WorkDir. This
# deliberately avoids ever Remove-Item -Recurse -Force'ing a caller-provided
# path: even if -WorkDir points at a sensitive directory, the worst case is a
# new empty aspire-cli-smoke.XXXXXXXX subdirectory being created underneath.
# CI tears down $env:RUNNER_TEMP between jobs; local users can clean up whenever.
$scaffoldDir = Join-Path $WorkDir ("aspire-cli-smoke." + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $scaffoldDir | Out-Null
Write-Host "Scaffolding into: $scaffoldDir"

aspire --version

Push-Location $scaffoldDir
try {
    aspire --log-level $LogLevel new aspire-starter --name $ProjectName --output . --non-interactive --nologo --suppress-agent-init
    aspire --log-level $LogLevel restore --non-interactive --nologo
}
finally {
    Pop-Location
}
