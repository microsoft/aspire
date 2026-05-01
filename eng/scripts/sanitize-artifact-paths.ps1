# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$invalidArtifactFileNameCharacters = [System.Collections.Generic.HashSet[char]]::new()
foreach ($character in [char[]]@('"', ':', '<', '>', '|', '*', '?', "`r", "`n")) {
    $invalidArtifactFileNameCharacters.Add($character) | Out-Null
}

function ConvertTo-ArtifactSafeName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $builder = [System.Text.StringBuilder]::new($Name.Length)
    foreach ($character in $Name.ToCharArray()) {
        if ($invalidArtifactFileNameCharacters.Contains($character)) {
            $builder.Append('_') | Out-Null
        } else {
            $builder.Append($character) | Out-Null
        }
    }

    $safeName = $builder.ToString()
    if ([string]::IsNullOrWhiteSpace($safeName)) {
        return 'artifact'
    }

    return $safeName
}

function Get-UniqueTargetName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$SafeName
    )

    $candidate = Join-Path $Directory $SafeName
    if (-not (Test-Path -LiteralPath $candidate)) {
        return $SafeName
    }

    $fileNameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($SafeName)
    $extension = [System.IO.Path]::GetExtension($SafeName)
    for ($i = 2; ; $i++) {
        $candidateName = "$fileNameWithoutExtension-$i$extension"
        $candidate = Join-Path $Directory $candidateName
        if (-not (Test-Path -LiteralPath $candidate)) {
            return $candidateName
        }
    }
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
    $targetName = Get-UniqueTargetName -Directory $directory -SafeName $safeName
    Write-Host "Renaming artifact path '$($Item.FullName)' to '$targetName'."
    Rename-Item -LiteralPath $Item.FullName -NewName $targetName
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
