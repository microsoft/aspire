<#
.SYNOPSIS
    Generates a Catalog Definition File (.cdf) and optionally runs makecat.exe to
    produce a signed catalog (.cat) file.
.DESCRIPTION
    Recursively scans a directory for files matching a filter and produces a .cdf
    file suitable for makecat.exe. Optionally invokes makecat.exe to generate the
    .cat file directly.

    Used in component team pipelines (Arcade SDK, MicroBuild, or custom) to
    catalog-sign customer-modifiable or non-PE files that cannot use direct
    Authenticode signing.

    If -RunMakecat is specified, the script finds and runs makecat.exe automatically.
    Otherwise, it prints the commands to run manually.
.PARAMETER RootPath
    The directory containing files to include in the catalog.
.PARAMETER CdfPath
    The output path for the .cdf file. If not specified and CatOutputPath is set,
    defaults to the CatOutputPath with a .cdf extension.
.PARAMETER CatOutputPath
    The path where makecat.exe will create the .cat file. Defaults to the CDF
    path with a .cat extension.
.PARAMETER Filter
    File filter pattern (e.g., '*.js', '*.xml', '*.ttf'). Default: '*.*' (all files).
.PARAMETER RunMakecat
    If specified, finds and runs makecat.exe to produce the .cat file.
.PARAMETER WindowsSdkDir
    Optional path to the Windows SDK. Used to locate makecat.exe when -RunMakecat is set.
.PARAMETER ErrorIfMakecatNotFound
    If specified with -RunMakecat, throws an error when makecat.exe is not found
    instead of warning and skipping. Use in CI/official builds.
.EXAMPLE
    # Generate CDF only (print manual steps):
    .\generate-catalog.ps1 -RootPath ".\content" -CdfPath ".\obj\my-files.cdf"
.EXAMPLE
    # Generate CDF and run makecat.exe:
    .\generate-catalog.ps1 -RootPath ".\content" -CatOutputPath ".\obj\my-files.cat" -Filter "*.js" -RunMakecat
.EXAMPLE
    # CI usage (error if makecat.exe not found):
    .\generate-catalog.ps1 -RootPath "$(_ContentRoot)" -CatOutputPath "$(_CatOutputPath)" -Filter "*.js" -RunMakecat -ErrorIfMakecatNotFound
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RootPath,

    [string]$CdfPath,

    [string]$CatOutputPath,

    [string]$Filter = '*.*',

    [string]$WindowsSdkDir = '',

    [switch]$RunMakecat,

    [switch]$ErrorIfMakecatNotFound
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $RootPath)) {
    Write-Error "Root path not found: $RootPath"
    return
}

# Resolve paths: need at least one of CdfPath or CatOutputPath
if (-not $CdfPath -and -not $CatOutputPath) {
    Write-Error "Specify at least one of -CdfPath or -CatOutputPath."
    return
}
if (-not $CatOutputPath) {
    $CatOutputPath = [System.IO.Path]::ChangeExtension($CdfPath, '.cat')
}
if (-not $CdfPath) {
    $CdfPath = [System.IO.Path]::ChangeExtension($CatOutputPath, '.cdf')
}

# Ensure output directories exist
$cdfDir = Split-Path $CdfPath -Parent
if ($cdfDir -and -not (Test-Path $cdfDir)) {
    New-Item -ItemType Directory -Path $cdfDir -Force | Out-Null
}
$catDir = Split-Path $CatOutputPath -Parent
if ($catDir -and -not (Test-Path $catDir)) {
    New-Item -ItemType Directory -Path $catDir -Force | Out-Null
}

$files = Get-ChildItem -Path $RootPath -Recurse -Filter $Filter -File
if ($files.Count -eq 0) {
    Write-Warning "No files matching '$Filter' found under $RootPath - skipping catalog generation."
    return
}

$cdfContent = @()
$cdfContent += "[CatalogHeader]"
$cdfContent += "Name=$CatOutputPath"
$cdfContent += "CatalogVersion=2"
$cdfContent += "HashAlgorithms=SHA256"
$cdfContent += ""
$cdfContent += "[CatalogFiles]"

$i = 0
foreach ($f in $files) {
    $ext = $f.Extension.TrimStart('.').ToLower()
    $label = "${ext}_${i}_" + ($f.Name -replace '[^\w\.-]', '_')
    $cdfContent += "<hash>$label=`"$($f.FullName)`""
    $i++
}

$cdfContent | Set-Content -Path $CdfPath -Encoding ASCII

Write-Host "Generated CDF with $($files.Count) file(s) matching '$Filter' at $CdfPath"

if ($RunMakecat) {
    # Find makecat.exe — ships with the Windows SDK.
    # Probe versioned directories directly instead of expensive recursive search.
    $makecat = $null
    if ($WindowsSdkDir -and (Test-Path $WindowsSdkDir)) {
        $binDir = Join-Path $WindowsSdkDir 'bin'
        if (Test-Path $binDir) {
            $makecat = Get-ChildItem -Path $binDir -Directory |
                Where-Object { $_.Name -match '^\d+\.\d+' } |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\makecat.exe' } |
                Where-Object { Test-Path $_ } |
                Select-Object -First 1 |
                ForEach-Object { Get-Item $_ }
        }
    }
    if (-not $makecat) { $makecat = Get-Command makecat.exe -ErrorAction SilentlyContinue }
    if (-not $makecat) {
        $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        if (Test-Path $sdkRoot) {
            $makecat = Get-ChildItem -Path $sdkRoot -Directory |
                Where-Object { $_.Name -match '^\d+\.\d+' } |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\makecat.exe' } |
                Where-Object { Test-Path $_ } |
                Select-Object -First 1 |
                ForEach-Object { Get-Item $_ }
        }
    }
    if (-not $makecat) {
        if ($ErrorIfMakecatNotFound) {
            throw "makecat.exe not found. Catalog signing requires the Windows SDK."
        }
        Write-Warning "makecat.exe not found - skipping catalog generation. Install Windows SDK for catalog signing."
        return
    }

    $makecatPath = if ($makecat -is [System.Management.Automation.CommandInfo]) { $makecat.Source } else { $makecat.FullName }
    Write-Host "Using makecat.exe at: $makecatPath"

    & $makecatPath $CdfPath
    if ($LASTEXITCODE -ne 0) {
        throw "makecat.exe failed with exit code $LASTEXITCODE"
    }

    Write-Host "Generated catalog file: $CatOutputPath"
} else {
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Run: makecat.exe `"$CdfPath`""
    Write-Host "  2. Sign: dotnet ddsignfiles.dll -- /file:`"$CatOutputPath`" /certs:Microsoft400"
}
