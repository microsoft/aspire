param(
  [Parameter(Mandatory = $true)]
  [string]$DownloadRoot,

  [Parameter(Mandatory = $true)]
  [string]$ShippingDir,

  [string]$CanonicalPointerArtifactName = 'native_archives_win_x64'
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $ShippingDir -Force | Out-Null

$packages = @(Get-ChildItem -Path $DownloadRoot -Filter "Aspire.Cli*.nupkg" -File -Recurse |
  Where-Object { $_.Name -notmatch '\.symbols\.' } |
  Sort-Object FullName)
if ($packages.Count -eq 0) {
  throw "No native CLI tool packages were downloaded to $DownloadRoot."
}

$packageVersionPattern = '(?<Version>\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)'
$pointerPackagePattern = "^Aspire\.Cli\.$packageVersionPattern\.nupkg$"

$classifiedPackages = @(
  foreach ($package in $packages) {
    $pathParts = $package.FullName.Split([char[]]@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)
    $nativeArchiveRoot = @($pathParts | Where-Object { $_ -like 'native_archives_*' } | Select-Object -First 1)

    if ($nativeArchiveRoot.Count -eq 0) {
      Write-Warning "Skipping Aspire.Cli package outside native archive artifacts: $($package.FullName)"
      continue
    }

    $artifactName = $nativeArchiveRoot[0]
    $artifactRid = $artifactName.Substring('native_archives_'.Length).Replace('_', '-')
    $artifactRidPattern = [System.Text.RegularExpressions.Regex]::Escape($artifactRid)
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
      throw "Unexpected Aspire.Cli package '$($package.FullName)' in artifact '$artifactName'. Expected either an Aspire.Cli pointer package or an Aspire.Cli.$artifactRid RID-specific package."
    }

    [pscustomobject]@{
      File = $package
      PackageName = $package.Name
      PackageVersion = $packageVersion
      ArtifactName = $artifactName
      ArtifactRid = $artifactRid
      IsPointerPackage = $isPointerPackage
    }
  }
)

if ($classifiedPackages.Count -eq 0) {
  throw "No native CLI tool packages were found under native_archives_<rid> artifacts in $DownloadRoot."
}

$canonicalPointerPackages = @($classifiedPackages | Where-Object { $_.IsPointerPackage -and $_.ArtifactName -eq $CanonicalPointerArtifactName })
if ($canonicalPointerPackages.Count -ne 1) {
  throw "Expected exactly one canonical Aspire.Cli pointer package from $CanonicalPointerArtifactName, but found $($canonicalPointerPackages.Count): $($canonicalPointerPackages.File.FullName -join ', ')"
}

$skippedPointerPackages = @($classifiedPackages | Where-Object { $_.IsPointerPackage -and $_.ArtifactName -ne $CanonicalPointerArtifactName })
foreach ($package in $skippedPointerPackages) {
  Write-Host "Skipping non-canonical Aspire.Cli pointer package from $($package.ArtifactName): $($package.File.FullName)"
}

$canonicalPointerVersion = $canonicalPointerPackages[0].PackageVersion
$ridPackages = @($classifiedPackages | Where-Object { -not $_.IsPointerPackage })
$duplicateRidPackageGroups = @($ridPackages | Group-Object ArtifactRid | Where-Object { $_.Count -gt 1 })
if ($duplicateRidPackageGroups.Count -gt 0) {
  $details = @($duplicateRidPackageGroups | ForEach-Object { "$($_.Name): $($_.Group.File.FullName -join ', ')" })
  throw "Expected exactly one Aspire.Cli RID-specific package per RID, but found duplicates: $($details -join '; ')"
}

$ridPackageVersionMismatches = @($ridPackages | Where-Object { $_.PackageVersion -ne $canonicalPointerVersion })
if ($ridPackageVersionMismatches.Count -gt 0) {
  $details = @($ridPackageVersionMismatches | ForEach-Object { "$($_.PackageName) has version $($_.PackageVersion)" })
  throw "All Aspire.Cli RID-specific packages must match canonical pointer package version $canonicalPointerVersion from $CanonicalPointerArtifactName, but found mismatches: $($details -join '; ')"
}

$packagesToStage = @($canonicalPointerPackages[0]) + $ridPackages
foreach ($packageToStage in $packagesToStage) {
  $package = $packageToStage.File
  Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $ShippingDir $package.Name) -Force
}
