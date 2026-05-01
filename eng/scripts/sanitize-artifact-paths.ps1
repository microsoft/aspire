# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$invalidArtifactFileNameCharacters = [char[]]@('"', ':', '<', '>', '|', '*', '?', "`r", "`n")

function ConvertTo-ArtifactSafeName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $safeName = $Name
    foreach ($character in $invalidArtifactFileNameCharacters) {
        $safeName = $safeName.Replace([string]$character, '_')
    }
    if ([string]::IsNullOrWhiteSpace($safeName)) {
        return 'artifact'
    }

    return $safeName
}

function Rename-ItemIfNeeded {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileSystemInfo]$Item
    )

    $safeName = ConvertTo-ArtifactSafeName -Name $Item.Name
    if ($safeName -eq $Item.Name) {
        return 0
    }

    $directory = if ($Item -is [System.IO.DirectoryInfo]) { $Item.Parent.FullName } else { $Item.DirectoryName }
    $targetPath = Join-Path $directory $safeName
    if (Test-Path -LiteralPath $targetPath) {
        throw "Cannot sanitize artifact path '$($Item.FullName)' because '$targetPath' already exists."
    }

    Write-Host "Renaming artifact path '$($Item.FullName)' to '$safeName'."
    Rename-Item -LiteralPath $Item.FullName -NewName $safeName
    return 1
}

$renamedCount = 0
foreach ($pathItem in $Path) {
    if (-not (Test-Path -LiteralPath $pathItem)) {
        Write-Host "Artifact path '$pathItem' does not exist; skipping."
        continue
    }

    $rootItem = Get-Item -LiteralPath $pathItem -Force
    if ($rootItem -is [System.IO.DirectoryInfo]) {
        $items = Get-ChildItem -LiteralPath $rootItem.FullName -Force -Recurse |
            Sort-Object { $_.FullName.Length } -Descending

        foreach ($item in $items) {
            $renamedCount += Rename-ItemIfNeeded -Item $item
        }
    } else {
        $renamedCount += Rename-ItemIfNeeded -Item $rootItem
    }
}

Write-Host "Renamed $renamedCount artifact path(s)."
