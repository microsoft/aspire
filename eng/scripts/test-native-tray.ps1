# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

<#
.SYNOPSIS
Smoke-tests native tray launchability from the built bundle payload archive.
.DESCRIPTION
Extracts the packaged tray and runs --help without repairing executable
permissions. Run only on a host matching the payload OS and architecture.
This does not initialize the desktop, load GTK/AppIndicator, or register on D-Bus.
The timeout bounds the process-exit wait, not output draining or cleanup.
#>
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath,
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Rid,
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$testDirectory = [System.IO.Directory]::CreateTempSubdirectory('aspire-tray-smoke-').FullName
$trayProcess = $null
$trayStarted = $false

try {
    & tar -xzf $ArchivePath -C $testDirectory "$Rid/tray"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to extract the packaged tray for '$Rid'."
    }

    $relativePath = if ($Rid.StartsWith('win-')) {
        'tray/aspire-tray.exe'
    } elseif ($Rid.StartsWith('osx-')) {
        'tray/Aspire Tray.app/Contents/MacOS/aspire-tray'
    } else {
        'tray/aspire-tray'
    }
    $executable = Join-Path $testDirectory "$Rid/$relativePath"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Packaged tray executable was not found at '$executable'."
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.ArgumentList.Add('--help')
    $startInfo.WorkingDirectory = Split-Path -Parent $executable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $trayProcess = [System.Diagnostics.Process]::new()
    $trayProcess.StartInfo = $startInfo
    $trayStarted = $trayProcess.Start()
    # Drain both streams concurrently so verbose failures cannot block process exit.
    $stdout = $trayProcess.StandardOutput.ReadToEndAsync()
    $stderr = $trayProcess.StandardError.ReadToEndAsync()
    if (-not $trayProcess.WaitForExit($TimeoutSeconds * 1000)) {
        throw "Packaged tray --help timed out after $TimeoutSeconds seconds."
    }
    Write-Output $stdout.GetAwaiter().GetResult()
    Write-Output $stderr.GetAwaiter().GetResult()
    if ($trayProcess.ExitCode -ne 0) {
        throw "Packaged tray --help failed with exit code $($trayProcess.ExitCode)."
    }
    Write-Output "Packaged tray launchability check passed for $Rid."
}
finally {
    if ($trayProcess) {
        if ($trayStarted -and -not $trayProcess.HasExited) {
            # --help does not spawn children; terminate only the process we launched.
            $trayProcess.Kill()
            $trayProcess.WaitForExit()
        }
        $trayProcess.Dispose()
    }
    Remove-Item -LiteralPath $testDirectory -Recurse -Force
}
