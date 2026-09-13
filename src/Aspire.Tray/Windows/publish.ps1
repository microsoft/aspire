<#
.SYNOPSIS
Publishes the C# NativeAOT Windows tray companion, optionally running its native UI smoke.
.DESCRIPTION
Run this script on Windows with .NET 10 and the Visual Studio C++ NativeAOT prerequisites
for the selected architecture. Windows NativeAOT linking is not supported from macOS.
Smoke runs require an interactive desktop, Explorer, and an existing Aspire CLI executable.
It creates real notification icons and menus but never opens a browser or stops AppHosts.
The WinExe logs are captured beside the published executable; no console window is required.
The per-user singleton spans Windows sessions. A second instance fails explicitly rather
than activating the first. Windows signing and installer integration are not yet implemented.
.EXAMPLE
.\publish.ps1 -Architecture x64
.EXAMPLE
.\publish.ps1 -Architecture arm64 -CliPath C:\tools\aspire.exe -SmokeSeconds 10
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64',

    [string] $CliPath,

    [ValidateRange(1, 120)]
    [int] $SmokeSeconds
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'NativeAOT publishing and native tray smoke must run on Windows.'
}

$runSmoke = $PSBoundParameters.ContainsKey('SmokeSeconds')
if ($runSmoke -and (
        [string]::IsNullOrWhiteSpace($CliPath) -or
        -not [IO.Path]::IsPathRooted($CliPath) -or
        [IO.Path]::GetPathRoot($CliPath).Length -lt 3 -or
        -not (Test-Path -LiteralPath $CliPath -PathType Leaf))) {
    throw 'Smoke requires -CliPath with an existing absolute Aspire executable path.'
}

$project = Join-Path $PSScriptRoot 'Aspire.Tray.Windows.csproj'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = Join-Path $repository "artifacts/bin/Aspire.Tray.Windows/Release/net10.0/win-$Architecture/publish"

& dotnet publish $project -c Release -r "win-$Architecture" --self-contained true -o $output
if ($LASTEXITCODE -ne 0) {
    throw "NativeAOT publish failed with exit code $LASTEXITCODE."
}
Write-Host "NativeAOT output: $output"

if ($runSmoke) {
    $executable = Join-Path $output 'aspire-tray.exe'
    $stdout = Join-Path $output 'smoke.stdout.log'
    $stderr = Join-Path $output 'smoke.stderr.log'
    # Windows paths cannot contain quotes. Quote the entire executable argument because
    # Start-Process joins ArgumentList into a single command-line string, even for arrays.
    if ($CliPath.Contains('"')) {
        throw 'The CLI path cannot contain quotation marks.'
    }
    $arguments = "--cli `"$CliPath`" --smoke-seconds $SmokeSeconds"
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    # Cache the native process handle before it exits, including on Windows PowerShell 5.1,
    # so ExitCode remains available after the bounded WaitForExit below.
    $null = $process.Handle
    if (-not $process.WaitForExit(($SmokeSeconds + 30) * 1000)) {
        # This is only the tray process launched above, never an AppHost or a process tree.
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw "Tray smoke timed out. See $stderr."
    }
    # Flush redirected asynchronous output before consuming the diagnostic files.
    $process.WaitForExit()
    Get-Content -LiteralPath $stdout
    Get-Content -LiteralPath $stderr
    if ($process.ExitCode -ne 0) {
        throw "Tray smoke failed with exit code $($process.ExitCode)."
    }
    Write-Host 'Native UI smoke passed. Verify discovery status in the log; this is not an AppHost connectivity assertion.'
}
