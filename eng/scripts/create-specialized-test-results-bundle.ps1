param(
    [Parameter(Mandatory = $true)]
    [string] $InputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $RunsheetPath,

    [Parameter(Mandatory = $true)]
    [string] $RunsheetResult,

    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [Parameter(Mandatory = $true)]
    [int] $CurrentAttempt
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$bundleTrxFiles = [System.Collections.Generic.List[string]]::new()

function Get-ArtifactDetails {
    param([string] $ArtifactName)

    $artifactPath = Join-Path $InputDirectory $ArtifactName
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Container)) {
        return [pscustomobject]@{
            sourceStatus = 'missing'
            sourceAttempt = $null
            sourceRunId = $null
            trxStatus = 'missing'
            trxFiles = @()
        }
    }

    $metadataPath = Join-Path $artifactPath 'test-artifact-metadata.json'
    $sourceAttempt = $null
    $sourceRunId = $null
    $sourceStatus = 'unknown'

    if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
        try {
            $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
            if ($null -eq $metadata.runAttempt -or [string]::IsNullOrWhiteSpace([string] $metadata.runId)) {
                throw 'Artifact metadata is missing its run ID or attempt.'
            }
            $sourceAttempt = [int] $metadata.runAttempt
            if ($sourceAttempt -lt 1) {
                throw 'Artifact metadata has an invalid run attempt.'
            }
            $sourceRunId = [string] $metadata.runId

            if ($sourceRunId -eq $RunId) {
                if ($sourceAttempt -eq $CurrentAttempt) {
                    $sourceStatus = 'current'
                } elseif ($sourceAttempt -lt $CurrentAttempt) {
                    $sourceStatus = 'inherited'
                }
            }
        } catch {
            $sourceAttempt = $null
            $sourceRunId = $null
        }
    }

    $trxFiles = @(Get-ChildItem -LiteralPath $artifactPath -Filter '*.trx' -File -Recurse)
    [pscustomobject]@{
        sourceStatus = $sourceStatus
        sourceAttempt = $sourceAttempt
        sourceRunId = $sourceRunId
        trxStatus = if ($trxFiles.Count -gt 0) { 'present' } else { 'missing' }
        trxFiles = $trxFiles
    }
}

function Get-SafePathSegment {
    param([string] $Value)

    $segment = [regex]::Replace($Value, '[^A-Za-z0-9._-]', '_')
    if ($segment -in @('', '.', '..')) {
        return '_'
    }

    return $segment
}

$runsheetJson = Get-Content -LiteralPath $RunsheetPath -Raw
$runsheet = if ([string]::IsNullOrWhiteSpace($runsheetJson)) {
    @()
} else {
    @(ConvertFrom-Json -InputObject $runsheetJson)
}
$expectedLanes = @(
    foreach ($lane in $runsheet) {
        [pscustomobject]@{
            project = [string] $lane.project
            os = [string] $lane.os
            artifactName = "logs-$($lane.project)-$($lane.os)"
        }
    }
)

$expectedArtifactNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$laneResults = @(
    foreach ($lane in $expectedLanes) {
        [void] $expectedArtifactNames.Add($lane.artifactName)
        $details = Get-ArtifactDetails -ArtifactName $lane.artifactName
        $bundleFiles = @()

        foreach ($trxFile in $details.trxFiles) {
            $attemptSegment = if ($null -eq $details.sourceAttempt) { 'attempt-unknown' } else { "attempt-$($details.sourceAttempt)" }
            $relativePath = [System.IO.Path]::GetRelativePath((Join-Path $InputDirectory $lane.artifactName), $trxFile.FullName)
            $destination = Join-Path $OutputDirectory (Join-Path 'trx' (Join-Path (Get-SafePathSegment $lane.project) (Join-Path (Get-SafePathSegment $lane.os) (Join-Path $attemptSegment $relativePath))))
            $destinationDirectory = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            Copy-Item -LiteralPath $trxFile.FullName -Destination $destination
            $bundlePath = [System.IO.Path]::GetRelativePath($OutputDirectory, $destination).Replace('\', '/')
            $bundleFiles += $bundlePath
            $bundleTrxFiles.Add($bundlePath)
        }

        [pscustomobject]@{
            project = $lane.project
            os = $lane.os
            artifactName = $lane.artifactName
            expected = $true
            sourceStatus = $details.sourceStatus
            sourceRunId = $details.sourceRunId
            sourceAttempt = $details.sourceAttempt
            trxStatus = $details.trxStatus
            trxFiles = @($bundleFiles)
        }
    }
)

$unmatchedArtifacts = @(
    if (Test-Path -LiteralPath $InputDirectory -PathType Container) {
        foreach ($artifactDirectory in Get-ChildItem -LiteralPath $InputDirectory -Directory -Filter 'logs-*') {
            if ($expectedArtifactNames.Contains($artifactDirectory.Name)) {
                continue
            }

            $details = Get-ArtifactDetails -ArtifactName $artifactDirectory.Name
            if ($details.trxFiles.Count -eq 0) {
                continue
            }

            $bundleFiles = @()
            foreach ($trxFile in $details.trxFiles) {
                $attemptSegment = if ($null -eq $details.sourceAttempt) { 'attempt-unknown' } else { "attempt-$($details.sourceAttempt)" }
                $relativePath = [System.IO.Path]::GetRelativePath($artifactDirectory.FullName, $trxFile.FullName)
                $destination = Join-Path $OutputDirectory (Join-Path 'trx' (Join-Path '_unmatched' (Join-Path (Get-SafePathSegment $artifactDirectory.Name) (Join-Path $attemptSegment $relativePath))))
                $destinationDirectory = Split-Path -Parent $destination
                New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
                Copy-Item -LiteralPath $trxFile.FullName -Destination $destination
                $bundlePath = [System.IO.Path]::GetRelativePath($OutputDirectory, $destination).Replace('\', '/')
                $bundleFiles += $bundlePath
                $bundleTrxFiles.Add($bundlePath)
            }

            [pscustomobject]@{
                artifactName = $artifactDirectory.Name
                expected = $false
                sourceStatus = $details.sourceStatus
                sourceRunId = $details.sourceRunId
                sourceAttempt = $details.sourceAttempt
                trxStatus = $details.trxStatus
                trxFiles = @($bundleFiles)
            }
        }
    }
)

$manifest = [pscustomobject]@{
    runId = $RunId
    attempt = $CurrentAttempt
    runsheetResult = $RunsheetResult
    expectedLaneCount = $expectedLanes.Count
    trxFileCount = $bundleTrxFiles.Count
    lanes = @($laneResults)
    unmatchedArtifacts = @($unmatchedArtifacts)
}
$manifestPath = Join-Path $OutputDirectory 'manifest.json'
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))
