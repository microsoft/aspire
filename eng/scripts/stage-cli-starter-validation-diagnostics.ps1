<#
.SYNOPSIS
    Archives Aspire CLI starter validation diagnostics for artifact upload.

.DESCRIPTION
    actions/upload-artifact first enumerates every path under the upload root and then calls
    fs.stat on each result. It does not tolerate an entry that disappears in between and fails with:

      ENOENT: no such file or directory, stat '<root>/aspire-go-starter/AspireCliGoStarterSmoke/frontend/node_modules'

    The generated starter projects contain node_modules trees. A JavaScript package installer, such
    as an interrupted `frontend-installer` npm install, can still rewrite or delete them after
    `aspire stop` returns. Upload a completed tar archive instead. tar excludes node_modules without
    traversing them and stores symbolic links without following them. Hidden files and directories
    remain excluded, matching upload-artifact's default. The archive contains the validation root
    directory and its retained contents.

.PARAMETER SourceRoot
    The starter validation root that contains generated projects, diagnostics, and CLI logs.

.PARAMETER DestinationRoot
    The directory in which to create starter-validation.tar for upload.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$archivePath = Join-Path $DestinationRoot 'starter-validation.tar'
$temporaryArchivePath = "$archivePath.partial"
foreach ($path in @($archivePath, $temporaryArchivePath))
{
    if (Test-Path -LiteralPath $path)
    {
        Remove-Item -LiteralPath $path -Force
    }
}

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container))
{
    Write-Host "No starter validation diagnostics found at '$SourceRoot'."
    return
}

New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
try
{
    # Both GNU tar and BSD/Windows tar exclude this directory name at any depth without entering it.
    # Do not dereference links or ignore tar failures: errors in retained diagnostics must stay visible.
    # Archive the named root from its parent: using "." lets BSD tar's hidden-file pattern exclude
    # the entire "./" tree. ".?*" excludes hidden names at any depth, including "..name".
    # https://github.com/actions/upload-artifact#uploading-hidden-files
    $sourceDirectory = Get-Item -LiteralPath $SourceRoot
    & tar --exclude=node_modules '--exclude=.?*' -cf $temporaryArchivePath -C $sourceDirectory.Parent.FullName $sourceDirectory.Name
    if ($LASTEXITCODE -ne 0)
    {
        throw "Archiving starter validation diagnostics failed with tar exit code $LASTEXITCODE."
    }

    Move-Item -LiteralPath $temporaryArchivePath -Destination $archivePath -Force
}
finally
{
    if (Test-Path -LiteralPath $temporaryArchivePath)
    {
        Remove-Item -LiteralPath $temporaryArchivePath -Force
    }
}

Write-Host "Archived starter validation diagnostics from '$SourceRoot' to '$archivePath'."
