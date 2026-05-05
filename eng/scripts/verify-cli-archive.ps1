<#
.SYNOPSIS
    Verify that a signed Aspire CLI archive produces a working binary.

.DESCRIPTION
    This script:
    1. Cleans ~/.aspire to ensure no stale state
    2. Extracts the CLI archive to a temp location
    3. Runs 'aspire --version' to validate the binary executes
    4. Runs 'aspire new aspire-starter' to test bundle self-extraction + starter project creation
    5. Builds the generated starter AppHost project
    6. Enables hidden templates using a temp local config file and runs 'aspire new aspire-apphost' to validate empty AppHost creation
    7. Builds the generated empty AppHost project
    8. Builds representative template project-file scenarios that replaced Aspire.Templates.Tests coverage
    9. Builds the single-file AppHost template
    10. Cleans up temp directories

.PARAMETER ArchivePath
    Path to the CLI archive (.zip or .tar.gz)

.EXAMPLE
    .\verify-cli-archive.ps1 -ArchivePath "artifacts\packages\Release\Shipping\aspire-cli-win-x64-10.0.0.zip"
#>

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

function Write-Step  { param([string]$msg) Write-Host "▶ $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "✅ $msg" -ForegroundColor Green }
function Write-Err   { param([string]$msg) Write-Host "❌ $msg" -ForegroundColor Red }

function Get-UserHome {
    if ($env:USERPROFILE) {
        return $env:USERPROFILE
    }

    if ($env:HOME) {
        return $env:HOME
    }

    throw "Unable to determine the user home directory."
}

function Test-IsWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Set-ExecutablePermission([string]$Path) {
    if (Test-IsWindows) {
        return
    }

    & chmod +x $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to mark '$Path' as executable."
    }
}

function Save-XmlDocument([xml]$Document, [string]$Path) {
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true

    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Get-AppHostSdkVersion([string]$ProjectPath) {
    [xml]$document = Get-Content -Raw $ProjectPath
    $sdkValue = $document.Project.Sdk
    if (-not $sdkValue) {
        throw "Sdk attribute not found in '$ProjectPath'."
    }

    $prefix = "Aspire.AppHost.Sdk/"
    if (-not $sdkValue.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
        throw "Unexpected SDK value '$sdkValue' in '$ProjectPath'."
    }

    return $sdkValue.Substring($prefix.Length)
}

function Add-PackageReference([string]$ProjectPath, [string]$PackageName, [string]$Version = $null) {
    [xml]$document = Get-Content -Raw $ProjectPath
    $project = $document.Project

    $itemGroup = $document.CreateElement("ItemGroup")
    $packageReference = $document.CreateElement("PackageReference")
    $packageReference.SetAttribute("Include", $PackageName)
    if ($Version) {
        $packageReference.SetAttribute("Version", $Version)
    }

    $itemGroup.AppendChild($packageReference) | Out-Null
    $project.AppendChild($itemGroup) | Out-Null
    Save-XmlDocument $document $ProjectPath
}

function Rewrite-AsExplicitSdkReference([string]$ProjectPath, [bool]$IncludeAspireHostingAppHostPackageReference) {
    [xml]$document = Get-Content -Raw $ProjectPath
    $project = $document.Project
    $version = Get-AppHostSdkVersion $ProjectPath

    $project.SetAttribute("Sdk", "Microsoft.NET.Sdk")

    $sdkElement = $document.CreateElement("Sdk")
    $sdkElement.SetAttribute("Name", "Aspire.AppHost.Sdk")
    $sdkElement.SetAttribute("Version", $version)

    if ($project.FirstChild) {
        $project.InsertBefore($sdkElement, $project.FirstChild) | Out-Null
    }
    else {
        $project.AppendChild($sdkElement) | Out-Null
    }

    Save-XmlDocument $document $ProjectPath

    if ($IncludeAspireHostingAppHostPackageReference) {
        Add-PackageReference $ProjectPath "Aspire.Hosting.AppHost" $version
    }
}

function Disable-PackageSourceMapping([string]$NuGetConfigPath) {
    if (-not (Test-Path $NuGetConfigPath)) {
        return
    }

    [xml]$document = Get-Content -Raw $NuGetConfigPath
    $node = $document.SelectSingleNode("/*[local-name()='configuration']/*[local-name()='packageSourceMapping']")
    if ($node) {
        $node.ParentNode.RemoveChild($node) | Out-Null
        Save-XmlDocument $document $NuGetConfigPath
    }
}

function Add-CentralPackageManagementForRedis([string]$ProjectPath, [string]$DirectoryPackagesPropsPath) {
    $version = Get-AppHostSdkVersion $ProjectPath
    Add-PackageReference $ProjectPath "Aspire.Hosting.Redis"

    Set-Content -Path $DirectoryPackagesPropsPath -Value @"
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <NoWarn>NU1507;`$(NoWarn)</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Aspire.Hosting.Redis" Version="$version" />
  </ItemGroup>
</Project>
"@
}

$userHome = Get-UserHome
$verifyTmpDir = $null
$aspireBackup = $null
$dotnetCmd = $null

function Get-DotNetCommand {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    $repoDotNet = if ($IsWindows) {
        Join-Path $repoRoot "dotnet.cmd"
    }
    else {
        Join-Path $repoRoot "dotnet.sh"
    }

    if (Test-Path $repoDotNet) {
        return $repoDotNet
    }

    return (Get-Command dotnet -ErrorAction Stop).Source
}

function Invoke-Cleanup {
    if ($verifyTmpDir -and (Test-Path $verifyTmpDir)) {
        Write-Step "Cleaning up temp directory: $verifyTmpDir"
        Remove-Item -Recurse -Force $verifyTmpDir -ErrorAction SilentlyContinue
    }
    # Restore ~/.aspire if we backed it up
    $aspireDir = Join-Path $userHome ".aspire"
    if ($aspireBackup -and (Test-Path $aspireBackup)) {
        if (Test-Path $aspireDir) {
            Remove-Item -Recurse -Force $aspireDir -ErrorAction SilentlyContinue
        }
        Move-Item $aspireBackup $aspireDir
        Write-Step "Restored original ~/.aspire"
    }
}

try {
    # Validate archive exists
    if (-not (Test-Path $ArchivePath)) {
        Write-Err "Archive not found: $ArchivePath"
        exit 1
    }

    $ArchivePath = (Resolve-Path $ArchivePath).Path

    # Suppress interactive prompts and telemetry
    $env:ASPIRE_CLI_TELEMETRY_OPTOUT = "true"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "true"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "true"
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
    $dotnetCmd = Get-DotNetCommand

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  Aspire CLI Archive Verification"
    Write-Host "=========================================="
    Write-Host "  Archive: $ArchivePath"
    Write-Host "  dotnet:  $dotnetCmd"
    Write-Host "=========================================="
    Write-Host ""

    # Step 1: Back up and clean ~/.aspire
    Write-Step "Cleaning ~/.aspire state..."
    $aspireDir = Join-Path $userHome ".aspire"
    if (Test-Path $aspireDir) {
        $aspireBackup = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-backup-$([System.IO.Path]::GetRandomFileName())"
        Move-Item $aspireDir $aspireBackup
        Write-Step "Backed up existing ~/.aspire to $aspireBackup"
    }
    Write-Ok "Clean ~/.aspire state"

    # Step 2: Extract the archive
    $verifyTmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-verify-$([System.IO.Path]::GetRandomFileName())"
    $extractDir = Join-Path $verifyTmpDir "cli"
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    Write-Step "Extracting archive to $extractDir..."
    if ($ArchivePath.EndsWith(".zip")) {
        Expand-Archive -Path $ArchivePath -DestinationPath $extractDir
    }
    elseif ($ArchivePath.EndsWith(".tar.gz")) {
        tar -xzf $ArchivePath -C $extractDir
        if ($LASTEXITCODE -ne 0) {
            Write-Err "Failed to extract tar.gz archive"
            exit 1
        }
    }
    else {
        Write-Err "Unsupported archive format: $ArchivePath (expected .zip or .tar.gz)"
        exit 1
    }

    # Find the aspire binary
    $aspireBin = Join-Path $extractDir "aspire.exe"
    if (-not (Test-Path $aspireBin)) {
        $aspireBin = Join-Path $extractDir "aspire"
        if (-not (Test-Path $aspireBin)) {
            Write-Err "Could not find 'aspire' binary in extracted archive."
            Get-ChildItem $extractDir | Format-Table
            exit 1
        }
    }
    Write-Ok "Extracted CLI binary: $aspireBin"

    # Install to ~/.aspire/bin so self-extraction works correctly
    Write-Step "Installing CLI to ~/.aspire/bin..."
    $aspireDir = Join-Path $userHome ".aspire"
    $aspireBinDir = Join-Path $aspireDir "bin"
    New-Item -ItemType Directory -Path $aspireBinDir -Force | Out-Null
    Copy-Item $aspireBin (Join-Path $aspireBinDir (Split-Path $aspireBin -Leaf))
    $aspireBin = Join-Path $aspireBinDir (Split-Path $aspireBin -Leaf)
    Set-ExecutablePermission $aspireBin
    $env:PATH = "$aspireBinDir$([System.IO.Path]::PathSeparator)$env:PATH"
    Write-Ok "CLI installed to ~/.aspire/bin"

    # Step 3: Verify aspire --version
    Write-Step "Running 'aspire --version'..."
    $versionOutput = & $aspireBin --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'aspire --version' failed with exit code $LASTEXITCODE"
        Write-Host "Output: $versionOutput"
        exit 1
    }
    Write-Host "  Version: $versionOutput"
    Write-Ok "'aspire --version' succeeded"

    # Step 4: Create a new starter project with aspire new
    # This exercises bundle self-extraction and aspire-managed (template search + download + scaffolding)
    $projectDir = Join-Path $verifyTmpDir "VerifyApp"
    New-Item -ItemType Directory -Path $projectDir -Force | Out-Null

    Write-Step "Running 'aspire new aspire-starter --name VerifyApp --output $projectDir --non-interactive --nologo'..."
    & $aspireBin new aspire-starter --name VerifyApp --output $projectDir --non-interactive --nologo 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'aspire new' failed with exit code $LASTEXITCODE"
        exit 1
    }

    # Verify the project was created
    $appHostDir = Join-Path $projectDir "VerifyApp.AppHost"
    if (-not (Test-Path $appHostDir)) {
        Write-Err "Expected project directory 'VerifyApp.AppHost' not found after 'aspire new'"
        Get-ChildItem $projectDir | Format-Table
        exit 1
    }
    Write-Ok "'aspire new' created project successfully"

    # Step 5: Build the generated starter AppHost project
    $starterAppHostProject = Join-Path $appHostDir "VerifyApp.AppHost.csproj"
    if (-not (Test-Path $starterAppHostProject)) {
        Write-Err "Expected AppHost project '$starterAppHostProject' not found after 'aspire new aspire-starter'"
        exit 1
    }

    Write-Step "Running 'dotnet build $starterAppHostProject'..."
    & $dotnetCmd build $starterAppHostProject 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'dotnet build' failed for starter AppHost with exit code $LASTEXITCODE"
        exit 1
    }
    Write-Ok "Starter AppHost build succeeded"

    # Step 6: Enable hidden templates in a temp local config file so global developer config is untouched
    $hiddenTemplateConfigDir = Join-Path $verifyTmpDir "hidden-template-config"
    New-Item -ItemType Directory -Path $hiddenTemplateConfigDir -Force | Out-Null

    Write-Step "Enabling hidden Aspire templates using temp local config..."
    Push-Location $hiddenTemplateConfigDir
    try {
        & $aspireBin config set features:showAllTemplates true --non-interactive 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire config set features:showAllTemplates' failed with exit code $LASTEXITCODE"
            exit 1
        }
    }
    finally {
        Pop-Location
    }
    Write-Ok "Hidden Aspire templates enabled in temp local config"

    # Step 7: Create an empty .NET AppHost project
    $appHostTemplateDir = Join-Path $verifyTmpDir "VerifyAppHost"
    New-Item -ItemType Directory -Path $appHostTemplateDir -Force | Out-Null

    Push-Location $hiddenTemplateConfigDir
    try {
        Write-Step "Running 'aspire new aspire-apphost --name VerifyAppHost --output $appHostTemplateDir --non-interactive --nologo'..."
        & $aspireBin new aspire-apphost --name VerifyAppHost --output $appHostTemplateDir --non-interactive --nologo 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire new aspire-apphost' failed with exit code $LASTEXITCODE"
            exit 1
        }
    }
    finally {
        Pop-Location
    }

    $emptyAppHostProject = Join-Path $appHostTemplateDir "VerifyAppHost.csproj"
    if (-not (Test-Path $emptyAppHostProject)) {
        Write-Err "Expected AppHost project '$emptyAppHostProject' not found after 'aspire new aspire-apphost'"
        Get-ChildItem $appHostTemplateDir | Format-Table
        exit 1
    }
    Write-Ok "'aspire new aspire-apphost' created project successfully"

    # Step 8: Build the generated empty AppHost project
    Write-Step "Running 'dotnet build $emptyAppHostProject'..."
    & $dotnetCmd build $emptyAppHostProject 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'dotnet build' failed for empty AppHost with exit code $LASTEXITCODE"
        exit 1
    }
    Write-Ok "Empty AppHost build succeeded"

    # Step 9: Verify explicit SDK-reference AppHost project behavior on Windows
    $explicitSdkDir = Join-Path $verifyTmpDir "VerifyExplicitSdkApp"
    New-Item -ItemType Directory -Path $explicitSdkDir -Force | Out-Null

    Push-Location $hiddenTemplateConfigDir
    try {
        Write-Step "Running 'aspire new aspire --name VerifyExplicitSdkApp --output $explicitSdkDir --non-interactive --nologo'..."
        & $aspireBin new aspire --name VerifyExplicitSdkApp --output $explicitSdkDir --non-interactive --nologo 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire new aspire' failed for explicit SDK scenario with exit code $LASTEXITCODE"
            exit 1
        }
    }
    finally {
        Pop-Location
    }

    $explicitSdkAppHostProject = Join-Path $explicitSdkDir "VerifyExplicitSdkApp.AppHost/VerifyExplicitSdkApp.AppHost.csproj"
    if (-not (Test-Path $explicitSdkAppHostProject)) {
        Write-Err "Expected AppHost project '$explicitSdkAppHostProject' not found for explicit SDK scenario"
        exit 1
    }

    Rewrite-AsExplicitSdkReference $explicitSdkAppHostProject $true
    Write-Step "Running 'dotnet build $explicitSdkAppHostProject'..."
    & $dotnetCmd build $explicitSdkAppHostProject 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'dotnet build' failed for explicit SDK AppHost with exit code $LASTEXITCODE"
        exit 1
    }
    Write-Ok "Explicit SDK AppHost build succeeded"

    # Step 10: Verify central package management AppHost project behavior on Windows
    $cpmDir = Join-Path $verifyTmpDir "VerifyCpmApp"
    New-Item -ItemType Directory -Path $cpmDir -Force | Out-Null

    Push-Location $hiddenTemplateConfigDir
    try {
        Write-Step "Running 'aspire new aspire --name VerifyCpmApp --output $cpmDir --non-interactive --nologo'..."
        & $aspireBin new aspire --name VerifyCpmApp --output $cpmDir --non-interactive --nologo 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire new aspire' failed for CPM scenario with exit code $LASTEXITCODE"
            exit 1
        }
    }
    finally {
        Pop-Location
    }

    $cpmAppHostProject = Join-Path $cpmDir "VerifyCpmApp.AppHost/VerifyCpmApp.AppHost.csproj"
    if (-not (Test-Path $cpmAppHostProject)) {
        Write-Err "Expected AppHost project '$cpmAppHostProject' not found for CPM scenario"
        exit 1
    }

    Add-CentralPackageManagementForRedis $cpmAppHostProject (Join-Path $cpmDir "Directory.Packages.props")
    Write-Step "Running 'dotnet build $cpmAppHostProject'..."
    & $dotnetCmd build $cpmAppHostProject 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'dotnet build' failed for CPM AppHost with exit code $LASTEXITCODE"
        exit 1
    }
    Write-Ok "Central package management AppHost build succeeded"

    # Step 11: Verify AppHost package version override behavior on Windows
    $versionedAppHostDir = Join-Path $verifyTmpDir "VerifyVersionedAppHost"
    New-Item -ItemType Directory -Path $versionedAppHostDir -Force | Out-Null

    Push-Location $hiddenTemplateConfigDir
    try {
        Write-Step "Running 'aspire new aspire-apphost --name VerifyVersionedAppHost --output $versionedAppHostDir --non-interactive --nologo'..."
        & $aspireBin new aspire-apphost --name VerifyVersionedAppHost --output $versionedAppHostDir --non-interactive --nologo 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire new aspire-apphost' failed for version override scenario with exit code $LASTEXITCODE"
            exit 1
        }
    }
    finally {
        Pop-Location
    }

    $versionedAppHostProject = Join-Path $versionedAppHostDir "VerifyVersionedAppHost.csproj"
    if (-not (Test-Path $versionedAppHostProject)) {
        Write-Err "Expected AppHost project '$versionedAppHostProject' not found for version override scenario"
        exit 1
    }

    Disable-PackageSourceMapping (Join-Path $versionedAppHostDir "nuget.config")
    Add-PackageReference $versionedAppHostProject "Aspire.Hosting.AppHost" "8.1.0"
    Write-Step "Running 'dotnet build $versionedAppHostProject'..."
    & $dotnetCmd build $versionedAppHostProject 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'dotnet build' failed for version override AppHost with exit code $LASTEXITCODE"
        exit 1
    }
    Write-Ok "Version override AppHost build succeeded"

    # Step 12: Verify single-file AppHost template behavior on Windows
    $singleFileAppHostDir = Join-Path $verifyTmpDir "VerifySingleFileAppHost"
    New-Item -ItemType Directory -Path $singleFileAppHostDir -Force | Out-Null

    Write-Step "Running 'dotnet new aspire-apphost-singlefile --name VerifySingleFileAppHost --output $singleFileAppHostDir'..."
    & $dotnetCmd new aspire-apphost-singlefile --name VerifySingleFileAppHost --output $singleFileAppHostDir 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'dotnet new aspire-apphost-singlefile' failed with exit code $LASTEXITCODE"
        exit 1
    }

    $singleFileAppHostPath = Join-Path $singleFileAppHostDir "apphost.cs"
    if (-not (Test-Path $singleFileAppHostPath)) {
        Write-Err "Expected single-file AppHost '$singleFileAppHostPath' not found"
        exit 1
    }

    Push-Location $singleFileAppHostDir
    try {
        Write-Step "Running 'dotnet build apphost.cs'..."
        & $dotnetCmd build apphost.cs 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'dotnet build' failed for single-file AppHost with exit code $LASTEXITCODE"
            exit 1
        }
    }
    finally {
        Pop-Location
    }
    Write-Ok "Single-file AppHost build succeeded"

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  All verification checks passed!" -ForegroundColor Green
    Write-Host "=========================================="
    Write-Host ""
}
catch {
    Write-Err "Verification failed: $_"
    Write-Host $_.ScriptStackTrace
    exit 1
}
finally {
    Invoke-Cleanup
}
