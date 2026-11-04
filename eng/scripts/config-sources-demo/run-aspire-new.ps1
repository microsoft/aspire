<#
.SYNOPSIS
Runs aspire new from this checkout with the configuration-source demo settings.
.DESCRIPTION
Uses published packages rather than requiring a local package hive. In the template
picker, choose "Search for more templates" and leave the search empty to list custom templates.
Defaults to aspire new. The aspire add launcher reuses this setup with the caller's working directory.
Uses the checkout's pinned SDK to build the CLI, even when the demo app pins a different SDK.
Runs the built CLI directly so child dotnet commands honor the demo app's SDK pin.
.EXAMPLE
.\eng\scripts\config-sources-demo\run-aspire-new.ps1
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-new.ps1' -WorkingDirectory $PWD.Path
#>
[CmdletBinding()]
param(
    [string] $WorkingDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "aspire-config-demo-$([guid]::NewGuid().ToString('N'))"),

    [ValidateSet('new', 'add')]
    [string] $Command = 'new',

    [string[]] $CommandArguments = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$version = & (Join-Path $repoRoot '.agents\skills\cli-channel-debugging\get-aspire-channel-version.ps1') stable
$settings = @{
    ASPIRE_INIT_SOURCE_URL = 'https://msft.ghe.com/coreai/aspire-1p-init-test.git'
    ASPIRE_INIT_SOURCE_PATH = 'aspire.config.json'
    ASPIRE_INIT_SOURCE_REF = 'jamesnk/source-demo'
    ASPIRE_CLI_CHANNEL = 'stable'
    ASPIRE_CLI_VERSION = $version
    ASPIRE_CLI_COMMIT = $null
    ASPIRE_CLI_PACKAGES = $null
    ASPIRE_CLI_NUGET_SERVICE_INDEX = $null
    MSBUILDTERMINALLOGGER = 'false'
}

$previousSettings = @{}
try
{
    foreach ($name in $settings.Keys)
    {
        $previousSettings[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }

    $workspace = New-Item -ItemType Directory -Path $WorkingDirectory -Force
    Write-Host "Demo workspace: $($workspace.FullName)"
    Write-Host "Emulating stable $version with the source-built CLI and published packages."
    # dotnet run passes its SDK paths to descendants. Build separately so the app's
    # child dotnet commands can select their own SDK instead of mixing SDK versions.
    Push-Location -LiteralPath $repoRoot
    try
    {
        $cliProject = Join-Path $repoRoot 'src\Aspire.Cli\Aspire.Cli.csproj'
        & (Join-Path $repoRoot 'dotnet.cmd') build $cliProject --configuration Debug -p:SkipNativeBuild=true
        if ($LASTEXITCODE -ne 0)
        {
            throw "CLI build failed with exit code $LASTEXITCODE."
        }

        $dotnetRoot = (Get-Content -LiteralPath (Join-Path $repoRoot 'artifacts\toolset\sdk.txt') -Raw).Trim()
        $dotnet = Join-Path $dotnetRoot 'dotnet.exe'
        $cliExecutable = & $dotnet msbuild $cliProject -nologo -p:Configuration=Debug -getProperty:RunCommand
        if ($LASTEXITCODE -ne 0)
        {
            throw "Failed to locate the CLI build output with exit code $LASTEXITCODE."
        }
    }
    finally
    {
        Pop-Location
    }

    Push-Location -LiteralPath $workspace.FullName
    try
    {
        & $cliExecutable $Command @CommandArguments
        if ($LASTEXITCODE -ne 0)
        {
            throw "aspire $Command failed with exit code $LASTEXITCODE."
        }
    }
    finally
    {
        Pop-Location
    }
}
finally
{
    foreach ($name in $previousSettings.Keys)
    {
        [Environment]::SetEnvironmentVariable($name, $previousSettings[$name], 'Process')
    }
}
