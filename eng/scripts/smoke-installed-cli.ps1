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
    $base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
    $WorkDir = Join-Path $base 'aspire-cli-smoke'
}

aspire --version

if (Test-Path $WorkDir) {
    Remove-Item -Recurse -Force $WorkDir
}

New-Item -ItemType Directory -Path $WorkDir | Out-Null

Push-Location $WorkDir
try {
    aspire --log-level $LogLevel new aspire-starter --name $ProjectName --output . --non-interactive --nologo --suppress-agent-init
    aspire --log-level $LogLevel restore --non-interactive --nologo
}
finally {
    Pop-Location
}
