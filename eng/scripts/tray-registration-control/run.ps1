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
$controlExited = $false
try {
    $source = Join-Path $PSScriptRoot 'control.cpp'
    $exe = Join-Path $scratch 'control.exe'
    & cl.exe /nologo /W4 /WX /EHsc /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 $source "/Fo$scratch/control.obj" "/Fe$exe" user32.lib shell32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'SDK tray registration control compilation failed.' }

    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exe))
    try {
        # PE files have MZ at 0, e_lfanew at 0x3c, then PE\0\0 and the COFF Machine.
        # https://learn.microsoft.com/windows/win32/debug/pe-format
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'SDK control has no DOS signature.' }
        $reader.BaseStream.Position = 0x3c
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt $reader.BaseStream.Length - 6) { throw 'SDK control has an invalid PE offset.' }
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'SDK control has no PE signature.' }
        $machine = $reader.ReadUInt16()
        $expectedMachine = if ($Architecture -eq 'arm64') { 0xaa64 } else { 0x8664 }
        Write-Host ('SDK control PE Machine=0x{0:x4} expected=0x{1:x4}' -f $machine, $expectedMachine)
        if ($machine -ne $expectedMachine) { throw 'SDK control PE Machine does not match the requested architecture.' }
    }
    finally {
        $reader.Dispose()
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new($exe)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            $controlExited = $true
            throw 'SDK tray registration control timed out after 30 seconds.'
        }
        $controlExited = $true
        Write-Host "SDK control pid=$($process.Id) exited=True exit-code=$($process.ExitCode)"
        if ($process.ExitCode -ne 0) { throw 'SDK tray registration control setup or cleanup failed.' }
    }
    finally {
        try {
            if ($controlExited) {
                Write-Host $stdout.GetAwaiter().GetResult()
                Write-Host $stderr.GetAwaiter().GetResult()
            }
        }
        finally {
            $process.Dispose()
            Write-Host 'SDK control process handle and redirected streams disposed.'
        }
    }
}
finally {
    # The x64 control returned successfully in job 105671139724, but immediate
    # deletion of control.exe was denied. The lock owner is not established.
    # Retry only access/sharing/lock violations after confirmed exit and disposal.
    for ($attempt = 1; ; $attempt++) {
        try {
            [IO.Directory]::Delete($scratch, $true)
            break
        }
        catch [IO.IOException], [UnauthorizedAccessException] {
            $exception = $_.Exception.GetBaseException()
            $errorCode = $exception.HResult -band 0xffff
            if (-not $controlExited -or $errorCode -notin @(5, 32, 33) -or $attempt -ge 10) {
                throw
            }
            Write-Warning "SDK control cleanup attempt $attempt failed (code $errorCode): $($exception.Message) Retrying in 500ms."
            Start-Sleep -Milliseconds 500
        }
    }
}
