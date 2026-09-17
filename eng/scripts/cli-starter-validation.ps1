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
    $process = Start-Process -FilePath 'aspire' `
        -ArgumentList $arguments `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    try
    {
        $process | Wait-Process -Timeout $TimeoutSeconds -ErrorAction Stop
    }
    catch
    {
        if (-not $process.HasExited)
        {
            $process | Stop-Process -Force -ErrorAction SilentlyContinue
        }

        throw "${TemplateId}: aspire $Command did not exit within $TimeoutSeconds seconds."
    }

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
            aspire new $templateId --name $projectName --output $projectRoot --channel "pr-$PRNumber" --non-interactive --nologo --suppress-agent-init

            Push-Location $projectRoot
            try
            {
                aspire add Aspire.Hosting.PostgreSQL --non-interactive --nologo *>&1 | Tee-Object -FilePath $addLogPath

                $runElapsed = Invoke-DetachedAspireCommand `
                    -Command run `
                    -TemplateId $templateId `
                    -WorkingDirectory $projectRoot `
                    -DiagnosticsDirectory $diagnosticsDir `
                    -TimeoutSeconds $MaxStartupSeconds

                aspire stop --non-interactive --nologo *>&1 | Out-File -FilePath $postRunStopLogPath -Encoding utf8
                Write-Host "$templateId aspire run started in $([math]::Round($runElapsed.TotalSeconds, 2)) seconds."
            }
            finally
            {
                Pop-Location
            }

            try
            {
                aspire stop *>&1 | Out-File -FilePath $preStartStopLogPath -Encoding utf8
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

            $resourcesProcess = Start-Process -FilePath 'aspire' `
                -ArgumentList @('resources') `
                -WorkingDirectory $projectRoot `
                -RedirectStandardOutput $resourcesStdOutPath `
                -RedirectStandardError $resourcesStdErrPath `
                -Wait `
                -PassThru

            $resourcesOutput = Get-CombinedProcessOutput -StdOutPath $resourcesStdOutPath -StdErrPath $resourcesStdErrPath

            Set-Content -Path $resourcesCombinedPath -Value $resourcesOutput -Encoding utf8
            Write-Host $resourcesOutput

            if ($resourcesProcess.ExitCode -ne 0)
            {
                throw "${templateId}: aspire resources failed with exit code $($resourcesProcess.ExitCode)."
            }

            foreach ($resourceName in $expectedResources)
            {
                $sanitizedResourceName = $resourceName -replace '[^A-Za-z0-9_.-]', '_'
                $waitStdOutPath = Join-Path $diagnosticsDir "aspire-wait-${sanitizedResourceName}.stdout.log"
                $waitStdErrPath = Join-Path $diagnosticsDir "aspire-wait-${sanitizedResourceName}.stderr.log"
                $waitCombinedPath = Join-Path $diagnosticsDir "aspire-wait-${sanitizedResourceName}.log"

                $waitProcess = Start-Process -FilePath 'aspire' `
                    -ArgumentList @('wait', $resourceName, '--status', 'up', '--timeout', $ResourceReadyTimeoutSeconds) `
                    -WorkingDirectory $projectRoot `
                    -RedirectStandardOutput $waitStdOutPath `
                    -RedirectStandardError $waitStdErrPath `
                    -Wait `
                    -PassThru

                $waitOutput = Get-CombinedProcessOutput -StdOutPath $waitStdOutPath -StdErrPath $waitStdErrPath

                Set-Content -Path $waitCombinedPath -Value $waitOutput -Encoding utf8

                if ($waitProcess.ExitCode -ne 0)
                {
                    throw "${templateId}: aspire wait for resource $resourceName failed with exit code $($waitProcess.ExitCode)."
                }
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
                aspire stop *>&1 | Out-File -FilePath $stopLogPath -Encoding utf8
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
