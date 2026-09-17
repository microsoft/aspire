# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture
)

$ErrorActionPreference = 'Stop'
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
    & cl.exe /nologo /W4 /WX /EHsc /DUNICODE /D_UNICODE $source "/Fo$scratch/control.obj" "/Fe$exe" user32.lib shell32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'SDK tray registration control compilation failed.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'SDK tray registration control setup or cleanup failed.' }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
