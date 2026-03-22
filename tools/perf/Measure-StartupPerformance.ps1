<#
.SYNOPSIS
    Measures .NET Aspire application startup performance by collecting ETW traces.

.DESCRIPTION
    This script runs an Aspire application, collects a performance trace
    using dotnet-trace, and computes the startup time from AspireEventSource events.
    The trace collection ends when the DcpModelCreationStop event is fired.

.PARAMETER ProjectPath
    Path to the AppHost project (.csproj) to measure. Can be absolute or relative.
    Defaults to the TestShop.AppHost project in the playground folder.

.PARAMETER Iterations
    Number of times to run the scenario and collect traces. Defaults to 1.

.PARAMETER PreserveTraces
    If specified, trace files are preserved after the run. By default, traces are
    stored in a temporary folder and deleted after analysis.

.PARAMETER TraceOutputDirectory
    Directory where trace files will be saved when PreserveTraces is set.
    Defaults to a 'traces' subdirectory in the script folder.

.PARAMETER SkipBuild
    If specified, skips building the project before running.

.PARAMETER TraceDurationSeconds
    Duration in seconds for the trace collection. Defaults to 60 (1 minute).
    The value is automatically converted to the dd:hh:mm:ss format required by dotnet-trace.

.PARAMETER PauseBetweenIterationsSeconds
    Number of seconds to pause between iterations. Defaults to 15.
    Set to 0 to disable the pause.

.PARAMETER Verbose
    If specified, shows detailed output during execution.

.EXAMPLE
    .\Measure-StartupPerformance.ps1

.EXAMPLE
    .\Measure-StartupPerformance.ps1 -Iterations 5

.EXAMPLE
    .\Measure-StartupPerformance.ps1 -ProjectPath "C:\MyApp\MyApp.AppHost.csproj" -Iterations 3

.EXAMPLE
    .\Measure-StartupPerformance.ps1 -Iterations 3 -PreserveTraces -TraceOutputDirectory "C:\traces"

.EXAMPLE
    .\Measure-StartupPerformance.ps1 -TraceDurationSeconds 120

.EXAMPLE
    .\Measure-StartupPerformance.ps1 -Iterations 5 -PauseBetweenIterationsSeconds 30

.NOTES
    Requires:
    - PowerShell 7+
    - dotnet-trace global tool (dotnet tool install -g dotnet-trace)
    - .NET SDK
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 100)]
    [int]$Iterations = 1,

    [Parameter(Mandatory = $false)]
    [switch]$PreserveTraces,

    [Parameter(Mandatory = $false)]
    [string]$TraceOutputDirectory,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 86400)]
    [int]$TraceDurationSeconds = 60,

    [Parameter(Mandatory = $false)]
    [ValidateRange(0, 3600)]
    [int]$PauseBetweenIterationsSeconds = 45
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Constants
$EventSourceName = 'Microsoft-Aspire-Hosting'

# Get repository root (script is in tools/perf)
$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir '..' '..')).Path

# Resolve project path
if (-not $ProjectPath) {
    # Default to TestShop.AppHost
    $ProjectPath = Join-Path $RepoRoot 'playground' 'TestShop' 'TestShop.AppHost' 'TestShop.AppHost.csproj'
}
elseif (-not [System.IO.Path]::IsPathRooted($ProjectPath)) {
    # Relative path - resolve from current directory
    $ProjectPath = (Resolve-Path $ProjectPath -ErrorAction Stop).Path
}

$AppHostProject = $ProjectPath
$AppHostDir = Split-Path $AppHostProject -Parent
$AppHostName = [System.IO.Path]::GetFileNameWithoutExtension($AppHostProject)

# Determine output directory for traces - always use temp directory unless explicitly specified
if ($TraceOutputDirectory) {
    $OutputDirectory = $TraceOutputDirectory
}
else {
    # Always use a temp directory for traces
    $OutputDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-perf-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"
}

# Only delete temp directory if not preserving traces and no custom directory was specified
$ShouldCleanupDirectory = -not $PreserveTraces -and -not $TraceOutputDirectory

# Ensure output directory exists
if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

# Verify prerequisites
function Test-Prerequisites {
    Write-Host "Checking prerequisites..." -ForegroundColor Cyan

    # Check dotnet-trace is installed
    $dotnetTrace = Get-Command 'dotnet-trace' -ErrorAction SilentlyContinue
    if (-not $dotnetTrace) {
        throw "dotnet-trace is not installed. Install it with: dotnet tool install -g dotnet-trace"
    }
    Write-Verbose "dotnet-trace found at: $($dotnetTrace.Source)"

    # Check project exists
    if (-not (Test-Path $AppHostProject)) {
        throw "AppHost project not found at: $AppHostProject"
    }
    Write-Verbose "AppHost project found at: $AppHostProject"

    Write-Host "Prerequisites check passed." -ForegroundColor Green
}

# Build the project
function Build-AppHost {
    Write-Host "Building $AppHostName..." -ForegroundColor Cyan

    Push-Location $AppHostDir
    try {
        $buildOutput = & dotnet build -c Release --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host ($buildOutput -join "`n") -ForegroundColor Red
            throw "Failed to build $AppHostName"
        }
        Write-Verbose ($buildOutput -join "`n")
        Write-Host "Build completed successfully." -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

# Run a single iteration of the performance test
function Invoke-PerformanceIteration {
    param(
        [int]$IterationNumber,
        [string]$TraceOutputPath
    )

    Write-Host "`nIteration $IterationNumber" -ForegroundColor Yellow
    Write-Host ("-" * 40) -ForegroundColor Yellow

    $nettracePath = "$TraceOutputPath.nettrace"
    $appProcess = $null
    $traceProcess = $null

    try {
        # Find the compiled executable - we need the path to launch it
        $exePath = $null
        $dllPath = $null

        # Search in multiple possible output locations:
        # 1. Arcade-style: artifacts/bin/<ProjectName>/Release/<tfm>/
        # 2. Traditional: <ProjectDir>/bin/Release/<tfm>/
        $searchPaths = @(
            (Join-Path $RepoRoot 'artifacts' 'bin' $AppHostName 'Release'),
            (Join-Path $AppHostDir 'bin' 'Release')
        )

        foreach ($basePath in $searchPaths) {
            if (-not (Test-Path $basePath)) {
                continue
            }

            # Find TFM subdirectories (e.g., net8.0, net9.0, net10.0)
            $tfmDirs = Get-ChildItem -Path $basePath -Directory -Filter 'net*' -ErrorAction SilentlyContinue
            foreach ($tfmDir in $tfmDirs) {
                $candidateExe = Join-Path $tfmDir.FullName "$AppHostName.exe"
                $candidateDll = Join-Path $tfmDir.FullName "$AppHostName.dll"

                if (Test-Path $candidateExe) {
                    $exePath = $candidateExe
                    Write-Verbose "Found executable at: $exePath"
                    break
                }
                elseif (Test-Path $candidateDll) {
                    $dllPath = $candidateDll
                    Write-Verbose "Found DLL at: $dllPath"
                    break
                }
            }

            if ($exePath -or $dllPath) {
                break
            }
        }

        if (-not $exePath -and -not $dllPath) {
            $searchedPaths = $searchPaths -join "`n  - "
            throw "Could not find compiled executable or DLL. Searched in:`n  - $searchedPaths`nPlease build the project first (without -SkipBuild)."
        }

        # Read launchSettings.json to get environment variables
        $launchSettingsPath = Join-Path $AppHostDir 'Properties' 'launchSettings.json'
        $envVars = @{}
        if (Test-Path $launchSettingsPath) {
            Write-Verbose "Reading launch settings from: $launchSettingsPath"
            try {
                # Read the file and remove JSON comments (// style) before parsing
                # Only remove lines that start with // (after optional whitespace) to avoid breaking URLs like https://
                $jsonLines = Get-Content $launchSettingsPath
                $filteredLines = $jsonLines | Where-Object { $_.Trim() -notmatch '^//' }
                $jsonContent = $filteredLines -join "`n"
                $launchSettings = $jsonContent | ConvertFrom-Json

                # Try to find a suitable profile (prefer 'http' for simplicity, then first available)
                $launchProfile = $null
                if ($launchSettings.profiles.http) {
                    $launchProfile = $launchSettings.profiles.http
                    Write-Verbose "Using 'http' launch profile"
                }
                elseif ($launchSettings.profiles.https) {
                    $launchProfile = $launchSettings.profiles.https
                    Write-Verbose "Using 'https' launch profile"
                }
                else {
                    # Use first profile that has environmentVariables
                    foreach ($prop in $launchSettings.profiles.PSObject.Properties) {
                        if ($prop.Value.environmentVariables) {
                            $launchProfile = $prop.Value
                            Write-Verbose "Using '$($prop.Name)' launch profile"
                            break
                        }
                    }
                }

                if ($launchProfile -and $launchProfile.environmentVariables) {
                    foreach ($prop in $launchProfile.environmentVariables.PSObject.Properties) {
                        $envVars[$prop.Name] = $prop.Value
                        Write-Verbose "  Environment: $($prop.Name)=$($prop.Value)"
                    }
                }

                # Use applicationUrl to set ASPNETCORE_URLS if not already set
                if ($launchProfile -and $launchProfile.applicationUrl -and -not $envVars.ContainsKey('ASPNETCORE_URLS')) {
                    $envVars['ASPNETCORE_URLS'] = $launchProfile.applicationUrl
                    Write-Verbose "  Environment: ASPNETCORE_URLS=$($launchProfile.applicationUrl) (from applicationUrl)"
                }
            }
            catch {
                Write-Warning "Failed to parse launchSettings.json: $_"
            }
        }
        else {
            Write-Verbose "No launchSettings.json found at: $launchSettingsPath"
        }

        # Always ensure Development environment is set
        if (-not $envVars.ContainsKey('DOTNET_ENVIRONMENT')) {
            $envVars['DOTNET_ENVIRONMENT'] = 'Development'
        }
        if (-not $envVars.ContainsKey('ASPNETCORE_ENVIRONMENT')) {
            $envVars['ASPNETCORE_ENVIRONMENT'] = 'Development'
        }

        # Start the AppHost application as a separate process
        Write-Host "Starting $AppHostName..." -ForegroundColor Cyan

        $appPsi = [System.Diagnostics.ProcessStartInfo]::new()
        if ($exePath) {
            $appPsi.FileName = $exePath
            $appPsi.Arguments = ''
        }
        else {
            $appPsi.FileName = 'dotnet'
            $appPsi.Arguments = "`"$dllPath`""
        }
        $appPsi.WorkingDirectory = $AppHostDir
        $appPsi.UseShellExecute = $false
        $appPsi.RedirectStandardOutput = $true
        $appPsi.RedirectStandardError = $true
        $appPsi.CreateNoWindow = $true

        # Set environment variables from launchSettings.json
        foreach ($key in $envVars.Keys) {
            $appPsi.Environment[$key] = $envVars[$key]
        }

        $appProcess = [System.Diagnostics.Process]::Start($appPsi)
        $appPid = $appProcess.Id

        Write-Verbose "$AppHostName started with PID: $appPid"

        # Give the process a moment to initialize before attaching
        Start-Sleep -Milliseconds 200

        # Verify the process is still running
        if ($appProcess.HasExited) {
            $stdout = $appProcess.StandardOutput.ReadToEnd()
            $stderr = $appProcess.StandardError.ReadToEnd()
            throw "Application exited immediately with code $($appProcess.ExitCode).`nStdOut: $stdout`nStdErr: $stderr"
        }

        # Start dotnet-trace to attach to the running process
        Write-Host "Attaching trace collection to PID $appPid..." -ForegroundColor Cyan

        # Use dotnet-trace with the EventSource provider
        # Format: ProviderName:Keywords:Level
        # Keywords=0xFFFFFFFF (all), Level=5 (Verbose)
        $providers = "${EventSourceName}"

        # Convert TraceDurationSeconds to dd:hh:mm:ss format required by dotnet-trace
        $days = [math]::Floor($TraceDurationSeconds / 86400)
        $hours = [math]::Floor(($TraceDurationSeconds % 86400) / 3600)
        $minutes = [math]::Floor(($TraceDurationSeconds % 3600) / 60)
        $seconds = $TraceDurationSeconds % 60
        $traceDuration = '{0:00}:{1:00}:{2:00}:{3:00}' -f $days, $hours, $minutes, $seconds

        $traceArgs = @(
            'collect',
            '--process-id', $appPid,
            '--providers', $providers,
            '--output', $nettracePath,
            '--format', 'nettrace',
            '--duration', $traceDuration,
            '--buffersize', '8192'
        )

        Write-Verbose "dotnet-trace arguments: $($traceArgs -join ' ')"

        $tracePsi = [System.Diagnostics.ProcessStartInfo]::new()
        $tracePsi.FileName = 'dotnet-trace'
        $tracePsi.Arguments = $traceArgs -join ' '
        $tracePsi.WorkingDirectory = $AppHostDir
        $tracePsi.UseShellExecute = $false
        $tracePsi.RedirectStandardOutput = $true
        $tracePsi.RedirectStandardError = $true
        $tracePsi.CreateNoWindow = $true

        $traceProcess = [System.Diagnostics.Process]::Start($tracePsi)

        Write-Host "Collecting performance trace..." -ForegroundColor Cyan

        # Wait for trace to complete
        $traceProcess.WaitForExit()

        # Read app process output (what was captured while trace was running)
        # Use async read to avoid blocking - read whatever is available
        $appStdout = ""
        $appStderr = ""
        if ($appProcess -and -not $appProcess.HasExited) {
            # Process is still running, we can try to read available output
            # Note: ReadToEnd would block, so we read what's available after stopping
        }

        $traceOutput = $traceProcess.StandardOutput.ReadToEnd()
        $traceError = $traceProcess.StandardError.ReadToEnd()

        if ($traceOutput) { Write-Verbose "dotnet-trace output: $traceOutput" }
        if ($traceError) { Write-Verbose "dotnet-trace stderr: $traceError" }

        # Check if trace file was created despite any errors
        # dotnet-trace may report errors during cleanup but the trace file is often still valid
        if ($traceProcess.ExitCode -ne 0) {
            if (Test-Path $nettracePath) {
                Write-Warning "dotnet-trace exited with code $($traceProcess.ExitCode), but trace file was created. Attempting to analyze."
            }
            else {
                Write-Warning "dotnet-trace exited with code $($traceProcess.ExitCode) and no trace file was created."
                return $null
            }
        }

        Write-Host "Trace collection completed." -ForegroundColor Green

        return $nettracePath
    }
    finally {
        # Clean up the application process and capture its output
        if ($appProcess) {
            # Read any remaining output before killing the process
            $appStdout = ""
            $appStderr = ""
            try {
                # Give a moment for any buffered output
                Start-Sleep -Milliseconds 100

                # We need to read asynchronously since the process may still be running
                # Read what's available without blocking indefinitely
                $stdoutTask = $appProcess.StandardOutput.ReadToEndAsync()
                $stderrTask = $appProcess.StandardError.ReadToEndAsync()

                # Wait briefly for output
                [System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask), 1000) | Out-Null

                if ($stdoutTask.IsCompleted) {
                    $appStdout = $stdoutTask.Result
                }
                if ($stderrTask.IsCompleted) {
                    $appStderr = $stderrTask.Result
                }
            }
            catch {
                # Ignore errors reading output
            }

            if ($appStdout) {
                Write-Verbose "Application stdout:`n$appStdout"
            }
            if ($appStderr) {
                Write-Verbose "Application stderr:`n$appStderr"
            }

            if (-not $appProcess.HasExited) {
                Write-Verbose "Stopping $AppHostName (PID: $($appProcess.Id))..."
                try {
                    # Try graceful shutdown first
                    $appProcess.Kill($true)
                    $appProcess.WaitForExit(5000) | Out-Null
                }
                catch {
                    Write-Warning "Failed to stop application: $_"
                }
            }
            $appProcess.Dispose()
        }

        # Clean up trace process
        if ($traceProcess) {
            if (-not $traceProcess.HasExited) {
                try {
                    $traceProcess.Kill()
                    $traceProcess.WaitForExit(2000) | Out-Null
                }
                catch {
                    # Ignore errors killing trace process
                }
            }
            $traceProcess.Dispose()
        }
    }
}

# Path to the trace analyzer tool
$TraceAnalyzerDir = Join-Path $ScriptDir 'TraceAnalyzer'
$TraceAnalyzerProject = Join-Path $TraceAnalyzerDir 'TraceAnalyzer.csproj'

# Build the trace analyzer tool
function Build-TraceAnalyzer {
    if (-not (Test-Path $TraceAnalyzerProject)) {
        Write-Warning "TraceAnalyzer project not found at: $TraceAnalyzerProject"
        return $false
    }

    Write-Verbose "Building TraceAnalyzer tool..."
    $buildOutput = & dotnet build $TraceAnalyzerProject -c Release --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to build TraceAnalyzer: $buildOutput"
        return $false
    }

    Write-Verbose "TraceAnalyzer built successfully"
    return $true
}

# Parse nettrace file using the TraceAnalyzer tool
function Get-StartupTiming {
    param(
        [string]$TracePath
    )

    Write-Host "Analyzing trace: $TracePath" -ForegroundColor Cyan

    if (-not (Test-Path $TracePath)) {
        Write-Warning "Trace file not found: $TracePath"
        return $null
    }

    try {
        $output = & dotnet run --project $TraceAnalyzerProject -c Release --no-build -- $TracePath 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "TraceAnalyzer failed: $output"
            return $null
        }

        $result = $output | Select-Object -Last 1
        if ($result -eq 'null') {
            Write-Warning "Could not find DcpModelCreation events in the trace"
            return $null
        }

        $duration = [double]::Parse($result, [System.Globalization.CultureInfo]::InvariantCulture)
        Write-Verbose "Calculated duration: $duration ms"
        return $duration
    }
    catch {
        Write-Warning "Error parsing trace: $_"
        return $null
    }
}

# Main execution
function Main {
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host " Aspire Startup Performance Measurement" -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Project: $AppHostName"
    Write-Host "Project Path: $AppHostProject"
    Write-Host "Iterations: $Iterations"
    Write-Host "Trace Duration: $TraceDurationSeconds seconds"
    Write-Host "Pause Between Iterations: $PauseBetweenIterationsSeconds seconds"
    Write-Host "Preserve Traces: $PreserveTraces"
    if ($PreserveTraces -or $TraceOutputDirectory) {
        Write-Host "Trace Directory: $OutputDirectory"
    }
    Write-Host ""

    Test-Prerequisites

    # Build the TraceAnalyzer tool for parsing traces
    $traceAnalyzerAvailable = Build-TraceAnalyzer

    # Ensure output directory exists
    if (-not (Test-Path $OutputDirectory)) {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    }

    if (-not $SkipBuild) {
        Build-AppHost
    }
    else {
        Write-Host "Skipping build (SkipBuild flag set)" -ForegroundColor Yellow
    }

    $results = @()
    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'

    try {
        for ($i = 1; $i -le $Iterations; $i++) {
            $traceBaseName = "${AppHostName}_startup_${timestamp}_iter${i}"
            $traceOutputPath = Join-Path $OutputDirectory $traceBaseName

            $tracePath = Invoke-PerformanceIteration -IterationNumber $i -TraceOutputPath $traceOutputPath

            if ($tracePath -and (Test-Path $tracePath)) {
                $duration = $null
                if ($traceAnalyzerAvailable) {
                    $duration = Get-StartupTiming -TracePath $tracePath
                }

                if ($null -ne $duration) {
                    $results += [PSCustomObject]@{
                        Iteration = $i
                        TracePath = $tracePath
                        StartupTimeMs = [math]::Round($duration, 2)
                    }
                    Write-Host "Startup time: $([math]::Round($duration, 2)) ms" -ForegroundColor Green
                }
                else {
                    $results += [PSCustomObject]@{
                        Iteration = $i
                        TracePath = $tracePath
                        StartupTimeMs = $null
                    }
                    Write-Host "Trace collected: $tracePath" -ForegroundColor Green
                }
            }
            else {
                Write-Warning "No trace file generated for iteration $i"
            }

            # Pause between iterations
            if ($i -lt $Iterations -and $PauseBetweenIterationsSeconds -gt 0) {
                Write-Verbose "Pausing for $PauseBetweenIterationsSeconds seconds before next iteration..."
                Start-Sleep -Seconds $PauseBetweenIterationsSeconds
            }
        }
    }
    finally {
        # Clean up temporary trace directory if not preserving traces
        if ($ShouldCleanupDirectory -and (Test-Path $OutputDirectory)) {
            Write-Verbose "Cleaning up temporary trace directory: $OutputDirectory"
            Remove-Item -Path $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Summary
    Write-Host ""
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host " Results Summary" -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan

    # Wrap in @() to ensure array even with single/null results
    $validResults = @($results | Where-Object { $null -ne $_.StartupTimeMs })

    if ($validResults.Count -gt 0) {
        Write-Host ""
        # Only show TracePath in summary if PreserveTraces is set
        if ($PreserveTraces) {
            $results | Format-Table -AutoSize
        }
        else {
            $results | Select-Object Iteration, StartupTimeMs | Format-Table -AutoSize
        }

        $times = @($validResults | ForEach-Object { $_.StartupTimeMs })
        $avg = ($times | Measure-Object -Average).Average
        $min = ($times | Measure-Object -Minimum).Minimum
        $max = ($times | Measure-Object -Maximum).Maximum

        Write-Host ""
        Write-Host "Statistics:" -ForegroundColor Yellow
        Write-Host "  Successful iterations: $($validResults.Count) / $Iterations"
        Write-Host "  Minimum: $([math]::Round($min, 2)) ms"
        Write-Host "  Maximum: $([math]::Round($max, 2)) ms"
        Write-Host "  Average: $([math]::Round($avg, 2)) ms"

        if ($validResults.Count -gt 1) {
            $stdDev = [math]::Sqrt(($times | ForEach-Object { [math]::Pow($_ - $avg, 2) } | Measure-Object -Average).Average)
            Write-Host "  Std Dev: $([math]::Round($stdDev, 2)) ms"
        }

        if ($PreserveTraces) {
            Write-Host ""
            Write-Host "Trace files saved to: $OutputDirectory" -ForegroundColor Cyan
        }
    }
    elseif ($results.Count -gt 0) {
        Write-Host ""
        Write-Host "Collected $($results.Count) trace(s) but could not extract timing." -ForegroundColor Yellow
        if ($PreserveTraces) {
            Write-Host ""
            Write-Host "Trace files saved to: $OutputDirectory" -ForegroundColor Cyan
            $results | Select-Object Iteration, TracePath | Format-Table -AutoSize
            Write-Host ""
            Write-Host "Open traces in PerfView or Visual Studio to analyze startup timing." -ForegroundColor Yellow
        }
    }
    else {
        Write-Warning "No traces were collected."
    }
}

# Run the script
Main
