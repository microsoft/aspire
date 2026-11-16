<#
.SYNOPSIS
    Copies Aspire CLI starter validation diagnostics into a private directory for artifact upload.

.DESCRIPTION
    actions/upload-artifact first enumerates every path under the upload root and then calls
    fs.stat on each result. It does not tolerate an entry that disappears in between and fails with:

      ENOENT: no such file or directory, stat '<root>/aspire-go-starter/AspireCliGoStarterSmoke/frontend/node_modules'

    The generated starter projects contain node_modules trees. A JavaScript package installer, such
    as an interrupted `frontend-installer` npm install, can still rewrite or delete them after
    `aspire stop` returns. Upload a copy that no other process writes to instead. The copy skips
    node_modules directories without enumerating them, skips directory symbolic links, and ignores
    entries that are removed while they are being copied.

.PARAMETER SourceRoot
    The starter validation root that contains generated projects, diagnostics, and CLI logs.

.PARAMETER DestinationRoot
    The directory to create for upload. Any existing directory at this path is replaced.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot
)

$ErrorActionPreference = 'Stop'

# Test-Path cannot tell whether an entry vanished: it reports a dangling symbolic link as present.
# Classify the failure itself so only "not found" errors are tolerated.
function Test-IsNotFoundError
{
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    for ($exception = $ErrorRecord.Exception; $null -ne $exception; $exception = $exception.InnerException)
    {
        if ($exception -is [System.IO.FileNotFoundException] -or
            $exception -is [System.IO.DirectoryNotFoundException] -or
            $exception -is [System.Management.Automation.ItemNotFoundException])
        {
            return $true
        }
    }

    return $false
}

function Copy-DiagnosticsDirectory
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    try
    {
        $children = @(Get-ChildItem -LiteralPath $Source -Force -ErrorAction Stop)
    }
    catch
    {
        if (-not (Test-IsNotFoundError -ErrorRecord $_))
        {
            throw
        }

        Write-Host "Skipping '$Source' because it was removed while staging diagnostics."
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    foreach ($child in $children)
    {
        $target = Join-Path $Destination $child.Name

        if ($child.PSIsContainer)
        {
            # Installed dependencies are not diagnostics and are the entries other processes mutate.
            # Symbolic links to directories can form cycles or point outside the validation root.
            if ($child.Name -eq 'node_modules' -or $child.LinkType)
            {
                continue
            }

            Copy-DiagnosticsDirectory -Source $child.FullName -Destination $target
            continue
        }

        try
        {
            Copy-Item -LiteralPath $child.FullName -Destination $target -Force -ErrorAction Stop
        }
        catch
        {
            if (-not (Test-IsNotFoundError -ErrorRecord $_))
            {
                throw
            }

            Write-Host "Skipping '$($child.FullName)' because it was removed while staging diagnostics."
        }
    }
}

if (Test-Path -LiteralPath $DestinationRoot)
{
    Remove-Item -LiteralPath $DestinationRoot -Recurse -Force
}

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container))
{
    Write-Host "No starter validation diagnostics found at '$SourceRoot'."
    return
}

Copy-DiagnosticsDirectory -Source $SourceRoot -Destination $DestinationRoot
Write-Host "Staged starter validation diagnostics from '$SourceRoot' to '$DestinationRoot'."
