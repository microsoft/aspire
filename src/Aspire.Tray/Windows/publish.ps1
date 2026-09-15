#requires -Version 7.4
<#
.SYNOPSIS
Publishes the C# NativeAOT Windows tray companion, optionally running its native UI smoke.
.DESCRIPTION
Run this script on Windows with the repository-pinned SDK installed by restore.cmd and
the Visual Studio C++ NativeAOT prerequisites for the selected architecture.
The tray targets net10.0. Windows NativeAOT linking is not supported from macOS.
Smoke runs require an interactive desktop, Explorer, and an existing Aspire CLI executable.
It creates real notification icons and menus but never opens a browser or stops AppHosts.
The WinExe logs are captured under artifacts/log; no console window is required.
PowerShell 7.4 or later is required for argument-list invocation and concurrent pipe draining.
Production bundles publish on Windows, Authenticode-sign the executable in the official
pipeline, then embed the executable and the original Aspire.ico in the invoking CLI.
This script publishes unsigned development output; it does not install a separate tray.
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
    [int] $SmokeSeconds,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $PublishDirectory,

    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'NativeAOT publishing and native tray smoke must run on Windows.'
}

$runSmoke = $PSBoundParameters.ContainsKey('SmokeSeconds')
if ($runSmoke -and (
        [string]::IsNullOrWhiteSpace($CliPath) -or
        -not [IO.Path]::IsPathFullyQualified($CliPath) -or
        -not (Test-Path -LiteralPath $CliPath -PathType Leaf))) {
    throw 'Smoke requires -CliPath with an existing absolute Aspire executable path.'
}

$project = Join-Path $PSScriptRoot 'Aspire.Tray.Windows.csproj'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = if ($PublishDirectory) {
    [IO.Path]::GetFullPath($PublishDirectory)
} else {
    Join-Path $repository "artifacts/bin/Aspire.Tray.Windows/$Configuration/net10.0/win-$Architecture/publish"
}

if (-not $SkipPublish) {
    & dotnet publish $project -c $Configuration -r "win-$Architecture" --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT publish failed with exit code $LASTEXITCODE."
    }
}
& (Join-Path $repository 'tools/CreateLayout/verify-windows-tray-payload.ps1') `
    -PublishDirectory $output -Rid "win-$Architecture"
Write-Host "NativeAOT output: $output"

if ($runSmoke) {
    $processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    if ($processArchitecture -ine $Architecture) {
        throw "Native smoke requires a matching $Architecture runner (current process: $processArchitecture)."
    }
    $executable = Join-Path $output 'aspire-tray.exe'
    $logs = Join-Path $repository "artifacts/log/$Configuration/tray-win-$Architecture"
    $null = New-Item -ItemType Directory -Path $logs -Force
    $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = $output
    foreach ($argument in @('--cli', $CliPath, '--smoke-seconds', "$SmokeSeconds")) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        # WinExe invocation does not imply a console wait. Observe the real process
        # and drain both pipes concurrently so UI/helper diagnostics cannot deadlock.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(($SmokeSeconds + 30) * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Tray smoke timed out. Logs: $logs."
        }
        if ($process.ExitCode -ne 0) {
            throw "Tray smoke failed with exit code $($process.ExitCode). Logs: $logs."
        }
    }
    finally {
        if ($stdout -and $stderr) {
            $outText = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $logs 'smoke.stdout.log'), $outText)
            [IO.File]::WriteAllText((Join-Path $logs 'smoke.stderr.log'), $errorText)
            Write-Host $outText
            Write-Host $errorText
        }
        $process.Dispose()
    }
    if (-not $errorText.Contains('Windows native smoke passed:', [StringComparison]::Ordinal)) {
        throw "Tray exited without completing the NativeSmokeHarness assertions. Logs: $logs."
    }
    Write-Host 'Native UI smoke assertions passed. Fixture-only smoke does not validate live AppHost connectivity.'
}
