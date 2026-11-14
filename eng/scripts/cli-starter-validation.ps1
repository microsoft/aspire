#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = "Pull request number used to select the PR dogfood channel")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$PRNumber,

    [Parameter(HelpMessage = "Maximum number of seconds allowed for aspire start to complete")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$MaxStartupSeconds = 120,

    [Parameter(HelpMessage = "Maximum number of seconds to wait for each expected resource to reach the requested status")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$ResourceReadyTimeoutSeconds = 120,

    [Parameter(HelpMessage = "Directory used to store starter validation projects and diagnostics")]
    [string]$ValidationRoot = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

function Get-ValidationRoot
{
    if (-not [string]::IsNullOrWhiteSpace($ValidationRoot))
    {
        return $ValidationRoot
    }

    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP))
    {
        return (Join-Path $env:RUNNER_TEMP 'aspire-cli-starter-validation')
    }

    return (Join-Path ([System.IO.Path]::GetTempPath()) 'aspire-cli-starter-validation')
}

function Get-FileContentOrEmpty
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path $Path)
    {
        return (Get-Content -Raw $Path)
    }

    return ''
}

function Get-CombinedProcessOutput
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$StdOutPath,

        [Parameter(Mandatory = $true)]
        [string]$StdErrPath
    )

    $stdout = Get-FileContentOrEmpty -Path $StdOutPath
    $stderr = Get-FileContentOrEmpty -Path $StdErrPath

    if ($stdout -and $stderr)
    {
        return ($stdout, $stderr) -join [Environment]::NewLine
    }

    return "$stdout$stderr"
}

function Write-DiagnosticMessage
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    Write-Host "[$([DateTimeOffset]::UtcNow.ToString('O'))] $Message"
}

function Write-DiagnosticFileTail
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path $Path))
    {
        Write-DiagnosticMessage "$Description does not exist at '$Path'."
        return
    }

    Write-DiagnosticMessage "$Description tail from '$Path':"
    Get-Content -Path $Path -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "| $_" }
}

function Write-AspireProcessSnapshot
{
    param(
        [Parameter(Mandatory = $true)]
        [int]$RootProcessId
    )

    Write-DiagnosticMessage "Process snapshot after timeout; launched CLI PID is $RootProcessId."

    try
    {
        $processes = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -eq $RootProcessId -or $_.ProcessName -match '^(aspire|dcp|dotnet|node|npm|pwsh)$' } |
            Select-Object Id, ProcessName, StartTime, CPU, Responding

        if ($processes)
        {
            Write-Host ($processes | Format-Table -AutoSize | Out-String -Width 240)
        }
        else
        {
            Write-Host 'No matching Aspire-related processes were found.'
        }
    }
    catch
    {
        Write-Warning "Failed to capture process snapshot: $($_.Exception.Message)"
    }
}

function Invoke-DetachedAspireCommand
{
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('run', 'start')]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string]$TemplateId,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $stdoutPath = Join-Path $DiagnosticsDirectory "aspire-$Command.stdout.json"
    $stderrPath = Join-Path $DiagnosticsDirectory "aspire-$Command.stderr.log"
    $combinedPath = Join-Path $DiagnosticsDirectory "aspire-$Command.log"
    $arguments = @($Command)
    if ($Command -eq 'run')
    {
        $arguments += '--detach'
    }
    $arguments += @('--format', 'json', '--non-interactive', '--nologo')

    $startedAt = Get-Date
    Write-DiagnosticMessage "${TemplateId}: invoking 'aspire $($arguments -join ' ')' in '$WorkingDirectory'."
    Write-DiagnosticMessage "${TemplateId}: stdout -> '$stdoutPath'; stderr -> '$stderrPath'."

    $process = Start-Process -FilePath 'aspire' `
        -ArgumentList $arguments `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    Write-DiagnosticMessage "${TemplateId}: Start-Process returned PID $($process.Id) for 'aspire $Command'."

    $nextHeartbeatSeconds = 30
    while (-not $process.WaitForExit(1000))
    {
        $elapsed = (Get-Date) - $startedAt
        if ($elapsed.TotalSeconds -ge $TimeoutSeconds)
        {
            Write-Warning "${TemplateId}: aspire $Command PID $($process.Id) did not exit within $TimeoutSeconds seconds."
            Write-DiagnosticFileTail -Path $stdoutPath -Description "aspire $Command stdout"
            Write-DiagnosticFileTail -Path $stderrPath -Description "aspire $Command stderr"
            Write-AspireProcessSnapshot -RootProcessId $process.Id

            if (-not $process.HasExited)
            {
                Write-DiagnosticMessage "${TemplateId}: force-stopping aspire $Command PID $($process.Id)."
                $process | Stop-Process -Force -ErrorAction SilentlyContinue
                [void]$process.WaitForExit(5000)
            }

            throw "${TemplateId}: aspire $Command did not exit within $TimeoutSeconds seconds."
        }

        if ($elapsed.TotalSeconds -ge $nextHeartbeatSeconds)
        {
            Write-DiagnosticMessage "${TemplateId}: aspire $Command PID $($process.Id) is still running after $([math]::Round($elapsed.TotalSeconds, 1)) seconds."
            $nextHeartbeatSeconds += 30
        }
    }

    Write-DiagnosticMessage "${TemplateId}: aspire $Command PID $($process.Id) exited with code $($process.ExitCode) after $([math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)) seconds."

    $output = Get-CombinedProcessOutput -StdOutPath $stdoutPath -StdErrPath $stderrPath
    Set-Content -Path $combinedPath -Value $output -Encoding utf8

    if ($process.ExitCode -ne 0)
    {
        throw "${TemplateId}: aspire $Command failed with exit code $($process.ExitCode)."
    }

    try
    {
        $metadata = Get-Content -Raw $stdoutPath | ConvertFrom-Json
    }
    catch
    {
        throw "${TemplateId}: aspire $Command did not produce valid JSON. Output: $output"
    }

    if ($null -eq $metadata -or
        $null -eq $metadata.PSObject.Properties['appHostPid'] -or
        $null -eq $metadata.PSObject.Properties['appHostPath'] -or
        $null -eq $metadata.PSObject.Properties['logFile'] -or
        $metadata.appHostPid -le 0 -or
        [string]::IsNullOrWhiteSpace([string]$metadata.appHostPath) -or
        [string]::IsNullOrWhiteSpace([string]$metadata.logFile))
    {
        throw "${TemplateId}: aspire $Command did not return the expected detached AppHost metadata. Output: $output"
    }

    return (Get-Date) - $startedAt
}

function Wait-AspireResource
{
    param(
        [string]$ResourceName,
        [string]$Phase,
        [string]$TemplateId,
        [string]$WorkingDirectory,
        [string]$DiagnosticsDirectory,
        [int]$TimeoutSeconds
    )

    $sanitizedResourceName = $ResourceName -replace '[^A-Za-z0-9_.-]', '_'
    $prefix = if ($Phase -eq 'run') { 'aspire-wait-run' } else { 'aspire-wait' }
    $stdoutPath = Join-Path $DiagnosticsDirectory "$prefix-${sanitizedResourceName}.stdout.log"
    $stderrPath = Join-Path $DiagnosticsDirectory "$prefix-${sanitizedResourceName}.stderr.log"
    $combinedPath = Join-Path $DiagnosticsDirectory "$prefix-${sanitizedResourceName}.log"

    Write-DiagnosticMessage "${TemplateId}: invoking aspire wait for resource '$ResourceName' after $Phase."
    $process = Start-Process -FilePath 'aspire' `
        -ArgumentList @('wait', $ResourceName, '--status', 'up', '--timeout', $TimeoutSeconds) `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -Wait `
        -PassThru
    Write-DiagnosticMessage "${TemplateId}: aspire wait for resource '$ResourceName' after $Phase returned with exit code $($process.ExitCode)."

    $output = Get-CombinedProcessOutput -StdOutPath $stdoutPath -StdErrPath $stderrPath
    Set-Content -Path $combinedPath -Value $output -Encoding utf8

    if ($process.ExitCode -ne 0)
    {
        try
        {
            # Capture installer logs before stopping the AppHost; they can explain
            # why a dependent resource entered FailedToStart.
            $logsFile = if ($Phase -eq 'run') { 'aspire-run-resource-logs.log' } else { 'aspire-resource-logs.log' }
            aspire logs --include-hidden *>&1 |
                Out-File -FilePath (Join-Path $DiagnosticsDirectory $logsFile) -Encoding utf8
        }
        catch
        {
            Write-Warning "${TemplateId}: failed to capture resource logs: $($_.Exception.Message)"
        }

        throw "${TemplateId}: aspire wait for resource $ResourceName failed with exit code $($process.ExitCode)."
    }
}

$validationRootPath = Get-ValidationRoot
Remove-Item -Recurse -Force $validationRootPath -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $validationRootPath -Force | Out-Null

$templates = @(
    @{
        TemplateId = 'aspire-ts-starter'
        ProjectName = 'AspireCliTsStarterSmoke'
        ExpectedResources = @('app', 'frontend')
    },
    @{
        TemplateId = 'aspire-starter'
        ProjectName = 'AspireCliCsStarterSmoke'
        ExpectedResources = @('apiservice')
    },
    @{
        TemplateId = 'aspire-go-starter'
        ProjectName = 'AspireCliGoStarterSmoke'
        # Hosted macOS/Windows runners have no Linux container runtime. Keep full Redis
        # readiness on Linux; the other runners exercise the supported cache-free starter.
        ExpectedResources = $(if ($IsLinux) { @('app', 'frontend', 'cache') } else { @('app', 'frontend') })
    }
)

$failures = [System.Collections.Generic.List[string]]::new()

foreach ($template in $templates)
{
    $templateId = [string]$template.TemplateId
    $projectName = [string]$template.ProjectName
    $expectedResources = @($template.ExpectedResources)
    $templateRoot = Join-Path $validationRootPath $templateId
    $diagnosticsDir = Join-Path $templateRoot 'diagnostics'
    $projectRoot = Join-Path $templateRoot $projectName
    $addLogPath = Join-Path $diagnosticsDir 'aspire-add.log'
    $postRunStopLogPath = Join-Path $diagnosticsDir 'aspire-stop-after-run.log'
    $preStartStopLogPath = Join-Path $diagnosticsDir 'aspire-stop-before-start.log'
    $stopLogPath = Join-Path $diagnosticsDir 'aspire-stop.log'

    New-Item -ItemType Directory -Path $templateRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $diagnosticsDir -Force | Out-Null

    Push-Location $templateRoot
    try
    {
        try
        {
            $newStartedAt = Get-Date
            Write-DiagnosticMessage "${templateId}: invoking aspire new."
            $templateArguments = @()
            if ($templateId -eq 'aspire-go-starter')
            {
                $templateArguments = @('--use-redis-cache', $(if ($IsLinux) { 'true' } else { 'false' }))
            }
            aspire new $templateId --name $projectName --output $projectRoot --channel "pr-$PRNumber" --non-interactive --nologo --suppress-agent-init @templateArguments
            Write-DiagnosticMessage "${templateId}: aspire new returned after $([math]::Round(((Get-Date) - $newStartedAt).TotalSeconds, 1)) seconds."

            Push-Location $projectRoot
            try
            {
                $addStartedAt = Get-Date
                Write-DiagnosticMessage "${templateId}: invoking aspire add."
                aspire add Aspire.Hosting.PostgreSQL --non-interactive --nologo *>&1 | Tee-Object -FilePath $addLogPath
                Write-DiagnosticMessage "${templateId}: aspire add returned after $([math]::Round(((Get-Date) - $addStartedAt).TotalSeconds, 1)) seconds."

                $runElapsed = Invoke-DetachedAspireCommand `
                    -Command run `
                    -TemplateId $templateId `
                    -WorkingDirectory $projectRoot `
                    -DiagnosticsDirectory $diagnosticsDir `
                    -TimeoutSeconds $MaxStartupSeconds

                if ($templateId -eq 'aspire-go-starter')
                {
                    # The next start reuses this project. Finish the installer before stopping
                    # so its interrupted npm cleanup cannot race the next npm install.
                    Wait-AspireResource -ResourceName 'frontend' -Phase 'run' -TemplateId $templateId `
                        -WorkingDirectory $projectRoot -DiagnosticsDirectory $diagnosticsDir `
                        -TimeoutSeconds $ResourceReadyTimeoutSeconds
                }

                Write-DiagnosticMessage "${templateId}: invoking aspire stop after run."
                aspire stop --non-interactive --nologo *>&1 | Out-File -FilePath $postRunStopLogPath -Encoding utf8
                Write-DiagnosticMessage "${templateId}: aspire stop after run returned."
                Write-Host "$templateId aspire run started in $([math]::Round($runElapsed.TotalSeconds, 2)) seconds."
            }
            finally
            {
                Pop-Location
            }

            try
            {
                Write-DiagnosticMessage "${templateId}: invoking pre-start aspire stop."
                aspire stop *>&1 | Out-File -FilePath $preStartStopLogPath -Encoding utf8
                Write-DiagnosticMessage "${templateId}: pre-start aspire stop returned."
            }
            catch
            {
                $preStartStopOutput = Get-FileContentOrEmpty -Path $preStartStopLogPath
                if ($preStartStopOutput -notmatch 'No running apphost found\.')
                {
                    Write-Warning "$templateId pre-start cleanup with aspire stop failed: $($_.Exception.Message)"
                    if ($preStartStopOutput)
                    {
                        Write-Host $preStartStopOutput
                    }
                }
            }

            $startElapsed = Invoke-DetachedAspireCommand `
                -Command start `
                -TemplateId $templateId `
                -WorkingDirectory $projectRoot `
                -DiagnosticsDirectory $diagnosticsDir `
                -TimeoutSeconds $MaxStartupSeconds

            Set-Location $projectRoot

            $resourcesStdOutPath = Join-Path $diagnosticsDir 'aspire-resources.stdout.log'
            $resourcesStdErrPath = Join-Path $diagnosticsDir 'aspire-resources.stderr.log'
            $resourcesCombinedPath = Join-Path $diagnosticsDir 'aspire-resources.log'

            Write-DiagnosticMessage "${templateId}: invoking aspire resources."
            $resourcesProcess = Start-Process -FilePath 'aspire' `
                -ArgumentList @('resources') `
                -WorkingDirectory $projectRoot `
                -RedirectStandardOutput $resourcesStdOutPath `
                -RedirectStandardError $resourcesStdErrPath `
                -Wait `
                -PassThru
            Write-DiagnosticMessage "${templateId}: aspire resources returned with exit code $($resourcesProcess.ExitCode)."

            $resourcesOutput = Get-CombinedProcessOutput -StdOutPath $resourcesStdOutPath -StdErrPath $resourcesStdErrPath

            Set-Content -Path $resourcesCombinedPath -Value $resourcesOutput -Encoding utf8
            Write-Host $resourcesOutput

            if ($resourcesProcess.ExitCode -ne 0)
            {
                throw "${templateId}: aspire resources failed with exit code $($resourcesProcess.ExitCode)."
            }

            foreach ($resourceName in $expectedResources)
            {
                Wait-AspireResource -ResourceName $resourceName -Phase 'start' -TemplateId $templateId `
                    -WorkingDirectory $projectRoot -DiagnosticsDirectory $diagnosticsDir `
                    -TimeoutSeconds $ResourceReadyTimeoutSeconds
            }

            Write-Host "$templateId aspire start started in $([math]::Round($startElapsed.TotalSeconds, 2)) seconds."
        }
        catch
        {
            $message = $_.Exception.Message
            Write-Warning $message
            $failures.Add($message)
        }
    }
    finally
    {
        if (Test-Path $projectRoot)
        {
            Push-Location $projectRoot
            try
            {
                Write-DiagnosticMessage "${templateId}: invoking final cleanup aspire stop."
                aspire stop *>&1 | Out-File -FilePath $stopLogPath -Encoding utf8
                Write-DiagnosticMessage "${templateId}: final cleanup aspire stop returned."
            }
            catch
            {
                Write-Warning "$templateId cleanup with aspire stop failed: $($_.Exception.Message)"
                $stopOutput = Get-FileContentOrEmpty -Path $stopLogPath
                if ($stopOutput)
                {
                    Write-Host $stopOutput
                }
            }
            finally
            {
                Pop-Location
            }
        }

        Pop-Location
    }
}

if ($failures.Count -gt 0)
{
    throw ("Starter validation failures:`n- " + ($failures -join "`n- "))
}
