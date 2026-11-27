#requires -Version 7.0
<#
.SYNOPSIS
Scaffolds and compiles a Go or Java starter using same-build CLI and packages.
.DESCRIPTION
Run inside the CodeQL Initialize/Finalize tracing window. Both the AppHost
(including the generated SDK runtime) and its API are compiled, but never run.
Keep OutputDirectory under the repository scan root and outside the playground
and tests exclusions. Leave generated sources in place until CodeQL Finalize.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('go', 'java')]
    [string]$Language,

    [Parameter(Mandatory)]
    [string]$CliPath,

    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path

$appHostPackages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter 'Aspire.Hosting.AppHost.*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' })
if ($appHostPackages.Count -ne 1) {
    throw "Expected exactly one same-build Aspire.Hosting.AppHost package, found $($appHostPackages.Count)."
}

# For example, Aspire.Hosting.AppHost.13.5.0-preview.1.26310.9.nupkg.
# Read its nuspec instead of splitting the filename or guessing the CLI channel's
# latest version: the build's exact version must drive template and SDK restore.
$archive = [IO.Compression.ZipFile]::OpenRead($appHostPackages[0].FullName)
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
    if ($entries.Count -ne 1) {
        throw 'AppHost package must contain exactly one nuspec.'
    }
    $stream = $entries[0].Open()
    try {
        $nuspec = [Xml.Linq.XDocument]::Load($stream)
        $ns = $nuspec.Root.Name.Namespace
        $metadata = $nuspec.Root.Element($ns + 'metadata')
        if ($metadata.Element($ns + 'id').Value -ne 'Aspire.Hosting.AppHost') {
            throw 'AppHost package nuspec has an unexpected package ID.'
        }
        $version = $metadata.Element($ns + 'version').Value
        if ([string]::IsNullOrWhiteSpace($version)) {
            throw 'AppHost package nuspec is missing its version.'
        }
    } finally {
        $stream.Dispose()
    }
} finally {
    $archive.Dispose()
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Output directory '$OutputDirectory' already exists. CodeQL starter builds require fresh sources and outputs."
}
$OutputDirectory = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$projectDirectory = Join-Path $OutputDirectory 'starter'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Command $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

# Isolate the CLI state and dependency caches from other jobs and any developer
# installation. The local channel pins Aspire packages to this build's artifact;
# non-Aspire NuGet dependencies continue to use the approved mirror.
$environment = @{
    ASPIRE_HOME = Join-Path $OutputDirectory 'aspire-home'
    ASPIRE_CLI_CHANNEL = 'local'
    ASPIRE_CLI_VERSION = $version
    ASPIRE_CLI_PACKAGES = $PackageDirectory
    ASPIRE_CLI_NUGET_SERVICE_INDEX = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json'
    ASPIRE_CLI_TELEMETRY_OPTOUT = 'true'
    ASPIRE_REPO_ROOT = $null
    DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
    NUGET_PACKAGES = Join-Path $OutputDirectory 'nuget-packages'
    GOCACHE = Join-Path $OutputDirectory 'go-cache'
    GOMODCACHE = Join-Path $OutputDirectory 'go-modules'
    GRADLE_USER_HOME = Join-Path $OutputDirectory 'gradle'
}
$savedEnvironment = @{}

try {
    foreach ($name in $environment.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }

    Write-Host "Scaffolding $Language starter using Aspire $version and packages at '$PackageDirectory'."
    $feature = if ($Language -eq 'go') { 'experimentalPolyglotGo' } else { 'experimentalPolyglotJava' }
    Invoke-CheckedCommand $CliPath @('--nologo', 'config', 'set', "features:$feature", 'true', '--global')
    Invoke-CheckedCommand $CliPath @('--nologo', 'new', "aspire-$Language-starter", '--name', 'CodeqlStarter',
        '--output', $projectDirectory, '--version', $version, '--channel', 'local', '--non-interactive',
        '--localhost-tld', 'false', '--suppress-agent-init')

    Push-Location $projectDirectory
    try {
        if ($Language -eq 'go') {
            Invoke-CheckedCommand 'go' @('mod', 'tidy')
            Invoke-CheckedCommand 'go' @('build', '-buildvcs=false', '-o', (Join-Path $OutputDirectory 'apphost.exe'), '.')
        } else {
            $sourceList = Join-Path $projectDirectory '.aspire' 'modules' 'sources.txt'
            if (-not (Test-Path -LiteralPath $sourceList -PathType Leaf)) {
                throw "Generated Java SDK source list not found at '$sourceList'."
            }
            Invoke-CheckedCommand 'javac' @('--release', '25', '-d', (Join-Path $OutputDirectory 'java-classes'),
                '@.aspire/modules/sources.txt', 'AppHost.java')
        }
    } finally {
        Pop-Location
    }

    Push-Location (Join-Path $projectDirectory 'api')
    try {
        if ($Language -eq 'go') {
            Invoke-CheckedCommand 'go' @('mod', 'tidy')
            Invoke-CheckedCommand 'go' @('build', '-buildvcs=false', '-o', (Join-Path $OutputDirectory 'api.exe'), '.')
        } else {
            # A Gradle daemon is outside the traced process tree. Disable both
            # its daemon and build cache so CodeQL observes actual compilation.
            # https://aka.ms/codeql-tsg-onboard-compiled
            $gradle = if ($IsWindows) { '.\gradlew.bat' } else { './gradlew' }
            Invoke-CheckedCommand $gradle @('--no-daemon', '--no-build-cache', '--init-script',
                (Join-Path $PSScriptRoot 'codeql-gradle-init.gradle'), 'clean', 'classes')
        }
    } finally {
        Pop-Location
    }

    Write-Host "Compiled $Language starter AppHost, generated SDK, and API for CodeQL extraction."
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
