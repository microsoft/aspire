<#
.SYNOPSIS
Runs aspire new with the configuration-source demo settings.
.DESCRIPTION
Uses the installed CLI by default. Use -Cli source to build and run the CLI from
this checkout with published packages rather than requiring a local package hive. In the template
picker, choose "Search for more templates" and leave the search empty to list custom templates.
Defaults to aspire new. The aspire add launcher reuses this setup with the caller's working directory.
Source mode uses the checkout's pinned SDK to build the CLI, even when the demo app pins a different SDK.
It runs the built CLI directly so child dotnet commands honor the demo app's SDK pin.
Source builds display only errors.
.EXAMPLE
.\eng\scripts\config-sources-demo\run-aspire-new.ps1
.EXAMPLE
& 'C:\Development\Source\aspire\eng\scripts\config-sources-demo\run-aspire-new.ps1' -WorkingDirectory $PWD.Path
.EXAMPLE
.\eng\scripts\config-sources-demo\run-aspire-new.ps1 -Cli source
#>
[CmdletBinding()]
param(
    [string] $WorkingDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "aspire-config-demo-$([guid]::NewGuid().ToString('N'))"),

    [ValidateSet('new', 'add')]
    [string] $Command = 'new',

    [string[]] $CommandArguments = @(),

    [ValidateSet('source', 'installed')]
    [string] $Cli = 'installed'
)

$ErrorActionPreference = 'Stop'
$settings = @{
    ASPIRE_INIT_SOURCE_URL = 'https://msft.ghe.com/coreai/aspire-1p-init-test.git'
    ASPIRE_INIT_SOURCE_PATH = 'aspire.config.json'
    ASPIRE_INIT_SOURCE_REF = 'jamesnk/source-demo'
    ASPIRE_INIT_SOURCE_README_URL = 'https://msft.ghe.com/coreai/aspire-1p-init-test/blob/jamesnk/source-demo/README.md'
}

if ($Cli -eq 'source')
{
    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
    $version = & (Join-Path $repoRoot '.agents\skills\cli-channel-debugging\get-aspire-channel-version.ps1') stable
    $settings.ASPIRE_CLI_CHANNEL = 'stable'
    $settings.ASPIRE_CLI_VERSION = $version
    $settings.ASPIRE_CLI_COMMIT = $null
    $settings.ASPIRE_CLI_PACKAGES = $null
    $settings.ASPIRE_CLI_NUGET_SERVICE_INDEX = $null
    $settings.MSBUILDTERMINALLOGGER = 'false'
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
    if ($Cli -eq 'source')
    {
        Write-Host "Emulating stable $version with the source-built CLI and published packages."
        # dotnet run passes its SDK paths to descendants. Build separately so the app's
        # child dotnet commands can select their own SDK instead of mixing SDK versions.
        Push-Location -LiteralPath $repoRoot
        try
        {
            $cliProject = Join-Path $repoRoot 'src\Aspire.Cli\Aspire.Cli.csproj'
            # Capture the bootstrapper's SDK-path output so successful builds stay silent.
            $buildOutput = & (Join-Path $repoRoot 'dotnet.cmd') build $cliProject `
                --configuration Debug -p:SkipNativeBuild=true --nologo --verbosity quiet '-clp:ErrorsOnly;NoSummary'
            $buildExitCode = $LASTEXITCODE
            if ($buildExitCode -ne 0)
            {
                $buildOutput | Out-Host
                throw "CLI build failed with exit code $buildExitCode."
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
    }
    else
    {
        $cliExecutable = (Get-Command aspire -CommandType Application -ErrorAction Stop).Source
        Write-Host "Using the installed Aspire CLI: $cliExecutable"
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
