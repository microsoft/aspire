#!/usr/bin/env pwsh
#requires -Version 7.0

<#
.SYNOPSIS
    Removes leaked Aspire CLI test entries from the per-user Add/Remove
    Programs (ARP) list.

.DESCRIPTION
    WindowsRegistryReaderIntegrationTests writes a uniquely-named subkey
    under HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall to
    exercise the real registry path. A crashed test run can leave subkeys
    behind, which then show up in Settings -> Apps as
    "Aspire CLI (... test) 0.0.0" entries on developer machines. This
    script removes every subkey whose name starts with the test prefix
    "Microsoft.Aspire_AspireCliTests_".

    Writing to HKCU does not require elevation, so this script does not
    require an administrator shell.

.PARAMETER WhatIf
    Show which subkeys would be deleted without modifying the registry.

.EXAMPLE
    pwsh eng/scripts/cleanup-test-arp-entries.ps1

.EXAMPLE
    pwsh eng/scripts/cleanup-test-arp-entries.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

if (-not $IsWindows -and $PSVersionTable.PSEdition -ne 'Desktop') {
    Write-Host 'This script only runs on Windows.'
    exit 0
}

$uninstallPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$prefix = 'Microsoft.Aspire_AspireCliTests_'

if (-not (Test-Path -LiteralPath $uninstallPath)) {
    Write-Host "Uninstall key not found at $uninstallPath; nothing to do."
    exit 0
}

$matches = Get-ChildItem -LiteralPath $uninstallPath |
    Where-Object { $_.PSChildName.StartsWith($prefix, [System.StringComparison]::Ordinal) }

if (-not $matches) {
    Write-Host "No leaked Aspire CLI test ARP entries found under $uninstallPath."
    exit 0
}

$removed = 0
$failed = 0
foreach ($entry in $matches) {
    if ($PSCmdlet.ShouldProcess($entry.PSPath, 'Remove leaked test ARP entry')) {
        try {
            Remove-Item -LiteralPath $entry.PSPath -Recurse -Force
            $removed++
        }
        catch {
            Write-Warning "Failed to remove '$($entry.PSChildName)': $_"
            $failed++
        }
    }
}

Write-Host "Removed $removed leaked Aspire CLI test ARP entries from $uninstallPath. Failed: $failed."
if ($failed -gt 0) {
    exit 1
}
