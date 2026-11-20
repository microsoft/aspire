#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Source))
{
    Write-Warning "Starter validation diagnostics not found at '$Source'."
    return
}

$sourcePath = [System.IO.Path]::GetFullPath($Source)
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$directories = [System.Collections.Generic.Stack[string]]::new()
$directories.Push($sourcePath)

while ($directories.Count -gt 0)
{
    $directory = $directories.Pop()
    $relativePath = [System.IO.Path]::GetRelativePath($sourcePath, $directory)
    $destinationDirectory = [System.IO.Path]::Combine($destinationPath, $relativePath)
    [void][System.IO.Directory]::CreateDirectory($destinationDirectory)

    foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($directory))
    {
        # npm may remove this directory at any time after aspire stop. Skip it before
        # inspecting its attributes, not just when filtering the upload's matches.
        if ([System.IO.Path]::GetFileName($entry) -ieq 'node_modules')
        {
            continue
        }

        $attributes = [System.IO.File]::GetAttributes($entry)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            Write-Warning "Skipping symbolic link '$entry'."
            continue
        }

        if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0)
        {
            $directories.Push($entry)
        }
        else
        {
            [System.IO.File]::Copy($entry, [System.IO.Path]::Combine($destinationDirectory, [System.IO.Path]::GetFileName($entry)))
        }
    }
}
