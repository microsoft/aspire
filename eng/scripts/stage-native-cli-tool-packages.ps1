param(
  [Parameter(Mandatory = $true)]
  [string]$DownloadRoot,

  [Parameter(Mandatory = $true)]
  [string]$ShippingDir,

  [string]$CanonicalPointerArtifactName = 'native_archives_win_x64',

  [string]$NpmPackageFilePrefix = 'microsoft-aspire-cli',

  [switch]$RequireNpmPackages
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $ShippingDir -Force | Out-Null

$packageVersionPattern = '(?<Version>\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)'

function Get-NativeArchiveArtifact {
  param([System.IO.FileInfo]$Package)

  $pathParts = $Package.FullName.Split([char[]]@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)
  $nativeArchiveRoot = @($pathParts | Where-Object { $_ -like 'native_archives_*' } | Select-Object -First 1)

  if ($nativeArchiveRoot.Count -eq 0) {
    return $null
  }

  $artifactName = $nativeArchiveRoot[0]
  return [pscustomobject]@{
    Name = $artifactName
    Rid = $artifactName.Substring('native_archives_'.Length).Replace('_', '-')
  }
}

function New-ClassifiedPackage {
  param(
    [System.IO.FileInfo]$Package,
    [string]$PackageKind,
    [string]$ArtifactName,
    [string]$ArtifactRid,
    [string]$PackageVersion,
    [bool]$IsPointerPackage
  )

  [pscustomobject]@{
    File = $Package
    PackageKind = $PackageKind
    PackageName = $Package.Name
    PackageVersion = $PackageVersion
    ArtifactName = $ArtifactName
    ArtifactRid = $ArtifactRid
    IsPointerPackage = $IsPointerPackage
  }
}

function Get-ClassifiedNuGetPackages {
  $packages = @(Get-ChildItem -Path $DownloadRoot -Filter "Aspire.Cli*.nupkg" -File -Recurse |
    Where-Object { $_.Name -notmatch '\.symbols\.' } |
    Sort-Object FullName)

  foreach ($package in $packages) {
    $artifact = Get-NativeArchiveArtifact $package
    if ($null -eq $artifact) {
      Write-Warning "Skipping Aspire.Cli package outside native archive artifacts: $($package.FullName)"
      continue
    }

    $artifactRidPattern = [System.Text.RegularExpressions.Regex]::Escape($artifact.Rid)
    $pointerPackagePattern = "^Aspire\.Cli\.$packageVersionPattern\.nupkg$"
    $ridPackagePattern = "^Aspire\.Cli\.$artifactRidPattern\.$packageVersionPattern\.nupkg$"

    $isPointerPackage = $false
    $packageVersion = $null

    if ($package.Name -match $pointerPackagePattern) {
      $isPointerPackage = $true
      $packageVersion = $Matches.Version
    } elseif ($package.Name -match $ridPackagePattern) {
      $packageVersion = $Matches.Version
    }

    if ($null -eq $packageVersion) {
      throw "Unexpected Aspire.Cli package '$($package.FullName)' in artifact '$($artifact.Name)'. Expected either an Aspire.Cli pointer package or an Aspire.Cli.$($artifact.Rid) RID-specific package."
    }

    New-ClassifiedPackage $package 'NuGet' $artifact.Name $artifact.Rid $packageVersion $isPointerPackage
  }
}

function Get-ClassifiedNpmPackages {
  $packages = @(Get-ChildItem -Path $DownloadRoot -Filter "$NpmPackageFilePrefix*.tgz" -File -Recurse |
    Sort-Object FullName)
  $escapedPrefix = [System.Text.RegularExpressions.Regex]::Escape($NpmPackageFilePrefix)

  foreach ($package in $packages) {
    $artifact = Get-NativeArchiveArtifact $package
    if ($null -eq $artifact) {
      Write-Warning "Skipping Aspire CLI npm package outside native archive artifacts: $($package.FullName)"
      continue
    }

    $artifactRidPattern = [System.Text.RegularExpressions.Regex]::Escape($artifact.Rid)
    $pointerPackagePattern = "^$escapedPrefix-$packageVersionPattern\.tgz$"
    $ridPackagePattern = "^$escapedPrefix-$artifactRidPattern-$packageVersionPattern\.tgz$"

    $isPointerPackage = $false
    $packageVersion = $null

    if ($package.Name -match $pointerPackagePattern) {
      $isPointerPackage = $true
      $packageVersion = $Matches.Version
    } elseif ($package.Name -match $ridPackagePattern) {
      $packageVersion = $Matches.Version
    }

    if ($null -eq $packageVersion) {
      throw "Unexpected Aspire CLI npm package '$($package.FullName)' in artifact '$($artifact.Name)'. Expected either a $NpmPackageFilePrefix pointer package or a $NpmPackageFilePrefix-$($artifact.Rid) RID-specific package."
    }

    New-ClassifiedPackage $package 'npm' $artifact.Name $artifact.Rid $packageVersion $isPointerPackage
  }
}

function Get-PackagesToStage {
  param(
    [object[]]$ClassifiedPackages,
    [string]$PointerPackageDescription,
    [string]$RidPackageDescription,
    [string]$NoPackagesDescription
  )

  if ($ClassifiedPackages.Count -eq 0) {
    throw "No $NoPackagesDescription packages were found under native_archives_<rid> artifacts in $DownloadRoot."
  }

  $canonicalPointerPackages = @($ClassifiedPackages | Where-Object { $_.IsPointerPackage -and $_.ArtifactName -eq $CanonicalPointerArtifactName })
  if ($canonicalPointerPackages.Count -ne 1) {
    throw "Expected exactly one canonical $PointerPackageDescription pointer package from $CanonicalPointerArtifactName, but found $($canonicalPointerPackages.Count): $($canonicalPointerPackages.File.FullName -join ', ')"
  }

  # Pointer packages are produced for every RID build. Stage exactly one
  # canonical pointer package and all RID-specific packages.
  $skippedPointerPackages = @($ClassifiedPackages | Where-Object { $_.IsPointerPackage -and $_.ArtifactName -ne $CanonicalPointerArtifactName })
  foreach ($package in $skippedPointerPackages) {
    Write-Host "Skipping non-canonical $PointerPackageDescription pointer package from $($package.ArtifactName): $($package.File.FullName)"
  }

  $canonicalPointerVersion = $canonicalPointerPackages[0].PackageVersion
  $ridPackages = @($ClassifiedPackages | Where-Object { -not $_.IsPointerPackage })
  $duplicateRidPackageGroups = @($ridPackages | Group-Object ArtifactRid | Where-Object { $_.Count -gt 1 })
  if ($duplicateRidPackageGroups.Count -gt 0) {
    $details = @($duplicateRidPackageGroups | ForEach-Object { "$($_.Name): $($_.Group.File.FullName -join ', ')" })
    throw "Expected exactly one $RidPackageDescription RID-specific package per RID, but found duplicates: $($details -join '; ')"
  }

  $ridPackageVersionMismatches = @($ridPackages | Where-Object { $_.PackageVersion -ne $canonicalPointerVersion })
  if ($ridPackageVersionMismatches.Count -gt 0) {
    $details = @($ridPackageVersionMismatches | ForEach-Object { "$($_.PackageName) has version $($_.PackageVersion)" })
    throw "All $RidPackageDescription RID-specific packages must match canonical pointer package version $canonicalPointerVersion from $CanonicalPointerArtifactName, but found mismatches: $($details -join '; ')"
  }

  return @($canonicalPointerPackages[0]) + $ridPackages
}

$nugetPackagesToStage = Get-PackagesToStage (Get-ClassifiedNuGetPackages) 'Aspire.Cli' 'Aspire.Cli' 'native CLI tool'
$classifiedNpmPackages = @(Get-ClassifiedNpmPackages)
$npmPackagesToStage = if ($classifiedNpmPackages.Count -eq 0) {
  $message = "No Aspire CLI npm packages were found under native_archives_<rid> artifacts in $DownloadRoot."
  if ($RequireNpmPackages) {
    throw $message
  }

  Write-Warning $message
  @()
} else {
  Get-PackagesToStage $classifiedNpmPackages 'Aspire CLI npm' 'Aspire CLI npm' 'Aspire CLI npm'
}
$packagesToStage = @($nugetPackagesToStage) + @($npmPackagesToStage)

foreach ($packageToStage in $packagesToStage) {
  $package = $packageToStage.File
  Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $ShippingDir $package.Name) -Force
}
