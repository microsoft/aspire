<#
.SYNOPSIS
Generates the template component manifest using packages from this build.
.DESCRIPTION
Run after managed packing and before Component Governance scanning.
The manifest is generated under artifacts/cg/templates and is never checked in.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$PackageDirectory,
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $RepositoryRoot 'artifacts' 'packages' $Configuration 'Shipping'
}
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Shipping packages not found at '$PackageDirectory'. Run the managed build with -pack first."
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$templatePackages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter 'Aspire.ProjectTemplates.*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' })
if ($templatePackages.Count -ne 1) {
    throw "Expected exactly one Aspire.ProjectTemplates package in '$PackageDirectory'; found $($templatePackages.Count). Remove stale template package versions."
}

# Read the version from the same-build package, not the environment's PR/daily suffix.
# No builds, installs, or restores of templated applications are performed here.
$archive = [IO.Compression.ZipFile]::OpenRead($templatePackages[0].FullName)
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
    if ($entries.Count -ne 1) { throw 'Template package must contain exactly one nuspec.' }
    $stream = $entries[0].Open()
    try {
        $nuspec = [Xml.Linq.XDocument]::Load($stream)
        $ns = $nuspec.Root.Name.Namespace
        $version = $nuspec.Root.Element($ns + 'metadata').Element($ns + 'version').Value
    } finally { $stream.Dispose() }
} finally { $archive.Dispose() }

$project = Join-Path $RepositoryRoot 'src' 'Aspire.ProjectTemplates' 'Aspire.ProjectTemplates.csproj'
$dotnet = if ($IsWindows) { Join-Path $RepositoryRoot 'dotnet.cmd' } else { Join-Path $RepositoryRoot 'dotnet.sh' }
& $dotnet msbuild $project -target:GenerateTemplateCgManifest "-property:Configuration=$Configuration" "-property:PackageVersion=$version" `
    "-property:TemplateCgPackageDirectory=$PackageDirectory" -verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    throw 'Template component manifest generation failed.'
}
