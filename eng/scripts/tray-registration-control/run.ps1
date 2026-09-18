# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

<#
.SYNOPSIS
Builds and runs the Windows SDK tray registration diagnostic, not the product smoke.
.DESCRIPTION
CompareArchitectures runs ARM64, emulated x64, then ARM64 again on the same Windows
ARM64 desktop. Separate PowerShell processes isolate Visual Studio toolchain state.
The repeated ARM64 baseline helps distinguish architecture from shell readiness changes.
Registration rejection is logged; setup, compilation, and cleanup failures remain fatal.
.EXAMPLE
.\run.ps1 -Architecture arm64 -CompareArchitectures
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture,

    [switch] $CompareArchitectures
)

$ErrorActionPreference = 'Stop'
if ($CompareArchitectures) {
    if (-not $IsWindows -or $Architecture -ne 'arm64' -or
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::Arm64) {
        throw 'Architecture comparison requires -Architecture arm64 on a Windows ARM64 host.'
    }
    $pwsh = (Get-Process -Id $PID).Path
    foreach ($target in @('arm64', 'x64', 'arm64')) {
        Write-Host "SDK registration comparison: $target"
        & $pwsh -NoProfile -File $PSCommandPath -Architecture $target
        if ($LASTEXITCODE -ne 0) { throw "SDK tray registration control failed for $target." }
    }
    return
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'Visual Studio was not found for the SDK tray control.' }
$devShell = Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Import-Module $devShell
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments "-arch=$Architecture -host_arch=x64"

$scratch = [IO.Directory]::CreateTempSubdirectory('aspire-tray-sdk-').FullName
try {
    $source = Join-Path $PSScriptRoot 'control.cpp'
    $exe = Join-Path $scratch 'control.exe'
    & cl.exe /nologo /W4 /WX /EHsc /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 $source "/Fo$scratch/control.obj" "/Fe$exe" user32.lib shell32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'SDK tray registration control compilation failed.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'SDK tray registration control setup or cleanup failed.' }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
