param(
    [Parameter(Mandatory)]
    [string]$DashboardPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $DashboardPath -PathType Leaf)) {
    throw "Native AOT Dashboard executable was not found at '$DashboardPath'."
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$dashboardUrl = "http://127.0.0.1:$port"
$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-dashboard-smoke-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $testDirectory
$stdoutPath = Join-Path $testDirectory 'stdout.log'
$stderrPath = Join-Path $testDirectory 'stderr.log'
$dashboardProcess = $null

try {
    $dashboardProcess = Start-Process `
        -FilePath $DashboardPath `
        -ArgumentList @(
            "--ASPNETCORE_URLS=$dashboardUrl",
            '--ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true'
        ) `
        -WorkingDirectory (Split-Path -Parent $DashboardPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddMinutes(1)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($dashboardProcess.HasExited) {
            throw "Native AOT Dashboard exited before becoming ready with exit code $($dashboardProcess.ExitCode)."
        }

        try {
            $assetResponse = Invoke-WebRequest -Uri "$dashboardUrl/_framework/blazor.web.js" -TimeoutSec 5 -UseBasicParsing
            if ($assetResponse.StatusCode -eq 200 -and
                $assetResponse.RawContentLength -gt 0) {
                Write-Host "Native AOT Dashboard smoke test passed at $dashboardUrl."
                return
            }
        }
        catch {
            # The server can accept TCP connections before static assets are ready.
        }

        [System.Threading.Thread]::Sleep(500)
    }

    throw 'Timed out waiting for the Native AOT Dashboard HTTP endpoints.'
}
catch {
    $failure = $_
    [Console]::Error.WriteLine($failure)
    if (Test-Path -LiteralPath $stdoutPath) {
        Write-Host 'Dashboard stdout:'
        Get-Content -LiteralPath $stdoutPath
    }
    if (Test-Path -LiteralPath $stderrPath) {
        Write-Host 'Dashboard stderr:'
        Get-Content -LiteralPath $stderrPath
    }
    throw $failure
}
finally {
    if ($dashboardProcess -and -not $dashboardProcess.HasExited) {
        Stop-Process -Id $dashboardProcess.Id -Force
        $dashboardProcess.WaitForExit()
    }
    Remove-Item -LiteralPath $testDirectory -Recurse -Force -ErrorAction SilentlyContinue
}