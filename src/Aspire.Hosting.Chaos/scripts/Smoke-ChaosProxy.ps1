# Smoke-ChaosProxy.ps1
#
# End-to-end smoke test for the chaos proxy. Installs a policy for each
# transform at runtime via POST /chaos/policies, probes it through the proxy,
# verifies the expected wire shape fired, captures fire counts, and tears
# down each policy. Mirrors the M3 run-to-green model: nothing is pre-installed
# on the proxy; each test owns its own install + teardown.
#
# Usage:
#   $env:CHAOS_PROXY_ENABLED = 'true'
#   $env:ASPIRE_CONTAINER_RUNTIME = 'podman'
#   # ... start the AppHost: dotnet run --project src/Chaos.Studio.V2.AppHost ...
#   # ... discover one of the mesh proxy URLs via:
#   #     podman ps --format "{{.Names}}|{{.Ports}}" | Select-String mesh
#   ./Smoke-ChaosProxy.ps1 -ProxyBaseUrl 'http://localhost:39257'
#
# Exits 0 on success, 1 on any assertion failure.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProxyBaseUrl
)

$ErrorActionPreference = 'Stop'
$failures = @()
$installedPolicyIds = @()

# ----------------------------------------------------------------- helpers

function Assert-Eq {
    param([string]$Label, $Expected, $Actual)
    if ($Expected -eq $Actual) {
        Write-Host "  PASS  $Label = $Actual" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $Label`: expected '$Expected', got '$Actual'" -ForegroundColor Red
        $script:failures += "$Label`: expected '$Expected', got '$Actual'"
    }
}

function Assert-Ge {
    param([string]$Label, [int]$Threshold, [int]$Actual)
    if ($Actual -ge $Threshold) {
        Write-Host "  PASS  $Label = ${Actual}ms (>= ${Threshold}ms)" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $Label`: expected >= ${Threshold}ms, got ${Actual}ms" -ForegroundColor Red
        $script:failures += "$Label`: expected >= ${Threshold}ms, got ${Actual}ms"
    }
}

function Install-Policy {
    # Install a chaos policy via POST /chaos/policies. Records the id so
    # Remove-AllPolicies can tear them all down at the end.
    param([hashtable]$Policy)
    $body = $Policy | ConvertTo-Json -Depth 10 -Compress
    $resp = Invoke-WebRequest -Method POST -Uri "$ProxyBaseUrl/chaos/policies" `
        -ContentType 'application/json' -Body $body -UseBasicParsing -TimeoutSec 10
    $parsed = $resp.Content | ConvertFrom-Json
    $script:installedPolicyIds += $parsed.id
    return $parsed.id
}

function Remove-AllPolicies {
    foreach ($id in $script:installedPolicyIds) {
        try {
            Invoke-WebRequest -Method DELETE -Uri "$ProxyBaseUrl/chaos/policies/$id" -UseBasicParsing -TimeoutSec 5 | Out-Null
        }
        catch {
            Write-Host "  WARN  teardown of policy '$id' failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

function Get-FireCounts {
    param([string]$PolicyId)
    try {
        $resp = Invoke-WebRequest -Uri "$ProxyBaseUrl/chaos/policies/$PolicyId/fire-counts" -UseBasicParsing -TimeoutSec 5
        return ($resp.Content | ConvertFrom-Json).fireCounts
    }
    catch {
        return $null
    }
}

function Invoke-Probe {
    param(
        [string]$Method,
        [string]$Path,
        [hashtable]$Headers = @{},
        [int]$TimeoutSec = 10
    )
    $url = "$ProxyBaseUrl$Path"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Method $Method -Uri $url -Headers $Headers `
            -TimeoutSec $TimeoutSec -SkipHttpErrorCheck -UseBasicParsing
        $sw.Stop()
        return @{ Status = [int]$response.StatusCode; Body = $response.Content; Headers = $response.Headers; DurationMs = $sw.ElapsedMilliseconds; Timeout = $false; Partial = $false }
    }
    catch [System.Net.Http.HttpRequestException], [System.IO.IOException] {
        # Partial-response transforms intentionally truncate the body below
        # the advertised Content-Length; HttpClient throws.
        $sw.Stop()
        return @{ Status = -1; Body = $null; Headers = @{}; DurationMs = $sw.ElapsedMilliseconds; Timeout = $false; Partial = $true; Error = $_.Exception.Message }
    }
    catch [System.Net.WebException], [System.Threading.Tasks.TaskCanceledException] {
        $sw.Stop()
        return @{ Status = -1; Body = $null; Headers = @{}; DurationMs = $sw.ElapsedMilliseconds; Timeout = $true; Partial = $false; Error = $_.Exception.Message }
    }
}

function Assert-PassThrough {
    # For transforms that decorate the request but let it reach upstream. Any
    # status > 0 means the chaos didn't short-circuit; upstream's exact response
    # is irrelevant (the proxy's job is to inject, not to mock upstream).
    param([string]$Label, $Probe)
    if ($Probe.Status -gt 0) {
        Write-Host "  PASS  $Label = passed through to upstream (status $($Probe.Status))" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $Label`: did not reach upstream (timeout=$($Probe.Timeout))" -ForegroundColor Red
        $script:failures += "$Label`: pass-through failed"
    }
}

function Assert-FireCount {
    param([string]$PolicyId, [int]$ExpectedMin = 1)
    $counts = Get-FireCounts -PolicyId $PolicyId
    if ($null -eq $counts) {
        Write-Host "  FAIL  $PolicyId`: fire-counts unreachable" -ForegroundColor Red
        $script:failures += "$PolicyId`: fire-counts unreachable"
        return
    }
    $total = 0
    foreach ($field in $counts.PSObject.Properties) { $total += [int]$field.Value }
    if ($total -ge $ExpectedMin) {
        Write-Host "  PASS  $PolicyId fired $total time(s) (>= $ExpectedMin)" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $PolicyId fire count $total < $ExpectedMin" -ForegroundColor Red
        $script:failures += "$PolicyId fire count $total < $ExpectedMin"
    }
}

# Use a per-run path prefix so concurrent runs don't collide on matchers and
# fresh runs don't see stale state on the proxy.
$runId = "smoke-$(Get-Random)"

# ----------------------------------------------------------------- start

Write-Host "=== Chaos proxy smoke test against $ProxyBaseUrl ===" -ForegroundColor Cyan
Write-Host "    run id: $runId" -ForegroundColor DarkGray
Write-Host ""

try {
    # --- Pre-flight: proxy reachable ---
    Write-Host "Pre-flight: GET /chaos/healthz" -ForegroundColor Cyan
    $health = Invoke-Probe -Method 'GET' -Path '/chaos/healthz'
    Assert-Eq "health status" 200 $health.Status
    if ($health.Status -ne 200) {
        Write-Host "Proxy not reachable - aborting smoke test." -ForegroundColor Red
        exit 1
    }

    # --- error: install -> probe -> expect 503 ---
    Write-Host ""
    Write-Host "Probe: error transform (install -> 503)" -ForegroundColor Cyan
    $errorId = Install-Policy @{
        id = "$runId-error"
        matcher = @{ pathPrefix = "/$runId/error/" }
        error = @{ status = 503; body = 'smoke ServerBusy'; probability = 1.0 }
        ttlSeconds = 60
    }
    $r = Invoke-Probe -Method 'GET' -Path "/$runId/error/x"
    Assert-Eq "status" 503 $r.Status
    Assert-FireCount $errorId 1

    # --- latency: install -> probe -> expect >=500ms ---
    Write-Host ""
    Write-Host "Probe: latency transform (install -> >=500ms)" -ForegroundColor Cyan
    $latencyId = Install-Policy @{
        id = "$runId-latency"
        matcher = @{ pathPrefix = "/$runId/latency/" }
        latency = @{ minMs = 500; maxMs = 800; probability = 1.0 }
        ttlSeconds = 60
    }
    $r = Invoke-Probe -Method 'GET' -Path "/$runId/latency/x" -TimeoutSec 5
    Assert-Ge "duration" 500 $r.DurationMs
    Assert-PassThrough "latency reached upstream" $r
    Assert-FireCount $latencyId 1

    # --- drop-response: install -> probe -> expect client timeout ---
    Write-Host ""
    Write-Host "Probe: drop-response transform (install -> client timeout)" -ForegroundColor Cyan
    $dropId = Install-Policy @{
        id = "$runId-drop"
        matcher = @{ pathPrefix = "/$runId/drop/" }
        dropResponse = @{ probability = 1.0 }
        ttlSeconds = 60
    }
    $r = Invoke-Probe -Method 'GET' -Path "/$runId/drop/x" -TimeoutSec 2
    if ($r.Timeout) {
        Write-Host "  PASS  client timeout as expected (response dropped)" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  expected timeout but got status $($r.Status)" -ForegroundColor Red
        $failures += "drop: expected timeout, got status $($r.Status)"
    }
    Assert-FireCount $dropId 1

    # --- rate-limit: install -> 3 probes -> expect 200, 200, 429 ---
    Write-Host ""
    Write-Host "Probe: rate-limit transform (install -> 3 calls, expect pass, pass, 429)" -ForegroundColor Cyan
    $rateId = Install-Policy @{
        id = "$runId-rate"
        matcher = @{ pathPrefix = "/$runId/rate/" }
        rateLimit = @{ requestsPerWindow = 2; windowMs = 10000; status = 429; headers = @{ 'Retry-After' = '10' } }
        ttlSeconds = 60
    }
    $first  = Invoke-Probe -Method 'GET' -Path "/$runId/rate/x"
    $second = Invoke-Probe -Method 'GET' -Path "/$runId/rate/x"
    $third  = Invoke-Probe -Method 'GET' -Path "/$runId/rate/x"
    Assert-PassThrough "request 1 passed through" $first
    Assert-PassThrough "request 2 passed through" $second
    Assert-Eq "request 3 status (blocked)" 429 $third.Status
    Assert-FireCount $rateId 1

    # --- header-tamper: install -> probe -> expect 200 round-trip ---
    Write-Host ""
    Write-Host "Probe: header-tamper transform (strips Authorization, sets X-Chaos-Injected)" -ForegroundColor Cyan
    $headerId = Install-Policy @{
        id = "$runId-headers"
        matcher = @{ pathPrefix = "/$runId/headers/" }
        headerTamper = @{ direction = 'Request'; remove = @('Authorization'); set = @{ 'X-Chaos-Injected' = 'true' } }
        ttlSeconds = 60
    }
    $r = Invoke-Probe -Method 'GET' -Path "/$runId/headers/x" -Headers @{ Authorization = 'Bearer dummy' }
    Assert-PassThrough "header-tamper round-trip" $r
    Assert-FireCount $headerId 1

    # --- partial-response: install -> probe -> expect Content-Length mismatch ---
    Write-Host ""
    Write-Host "Probe: partial-response transform (install -> body shorter than Content-Length)" -ForegroundColor Cyan
    $partialId = Install-Policy @{
        id = "$runId-partial"
        matcher = @{ pathPrefix = "/$runId/partial/" }
        partialResponse = @{
            status = 200
            contentType = 'application/json'
            body = '{"data":["truncated'
            advertisedContentLength = 5000
            abortAfterMs = 50
            probability = 1.0
        }
        ttlSeconds = 60
    }
    $r = Invoke-Probe -Method 'GET' -Path "/$runId/partial/x" -TimeoutSec 5
    if ($r.Partial) {
        Write-Host "  PASS  response ended prematurely (HttpClient detected Content-Length mismatch)" -ForegroundColor Green
    }
    elseif ($r.Status -eq 200 -and $r.Body -like '*truncated*') {
        Write-Host "  PASS  status 200 + truncated marker (PowerShell tolerated short read)" -ForegroundColor Green
    }
    else {
        $preview = if ($r.Body) { $r.Body.Substring(0, [Math]::Min(80, $r.Body.Length)) } else { '<empty>' }
        Write-Host "  FAIL  expected partial response (got status=$($r.Status), body=$preview)" -ForegroundColor Red
        $failures += "partial: no truncation signal"
    }
    Assert-FireCount $partialId 1

    # --- idempotency-collision: install -> 2 calls same key -> expect upstream then 409 ---
    Write-Host ""
    Write-Host "Probe: idempotency-collision transform (install -> 2 same-key POSTs, expect upstream then 409)" -ForegroundColor Cyan
    $idemId = Install-Policy @{
        id = "$runId-idem"
        matcher = @{ pathPrefix = "/$runId/idem/" }
        idempotencyCollision = @{
            keyHeaderName = 'Idempotency-Key'
            status = 409
            body = 'duplicate request'
            contentType = 'text/plain'
            windowMs = 60000
        }
        ttlSeconds = 60
    }
    $idemKey = "$runId-key"
    $first  = Invoke-Probe -Method 'POST' -Path "/$runId/idem/x" -Headers @{ 'Idempotency-Key' = $idemKey }
    $second = Invoke-Probe -Method 'POST' -Path "/$runId/idem/x" -Headers @{ 'Idempotency-Key' = $idemKey }
    Assert-PassThrough "request 1 reached upstream" $first
    Assert-Eq "request 2 status (collision)" 409 $second.Status
    Assert-FireCount $idemId 1

    # --- slow-response: install -> probe -> expect streaming duration ---
    Write-Host ""
    Write-Host "Probe: slow-response transform (200 bytes @ 500 bytes/sec -> >=300ms)" -ForegroundColor Cyan
    $slowStreamId = Install-Policy @{
        id = "$runId-slow-stream"
        matcher = @{ pathPrefix = "/$runId/slow-stream/" }
        slowResponse = @{
            status = 200
            contentType = 'text/plain'
            body = ('X' * 200)
            bytesPerSecond = 500
            chunkSize = 20
            probability = 1.0
        }
        ttlSeconds = 60
    }
    $r = Invoke-Probe -Method 'GET' -Path "/$runId/slow-stream/x" -TimeoutSec 5
    Assert-Ge "stream duration" 300 $r.DurationMs
    Assert-Eq "slow-stream status" 200 $r.Status
    Assert-FireCount $slowStreamId 1

    # --- replay-duplicate: install -> single probe -> expect fire counter shows replay ---
    Write-Host ""
    Write-Host "Probe: replay-duplicate transform (single probe -> background fan-out)" -ForegroundColor Cyan
    $replayId = Install-Policy @{
        id = "$runId-replay"
        matcher = @{ pathPrefix = "/$runId/replay/" }
        replayDuplicate = @{ probability = 1.0 }
        ttlSeconds = 60
    }
    $r = Invoke-Probe -Method 'GET' -Path "/$runId/replay/x"
    Assert-PassThrough "replay-duplicate (original request still flows)" $r
    Start-Sleep -Seconds 2  # background replay needs a moment
    Assert-FireCount $replayId 1
}
finally {
    Write-Host ""
    Write-Host "Teardown: removing $($script:installedPolicyIds.Count) installed policies" -ForegroundColor Cyan
    Remove-AllPolicies
}

# --- Result ---
Write-Host ""
Write-Host "=== Smoke test complete ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "All assertions passed." -ForegroundColor Green
    exit 0
}
else {
    Write-Host "$($failures.Count) failure(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
