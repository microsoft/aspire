param(
    [Parameter(Mandatory = $true)]
    [string] $TerminalHostPath
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Directory]::CreateTempSubdirectory('ath-')
$process = [System.Diagnostics.Process]::new()
$socket = $null
$stream = $null
$started = $false
try {
    $paths = @('p.sock', 'h.sock', 'c.sock') | ForEach-Object { Join-Path $root.FullName $_ }
    $process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $TerminalHostPath).Path)
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in @('--producer-uds', $paths[0], '--consumer-uds', $paths[1], '--control-uds', $paths[2])) {
        $process.StartInfo.ArgumentList.Add($argument)
    }
    $process.StartInfo.Environment.Remove('ASPIRE_TERMINAL_HOST_PARENT_PID') | Out-Null
    $process.StartInfo.Environment.Remove('ASPIRE_TERMINAL_HOST_PARENT_STARTED_STABLE') | Out-Null
    $process.StartInfo.Environment['ASPIRE_BUNDLE_VERSION_DIR'] = $root.FullName
    $process.StartInfo.Environment['ASPIRE_TERMINAL_HOST_TELEMETRY_ENABLED'] = 'false'
    if (!$process.Start()) { throw 'Failed to launch native TerminalHost.' }
    $started = $true
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (@($paths | Where-Object { ![System.IO.File]::Exists($_) }).Count -ne 0) {
        if ($process.HasExited) { throw "Native TerminalHost exited with $($process.ExitCode): $($stderr.GetAwaiter().GetResult())" }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Native TerminalHost did not bind its sockets.' }
        Start-Sleep -Milliseconds 50
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $root.FullName '.leases') -Filter '*.lease').Count -ne 1) {
        throw 'Native TerminalHost did not acquire its bundle lease.'
    }

    $socket = [System.Net.Sockets.Socket]::new(
        [System.Net.Sockets.AddressFamily]::Unix,
        [System.Net.Sockets.SocketType]::Stream,
        [System.Net.Sockets.ProtocolType]::Unspecified)
    $socket.Connect([System.Net.Sockets.UnixDomainSocketEndPoint]::new($paths[2]))
    $stream = [System.Net.Sockets.NetworkStream]::new($socket)
    $stream.ReadTimeout = 10000
    $stream.WriteTimeout = 10000

    function Send-Rpc([string] $method, [int] $id) {
        # StreamJsonRpc's control socket uses: Content-Length: <UTF-8 byte count>\r\n\r\n<JSON>.
        $body = [System.Text.Encoding]::UTF8.GetBytes((@{ jsonrpc = '2.0'; id = $id; method = $method } | ConvertTo-Json -Compress))
        $header = [System.Text.Encoding]::ASCII.GetBytes("Content-Length: $($body.Length)`r`n`r`n")
        $stream.Write($header)
        $stream.Write($body)
    }

    function Read-Rpc {
        $header = ''
        while (!$header.EndsWith("`r`n`r`n")) {
            $value = $stream.ReadByte()
            if ($value -lt 0) { throw 'Control RPC closed before its response header.' }
            $header += [char] $value
            if ($header.Length -gt 4096) { throw 'Control RPC response header is too large.' }
        }
        if ($header -notmatch '(?i)Content-Length: (\d+)') { throw "Invalid control RPC header: $header" }
        $length = [int] $Matches[1]
        if ($length -gt 1048576) { throw 'Control RPC response is too large.' }
        $body = [byte[]]::new($length)
        $offset = 0
        while ($offset -lt $length) {
            $read = $stream.Read($body, $offset, $length - $offset)
            if ($read -eq 0) { throw 'Control RPC closed before its response body.' }
            $offset += $read
        }
        return [System.Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
    }

    Send-Rpc 'getInfo' 1
    $info = Read-Rpc
    if ($info.id -ne 1 -or $info.result.protocolVersion -ne 2 -or $null -ne $info.error) {
        throw "Unexpected native control RPC response: $($info | ConvertTo-Json -Depth 10)"
    }
    Send-Rpc 'getSession' 2
    $session = Read-Rpc
    if ($session.id -ne 2 -or $null -ne $session.error -or $session.result.producerUdsPath -ne $paths[0]) {
        throw "Unexpected native session response: $($session | ConvertTo-Json -Depth 10)"
    }
    Send-Rpc 'shutdown' 3
    if (!$process.WaitForExit(10000)) { throw 'Native TerminalHost did not honor control shutdown.' }
    if ($process.ExitCode -ne 0) { throw "Native TerminalHost failed: $($stderr.GetAwaiter().GetResult())" }
    foreach ($path in $paths) {
        if ([System.IO.File]::Exists($path)) { throw "Native TerminalHost left a socket behind: $path" }
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $root.FullName '.leases') -Filter '*.lease').Count -ne 0) {
        throw 'Native TerminalHost left an active bundle lease.'
    }
    Write-Host 'Native TerminalHost control RPC, shutdown, socket cleanup, and bundle lease passed.'
}
finally {
    if ($null -ne $stream) { $stream.Dispose() }
    if ($null -ne $socket) { $socket.Dispose() }
    if ($started -and !$process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $process.Dispose()
    $root.Delete($true)
}
