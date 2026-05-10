param(
    [Alias("AspirePath")]
    [string]$TargetAspirePath,

    [Alias("DashboardAspirePath")]
    [string]$ProfilerAspirePath,

    [string]$LayoutPath,
    [string]$DcpPath,
    [string]$OutputRoot,
    [switch]$RequireDcpSpans,
    [int]$PostStartDelaySeconds = 0,
    [switch]$SkipBuild
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repoRoot "artifacts\tmp\startup-otel-harness"
}

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $OutputRoot $runId
$workspace = Join-Path $runRoot "workspace"
$projectDir = Join-Path $workspace "StartupOtelHarness"
$logsDir = Join-Path $runRoot "logs"
$exportDir = Join-Path $runRoot "export"
$exportZip = Join-Path $runRoot "startup-otel-export.zip"
$spanSummaryPath = Join-Path $runRoot "span-summary.json"

New-Item -ItemType Directory -Path $workspace, $logsDir, $exportDir -Force | Out-Null

function Write-Step($message) {
    Write-Host "==> $message"
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return $listener.LocalEndpoint.Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [switch]$AllowFailure
    )

    $stdoutPath = Join-Path $logsDir "$Name.stdout.txt"
    $stderrPath = Join-Path $logsDir "$Name.stderr.txt"

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments > $stdoutPath 2> $stderrPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $stderr = if (Test-Path $stderrPath) { Get-Content $stderrPath -Raw } else { "" }
        $stdout = if (Test-Path $stdoutPath) { Get-Content $stdoutPath -Raw } else { "" }
        throw @"
Command failed ($exitCode): $FilePath $($Arguments -join ' ')
stdout: $stdoutPath
$stdout
stderr: $stderrPath
$stderr
"@
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Stdout = $stdoutPath
        Stderr = $stderrPath
    }
}

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,

        [int]$TimeoutSeconds = 60
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Timed out waiting for $Url"
}

function Get-ChildProcessIds {
    param(
        [Parameter(Mandatory = $true)]
        [int]$ParentProcessId
    )

    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentProcessId" -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
        Get-ChildProcessIds -ParentProcessId $child.ProcessId
        $child.ProcessId
    }
}

function Stop-ProcessTree {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process
    )

    $processIds = @(Get-ChildProcessIds -ParentProcessId $Process.Id) + $Process.Id
    foreach ($processId in $processIds | Select-Object -Unique) {
        try {
            $target = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($target) {
                Stop-Process -Id $processId -Force
            }
        }
        catch {
            Write-Verbose "Failed to stop process ${processId}: $_"
        }
    }
}

function Set-ProcessEnvironmentVariable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string]$Value
    )

    $previous = [Environment]::GetEnvironmentVariable($Name, "Process")
    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
    return [pscustomobject]@{
        Name = $Name
        Previous = $previous
    }
}

function Restore-ProcessEnvironmentVariables {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Variables
    )

    foreach ($variable in $Variables) {
        [Environment]::SetEnvironmentVariable($variable.Name, $variable.Previous, "Process")
    }
}

if (-not $TargetAspirePath) {
    $TargetAspirePath = Join-Path $repoRoot "artifacts\bin\Aspire.Cli\Debug\net10.0\aspire.exe"
}

if (-not $ProfilerAspirePath) {
    $profilerAspireCommand = Get-Command aspire -ErrorAction SilentlyContinue
    if ($profilerAspireCommand) {
        $ProfilerAspirePath = $profilerAspireCommand.Source
    }
    else {
        $ProfilerAspirePath = $TargetAspirePath
    }
}

if (-not $LayoutPath -and $ProfilerAspirePath) {
    $profilerAspireDirectory = Split-Path $ProfilerAspirePath -Parent
    $candidateLayoutPath = Split-Path $profilerAspireDirectory -Parent
    if ($candidateLayoutPath -and (Test-Path (Join-Path $candidateLayoutPath "bundle"))) {
        $LayoutPath = $candidateLayoutPath
    }
}

if (-not $SkipBuild) {
    Write-Step "Building local Aspire CLI"
    Invoke-LoggedCommand -FilePath (Join-Path $repoRoot "restore.cmd") -Arguments @() -WorkingDirectory $repoRoot -Name "restore" | Out-Null
    Invoke-LoggedCommand -FilePath "dotnet" -Arguments @("build", "src\Aspire.Cli\Aspire.Cli.csproj", "--no-restore") -WorkingDirectory $repoRoot -Name "build-aspire-cli" | Out-Null
}

if (-not (Test-Path $TargetAspirePath)) {
    throw "Target Aspire CLI not found at $TargetAspirePath"
}

if (-not (Test-Path $ProfilerAspirePath)) {
    throw "Profiler Aspire CLI not found at $ProfilerAspirePath"
}

$dashboardPort = Get-FreeTcpPort
$otlpGrpcPort = Get-FreeTcpPort
$otlpHttpPort = Get-FreeTcpPort
$dashboardUrl = "http://localhost:$dashboardPort"
$otlpGrpcUrl = "http://localhost:$otlpGrpcPort"
$otlpHttpUrl = "http://localhost:$otlpHttpPort"

$dashboardStdout = Join-Path $logsDir "dashboard.stdout.txt"
$dashboardStderr = Join-Path $logsDir "dashboard.stderr.txt"
$dashboardArgs = @(
    "dashboard",
    "run",
    "--frontend-url",
    $dashboardUrl,
    "--otlp-grpc-url",
    $otlpGrpcUrl,
    "--otlp-http-url",
    $otlpHttpUrl,
    "--allow-anonymous"
)

$environmentSnapshot = @()
if ($LayoutPath) {
    Write-Step "Using Aspire bundle layout at $LayoutPath"
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "ASPIRE_LAYOUT_PATH" -Value $LayoutPath
}

Write-Step "Starting standalone dashboard at $dashboardUrl"
$dashboardProcess = Start-Process -FilePath $ProfilerAspirePath -ArgumentList $dashboardArgs -WorkingDirectory $runRoot -RedirectStandardOutput $dashboardStdout -RedirectStandardError $dashboardStderr -PassThru -WindowStyle Hidden

try {
    Wait-HttpReady -Url $dashboardUrl -TimeoutSeconds 90

    Write-Step "Configuring CLI diagnostic OTLP export to $otlpGrpcUrl"
    # Keep both forms: OTEL_* configures CLI/OpenTelemetry exporters, while ASPIRE_OTEL_* is
    # projected into AppHost IConfiguration as OTEL_* by DistributedApplicationBuilder.
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "ASPIRE_CLI_TELEMETRY_OPTOUT" -Value "true"
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "ASPIRE_PROFILING_ENABLED" -Value "true"
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "ASPIRE_STARTUP_PROFILING_ENABLED" -Value "true"
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "OTEL_EXPORTER_OTLP_ENDPOINT" -Value $otlpGrpcUrl
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "OTEL_EXPORTER_OTLP_PROTOCOL" -Value "grpc"
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "ASPIRE_OTEL_EXPORTER_OTLP_ENDPOINT" -Value $otlpGrpcUrl
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "ASPIRE_OTEL_EXPORTER_OTLP_PROTOCOL" -Value "grpc"

    if ($DcpPath) {
        $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "ASPIRE_DCP_PATH" -Value $DcpPath
    }

    Write-Step "Creating TypeScript AppHost fixture"
    $serviceDir = Join-Path $projectDir "service"
    New-Item -ItemType Directory -Path $projectDir, $serviceDir -Force | Out-Null
    $appHostDashboardPort = Get-FreeTcpPort
    $appHostOtlpGrpcPort = Get-FreeTcpPort
    $appHostResourceServicePort = Get-FreeTcpPort

    @"
{
  "appHost": {
    "path": "apphost.ts",
    "language": "typescript/nodejs"
  },
  "profiles": {
    "https": {
      "applicationUrl": "http://localhost:$appHostDashboardPort",
        "environmentVariables": {
          "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true",
          "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:$appHostOtlpGrpcPort",
          "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:$appHostResourceServicePort",
          "ASPIRE_PROFILING_ENABLED": "true",
          "ASPIRE_STARTUP_PROFILING_ENABLED": "true",
          "OTEL_EXPORTER_OTLP_ENDPOINT": "$otlpGrpcUrl",
          "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
          "ASPIRE_OTEL_EXPORTER_OTLP_ENDPOINT": "$otlpGrpcUrl",
          "ASPIRE_OTEL_EXPORTER_OTLP_PROTOCOL": "grpc"
        }
      }
    }
}
"@ | Set-Content -Path (Join-Path $projectDir "aspire.config.json") -Encoding UTF8

    @'
{
  "name": "startupotelharness",
  "private": true,
  "type": "module",
  "dependencies": {
    "vscode-jsonrpc": "^8.2.0"
  },
  "devDependencies": {
    "@types/node": "^22.0.0",
    "tsx": "^4.21.0",
    "typescript": "^5.9.3"
  }
}
'@ | Set-Content -Path (Join-Path $projectDir "package.json") -Encoding UTF8

    @'
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "esModuleInterop": true,
    "forceConsistentCasingInFileNames": true,
    "strict": true,
    "skipLibCheck": true,
    "outDir": "./dist/apphost",
    "rootDir": "."
  },
  "include": ["apphost.ts", ".modules/**/*.ts"],
  "exclude": ["node_modules"]
}
'@ | Set-Content -Path (Join-Path $projectDir "tsconfig.apphost.json") -Encoding UTF8

    @'
import http from 'node:http';

const port = Number(process.env.PORT ?? '0');
const server = http.createServer((request, response) => {
    response.writeHead(200, { 'content-type': 'text/plain' });
    response.end('startup otel harness');
});

server.listen(port, '127.0.0.1', () => {
    console.log(`startup otel harness listening on ${port}`);
});

process.on('SIGTERM', () => {
    server.close(() => process.exit(0));
});
'@ | Set-Content -Path (Join-Path $serviceDir "server.js") -Encoding UTF8

    @'
import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const worker = await builder.addExecutable("worker", "node", "./service", ["server.js"]);
await worker.withHttpEndpoint({ env: "PORT" });

const dependent = await builder.addExecutable("dependent", "node", "./service", ["server.js"]);
await dependent.withHttpEndpoint({ env: "PORT" });
await dependent.waitFor(worker);

await builder.build().run();
'@ | Set-Content -Path (Join-Path $projectDir "apphost.ts") -Encoding UTF8

    $appHostPath = Join-Path $projectDir "apphost.ts"

    Write-Step "Restoring TypeScript AppHost fixture"
    Invoke-LoggedCommand -FilePath $TargetAspirePath -Arguments @("restore", "--apphost", $appHostPath) -WorkingDirectory $projectDir -Name "restore-ts-apphost" | Out-Null

    Write-Step "Starting TypeScript AppHost with telemetry export enabled"
    $startResult = Invoke-LoggedCommand -FilePath $TargetAspirePath -Arguments @("start", "--isolated", "--format", "Json", "--apphost", $appHostPath) -WorkingDirectory $projectDir -Name "start"

    if ($PostStartDelaySeconds -gt 0) {
        Write-Step "Waiting ${PostStartDelaySeconds}s for profiling telemetry to flush"
        Start-Sleep -Seconds $PostStartDelaySeconds
    }

    Write-Step "Stopping TypeScript AppHost"
    Invoke-LoggedCommand -FilePath $TargetAspirePath -Arguments @("stop", "--apphost", $appHostPath) -WorkingDirectory $projectDir -Name "stop" | Out-Null

    Start-Sleep -Seconds 3

    Write-Step "Exporting standalone dashboard telemetry"
    Invoke-LoggedCommand -FilePath $TargetAspirePath -Arguments @("export", "--dashboard-url", $dashboardUrl, "--include-hidden", "--output", $exportZip) -WorkingDirectory $runRoot -Name "export" | Out-Null

    if (-not (Test-Path $exportZip)) {
        throw "Export zip was not created: $exportZip"
    }

    Expand-Archive -Path $exportZip -DestinationPath $exportDir -Force

    $requireDcpSpansValue = if ($RequireDcpSpans) { "true" } else { "false" }
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "REQUIRE_DCP_SPANS" -Value $requireDcpSpansValue
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "EXPORT_DIR" -Value $exportDir
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "SPAN_SUMMARY_PATH" -Value $spanSummaryPath
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "RUN_ROOT" -Value $runRoot
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "TARGET_ASPIRE_PATH" -Value (Resolve-Path $TargetAspirePath).Path
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "PROFILER_ASPIRE_PATH" -Value (Resolve-Path $ProfilerAspirePath).Path
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "POST_START_DELAY_SECONDS" -Value $PostStartDelaySeconds.ToString()
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "DASHBOARD_URL" -Value $dashboardUrl
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "OTLP_GRPC_URL" -Value $otlpGrpcUrl
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "OTLP_HTTP_URL" -Value $otlpHttpUrl
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "APPHOST_PATH" -Value $appHostPath
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "START_JSON_PATH" -Value $startResult.Stdout
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "EXPORT_ZIP" -Value $exportZip
    if ($LayoutPath) {
        $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "LAYOUT_PATH" -Value $LayoutPath
    }
    if ($DcpPath) {
        $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "DCP_PATH" -Value $DcpPath
    }

    Write-Step "Validating startup OTEL export"
    $environmentSnapshot += Set-ProcessEnvironmentVariable -Name "MSBUILDTERMINALLOGGER" -Value "false"
    & dotnet run (Join-Path $repoRoot "tools\StartupOtelValidator\ValidateStartupOtelExport.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Startup OTEL export validation failed. See $spanSummaryPath"
    }

    Write-Step "Startup OTEL harness passed"
}
finally {
    if ($environmentSnapshot.Count -gt 0) {
        Restore-ProcessEnvironmentVariables -Variables $environmentSnapshot
    }

    if ($dashboardProcess -and -not $dashboardProcess.HasExited) {
        Write-Step "Stopping standalone dashboard"
        Stop-ProcessTree -Process $dashboardProcess
    }
}
