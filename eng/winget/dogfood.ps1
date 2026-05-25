<#
.SYNOPSIS
    Installs the Aspire CLI from local WinGet manifest files for dogfooding.

.DESCRIPTION
    This script installs (or uninstalls) the Aspire CLI using local WinGet manifest files,
    allowing you to test builds before they are published to microsoft/winget-pkgs.

.PARAMETER ManifestPath
    Path to the directory containing the WinGet manifest YAML files.
    Defaults to auto-detecting the manifest directory relative to this script.

.PARAMETER ArchiveRoot
    Root directory containing downloaded aspire-cli-win-* archive artifacts. When present, the
    local manifest is rewritten to install from those archive files instead of ci.dot.net URLs.

.PARAMETER Uninstall
    Uninstall a previously dogfooded Aspire CLI.

.PARAMETER Force
    Allow replacing an existing Microsoft.Aspire WinGet installation.

.EXAMPLE
    .\dogfood.ps1
    # Auto-detects manifests in the script directory and installs

.EXAMPLE
    .\dogfood.ps1 -ManifestPath .\manifests\m\Microsoft\Aspire\9.2.0
    # Install from a specific manifest directory

.EXAMPLE
    .\dogfood.ps1 -ArchiveRoot ..\native-archives
    # Install using downloaded native archive artifacts

.EXAMPLE
    .\dogfood.ps1 -Uninstall
    # Uninstall the dogfooded Aspire CLI
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$ManifestPath,

    [string]$ArchiveRoot,

    [switch]$Uninstall,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-InstallerManifestPath {
    param([string]$Path)

    $installerManifests = @(Get-ChildItem -Path $Path -File -Filter "*.installer.yaml")
    if ($installerManifests.Count -ne 1) {
        Write-Error "Expected exactly one *.installer.yaml manifest under $Path, but found $($installerManifests.Count)."
        exit 1
    }

    return $installerManifests[0].FullName
}

function Get-ManifestVersion {
    param([string]$ManifestPath)

    foreach ($line in Get-Content -Path $ManifestPath) {
        if ($line -match '^\s*PackageVersion:\s*"?([^"]+)"?\s*$') {
            return $Matches[1]
        }
    }

    Write-Error "Could not read PackageVersion from $ManifestPath."
    exit 1
}

function Find-ArchiveIfPresent {
    param(
        [string]$Root,
        [string]$ArchiveName
    )

    if (-not (Test-Path $Root)) {
        return $null
    }

    return Get-ChildItem -Path $Root -File -Recurse -Filter $ArchiveName -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

function Find-Archive {
    param(
        [string]$Root,
        [string]$ArchiveName
    )

    $matchedItems = @(Get-ChildItem -Path $Root -File -Recurse -Filter $ArchiveName -ErrorAction SilentlyContinue | Sort-Object FullName)
    if ($matchedItems.Count -eq 0) {
        Write-Error "Could not find $ArchiveName under $Root."
        exit 1
    }

    if ($matchedItems.Count -gt 1) {
        $matchList = $matchedItems | ForEach-Object { "  $($_.FullName)" }
        Write-Error "Found multiple $ArchiveName archives under ${Root}:`n$($matchList -join "`n")"
        exit 1
    }

    return $matchedItems[0].FullName
}

function Get-DefaultArchiveRoot {
    param([string]$Version)

    foreach ($candidate in @($ScriptDir, (Split-Path -Parent $ScriptDir))) {
        if ((Find-ArchiveIfPresent -Root $candidate -ArchiveName "aspire-cli-win-x64-$Version.zip") -and
            (Find-ArchiveIfPresent -Root $candidate -ArchiveName "aspire-cli-win-arm64-$Version.zip")) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Start-LocalArchiveServer {
    param(
        # Map of <archive filename> → <absolute path on disk>. Only these names are
        # serveable; anything else returns 404.
        [hashtable]$FileMap
    )

    # WinGet downloads InstallerUrl payloads via WinINet's InternetOpenUrl(), which
    # only supports http/https/ftp — not file://. So we serve the local archives over
    # a loopback HTTP listener instead of rewriting InstallerUrl to file:///, which
    # used to fail at install time with:
    #   "InternetOpenUrl() failed. 0x8007007b : The filename, directory name, or
    #    volume label syntax is incorrect."
    #
    # HttpListener on a loopback prefix does NOT require admin / netsh urlacl
    # registration for the current user — that restriction only applies to non-loopback
    # bindings. See:
    # https://learn.microsoft.com/dotnet/api/system.net.httplistener
    #
    # Get-FreeLoopbackPort picks a port by reserving and immediately releasing a
    # TcpListener, so between that release and HttpListener.Start() below the
    # kernel could hand the port to another process on a busy CI runner. Retry
    # on HttpListenerException with a fresh port; only fail after several misses.
    $listener = $null
    $prefix = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $port = (Get-FreeLoopbackPort)
        $prefix = "http://127.0.0.1:$port/"
        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add($prefix)
        try {
            $listener.Start()
            break
        } catch [System.Net.HttpListenerException] {
            $listener.Close()
            $listener = $null
            if ($attempt -eq 5) {
                throw
            }
        }
    }

    # Use a runspace (not Start-Job/Start-ThreadJob) so we don't pull in the Jobs or
    # ThreadJob modules, which aren't guaranteed to be available on every winget host.
    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $runspace
    [void]$ps.AddScript({
        param($Listener, $FileMap)
        while ($Listener.IsListening) {
            try {
                $context = $Listener.GetContext()
            } catch {
                break
            }

            try {
                $requestedName = [System.IO.Path]::GetFileName([System.Uri]::UnescapeDataString($context.Request.Url.AbsolutePath))
                if ($FileMap.ContainsKey($requestedName)) {
                    $filePath = $FileMap[$requestedName]
                    $bytes = [System.IO.File]::ReadAllBytes($filePath)
                    $context.Response.ContentType = 'application/octet-stream'
                    $context.Response.ContentLength64 = $bytes.LongLength
                    $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                } else {
                    $context.Response.StatusCode = 404
                }
            } catch {
                try { $context.Response.StatusCode = 500 } catch { }
            } finally {
                try { $context.Response.Close() } catch { }
            }
        }
    }).AddArgument($listener).AddArgument($FileMap)

    $asyncResult = $ps.BeginInvoke()

    return [pscustomobject]@{
        Listener    = $listener
        BaseUri     = $prefix.TrimEnd('/')
        PowerShell  = $ps
        Runspace    = $runspace
        AsyncResult = $asyncResult
    }
}

function Stop-LocalArchiveServer {
    param($Server)

    if (-not $Server) { return }

    try {
        if ($Server.Listener -and $Server.Listener.IsListening) {
            $Server.Listener.Stop()
        }
        if ($Server.Listener) {
            $Server.Listener.Close()
        }
    } catch { }

    try {
        if ($Server.PowerShell -and $Server.AsyncResult) {
            [void]$Server.PowerShell.EndInvoke($Server.AsyncResult)
        }
    } catch { }

    try { if ($Server.PowerShell) { $Server.PowerShell.Dispose() } } catch { }
    try { if ($Server.Runspace)   { $Server.Runspace.Dispose() } } catch { }
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

function Show-LatestWinGetDiagLog {
    param([string]$Reason = '')

    # Real winget writes per-invocation diag logs to
    # %LOCALAPPDATA%\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\DiagOutputDir
    # When `winget install --manifest` exits non-zero, the diag log is almost always
    # the only place that says *why* (the console output is often truncated or empty,
    # and `winget` exit codes like 0x8A150001 are generic). Print the newest log to
    # the host console (Write-Host, matching the rest of this script) so dogfood
    # failures are diagnosable without separately hunting it down. In CI runs the
    # host output is captured into the job log, so this lands in the same place as
    # the surrounding install output.
    #
    # Tests set ASPIRE_TEST_MODE=true and run a mock winget that doesn't produce diag
    # logs, so skip this in test mode to avoid emitting unrelated stale logs.
    if ($env:ASPIRE_TEST_MODE -eq 'true') {
        return
    }

    $diagDir = Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\DiagOutputDir'
    if (-not (Test-Path -LiteralPath $diagDir)) {
        Write-Host "(no winget diag dir at $diagDir)"
        return
    }

    $log = Get-ChildItem -LiteralPath $diagDir -Filter 'WinGet-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $log) {
        Write-Host "(no WinGet-*.log files in $diagDir)"
        return
    }

    Write-Host ""
    Write-Host "=== winget diag log ($Reason) ==="
    Write-Host "  $($log.FullName)"
    Write-Host ""
    Get-Content -LiteralPath $log.FullName | ForEach-Object { Write-Host "  $_" }
    Write-Host "=== end winget diag log ==="
    Write-Host ""
}

function Find-AspireBinaryOnPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ExpectedVersion
    )

    # Walks the freshly-refreshed PATH (machine+user from the registry, which
    # picks up the dir winget added during portable install) for every aspire.exe
    # and runs it. Returns the FIRST one whose --version output matches
    # $ExpectedVersion. If none match, returns the first aspire.exe found so the
    # caller can warn about PATH shadowing without losing the diagnostic. Returns
    # $null if no aspire.exe is on PATH at all.
    #
    # This serves two callers:
    #
    #  - Post-install fallback: some Windows environments (corp-managed machines
    #    hitting winget bug https://github.com/microsoft/winget-cli/issues/6230)
    #    cause `winget install --manifest` to return -1978335231 (0x8A150001)
    #    even when the install completed end-to-end. A successful version match
    #    here means the binary is really deployed despite winget's exit code.
    #    Don't fall back to `winget list` for this — winget's installed-package
    #    store keeps stale entries from prior partial installs even after a
    #    rollback, so it returns false positives.
    #
    #  - Post-install verification: an older aspire.exe earlier on PATH (e.g.
    #    from a previous get-aspire-cli install) would shadow the winget-installed
    #    binary if we just used Get-Command. Walking PATH for a version match
    #    finds the freshly-installed binary even when it isn't first.
    #
    # Returns: $null OR [pscustomobject]@{
    #   Path                  = full path to aspire.exe (or .cmd in test mode)
    #   Version               = trimmed --version output
    #   ExitCode              = aspire --version exit code
    #   ExpectedVersionMatched= $true iff Version contains $ExpectedVersion
    # }
    #
    # In test mode the mock aspire.cmd's --version output ("mock aspire version")
    # does not match any real PackageVersion. Fall back to Get-Command so the
    # test-mode verification assertions still fire on the mock.
    if ($env:ASPIRE_TEST_MODE -eq 'true') {
        $cmd = Get-Command aspire -ErrorAction SilentlyContinue
        if (-not $cmd) { return $null }
        $output = & $cmd.Source --version 2>&1
        $exitCode = $LASTEXITCODE
        $versionString = ($output | Out-String).Trim()
        return [pscustomobject]@{
            Path                   = $cmd.Source
            Version                = $versionString
            ExitCode               = $exitCode
            ExpectedVersionMatched = $false
        }
    }

    $newPath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
               [System.Environment]::GetEnvironmentVariable('Path', 'User')
    $firstFound = $null
    foreach ($dir in ($newPath -split ';' | Where-Object { $_ })) {
        $candidate = Join-Path $dir 'aspire.exe'
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        try {
            $output = & $candidate --version 2>&1
            $exitCode = $LASTEXITCODE
        } catch {
            continue
        }
        $versionString = ($output | Out-String).Trim()
        $info = [pscustomobject]@{
            Path                   = $candidate
            Version                = $versionString
            ExitCode               = $exitCode
            ExpectedVersionMatched = ($versionString -match [regex]::Escape($ExpectedVersion))
        }
        if ($info.ExpectedVersionMatched) {
            return $info
        }
        if (-not $firstFound) {
            $firstFound = $info
        }
    }
    return $firstFound
}

function Test-WinGetVersionForMotwFix {
    [CmdletBinding()]
    param()

    # winget-cli bug https://github.com/microsoft/winget-cli/issues/6230: portable
    # installs exit -1978335231 (0x8A150001) at the post-install IAttachmentExecute
    # Mark-of-the-Web step on some machines, even when the binary is deployed end-to-end.
    # Fixed in v1.29.140-preview by https://github.com/microsoft/winget-cli/pull/6127;
    # last broken stable release is v1.28.240 (2026-04-17).
    #
    # The failure is environmental (interaction with AV / IOfficeAntiVirus) and not every
    # < 1.29.140 install actually trips it, so this is a soft warning, not a hard gate.
    # Find-AspireBinaryOnPath in the post-install fallback already recovers from the
    # case where winget exits non-zero but the binary IS deployed.
    if ($env:ASPIRE_TEST_MODE -eq 'true') {
        return
    }

    try {
        $versionOutput = (winget --version 2>&1) | Out-String
    } catch {
        return
    }
    $trimmed = $versionOutput.Trim()
    # winget --version output: "v1.29.170-preview" or "v1.28.240"
    if ($trimmed -notmatch '^v(\d+\.\d+\.\d+)') {
        return
    }
    $parsed = $null
    if (-not [Version]::TryParse($Matches[1], [ref]$parsed)) {
        return
    }
    if ($parsed -lt [Version]'1.29.140') {
        Write-Warning @"
Local winget is $trimmed. Versions older than v1.29.140-preview can fail portable
installs with exit code -1978335231 (0x8A150001) at the post-install Mark-of-the-Web
step even when the binary is fully deployed. See:
  https://github.com/microsoft/winget-cli/issues/6230

If the install below fails with that exit code, upgrade winget to v1.29.140-preview
or newer. Pre-release builds are available at:
  https://github.com/microsoft/winget-cli/releases
"@
    }
}

function Resolve-ArchiveFileMap {
    param(
        [string]$ResolvedArchiveRoot,
        [string]$Version
    )

    # The PR-channel CI artifact extracts under <archive-root>/Debug/Shipping/, not
    # the root of the artifact directory. Find-Archive scans recursively to locate
    # the actual on-disk path. This returns:
    #   filename → absolute path  (e.g. "aspire-cli-win-x64-13.4.0-pr.X.gSHA.zip"
    #                              → "C:\...\Debug\Shipping\aspire-cli-win-x64-13.4.0-pr.X.gSHA.zip")
    $archives = @{}
    foreach ($arch in @("x64", "arm64")) {
        $archiveName = "aspire-cli-win-$arch-$Version.zip"
        $archives[$archiveName] = Find-Archive -Root $ResolvedArchiveRoot -ArchiveName $archiveName
    }
    return $archives
}

function Set-LocalInstallerSources {
    param(
        [string]$InstallerManifestPath,
        [hashtable]$ArchiveFileMap,
        [string]$BaseUri
    )

    # winget install hashes the downloaded payload and compares it with the recorded
    # InstallerSha256, so we have to refresh the hash whenever we redirect InstallerUrl
    # at a local archive. PR-channel manifests in particular ship with the placeholder
    # ("0" * 64) baked in by generate-manifests.ps1's -SkipUrlValidation path, and that
    # hash never matches a real build.
    $sha256ByArchitecture = @{}
    $urlByArchitecture = @{}
    foreach ($name in $ArchiveFileMap.Keys) {
        $archivePath = $ArchiveFileMap[$name]
        $arch = if ($name -match 'aspire-cli-win-(x64|arm64)-') { $Matches[1] } else { continue }
        $sha256ByArchitecture[$arch] = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
        $urlByArchitecture[$arch] = "$BaseUri/$name"
    }

    $currentArchitecture = $null
    $updatedLines = foreach ($line in Get-Content -Path $InstallerManifestPath) {
        if ($line -match '^\s*-\s*Architecture:\s*(\S+)\s*$') {
            $currentArchitecture = $Matches[1]
            $line
            continue
        }

        if ($line -match '^(\s*)InstallerUrl:\s*' -and $currentArchitecture -and $urlByArchitecture.ContainsKey($currentArchitecture)) {
            "$($Matches[1])InstallerUrl: $($urlByArchitecture[$currentArchitecture])"
            continue
        }

        if ($line -match '^(\s*)InstallerSha256:\s*' -and $currentArchitecture -and $sha256ByArchitecture.ContainsKey($currentArchitecture)) {
            "$($Matches[1])InstallerSha256: $($sha256ByArchitecture[$currentArchitecture])"
            continue
        }

        $line
    }

    Set-Content -Path $InstallerManifestPath -Value $updatedLines
}

if ($Uninstall) {
    Write-Host "Uninstalling dogfooded Aspire CLI..."
    Write-Host ""

    # Look for the stable Aspire package in the local installation.
    $packages = @("Microsoft.Aspire")
    foreach ($pkg in $packages) {
        Write-Host "Checking for $pkg..."
        $result = winget list --id $pkg --accept-source-agreements 2>&1
        if ($LASTEXITCODE -eq 0 -and $result -match $pkg) {
            Write-Host "  Found $pkg, uninstalling..."
            winget uninstall --id $pkg --accept-source-agreements
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Uninstalled $pkg."
            } else {
                Write-Warning "  Failed to uninstall $pkg (exit code: $LASTEXITCODE)"
            }
        }
    }

    Write-Host ""
    Write-Host "Done."
    exit 0
}

# Auto-detect manifest path if not specified
if (-not $ManifestPath) {
    if (Get-ChildItem -Path $ScriptDir -File -Filter "*.installer.yaml" | Select-Object -First 1) {
        $ManifestPath = $ScriptDir
    } else {
        # Look for versioned manifest directories under the script directory.
        # Convention: manifests/m/Microsoft/Aspire/{Version}/
        $candidates = Get-ChildItem -Path $ScriptDir -Directory -Recurse -Depth 6 |
            Where-Object {
                Test-Path (Join-Path $_.FullName "*.installer.yaml")
            } |
            Select-Object -First 1

        if ($candidates) {
            $ManifestPath = $candidates.FullName
        } else {
            Write-Error "No manifest directory found under $ScriptDir. Specify -ManifestPath explicitly."
            exit 1
        }
    }
}

if (-not (Test-Path $ManifestPath)) {
    Write-Error "Manifest path not found: $ManifestPath"
    exit 1
}

# Verify it contains manifest files
$manifestFiles = Get-ChildItem -Path $ManifestPath -Filter "*.yaml"
if ($manifestFiles.Count -eq 0) {
    Write-Error "No .yaml manifest files found in: $ManifestPath"
    exit 1
}

Write-Host "Aspire CLI WinGet Dogfood Installer"
Write-Host "====================================="
Write-Host "  Manifest path: $ManifestPath"
Write-Host "  Manifest files:"
foreach ($f in $manifestFiles) {
    Write-Host "    - $($f.Name)"
}
Write-Host ""

Test-WinGetVersionForMotwFix

if (-not $Force) {
    $existingInstall = winget list --id Microsoft.Aspire --accept-source-agreements 2>&1
    if ($LASTEXITCODE -eq 0 -and $existingInstall -match "Microsoft\.Aspire") {
        Write-Error "Microsoft.Aspire is already installed. Uninstall it first, or rerun with -Force to replace it with the dogfood manifest."
        exit 1
    }
}

# Stage the .yaml manifest files into a clean temp directory before calling winget.
# `winget validate --manifest <dir>` and `winget install --manifest <dir>` treat every
# file in the directory as a multi-file manifest. The CI artifact ships dogfood.ps1
# alongside the yaml files, so pointing winget at the artifact directly fails with
# "The manifest does not contain a valid root. File: dogfood.ps1". Staging also keeps
# Set-LocalInstallerSources's rewrites out of the user's artifact so reruns are idempotent.
$stagedManifestDir = Join-Path ([System.IO.Path]::GetTempPath()) ("aspire-winget-manifest-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagedManifestDir -Force | Out-Null

$archiveServer = $null
try {
    foreach ($f in $manifestFiles) {
        Copy-Item -Path $f.FullName -Destination (Join-Path $stagedManifestDir $f.Name) -Force
    }

    $installerManifestPath = Get-InstallerManifestPath -Path $stagedManifestDir
    $version = Get-ManifestVersion -ManifestPath $installerManifestPath

    if (-not $ArchiveRoot) {
        $ArchiveRoot = Get-DefaultArchiveRoot -Version $version
    }

    if ($ArchiveRoot) {
        $ArchiveRoot = (Resolve-Path $ArchiveRoot).Path
        Write-Host "Using local native archive artifacts from: $ArchiveRoot"
    } else {
        Write-Host "No local native archive artifacts found; installing with URLs from the manifests."
    }
    Write-Host ""

    # Enable local manifest files
    Write-Host "Enabling local manifest files in winget settings..."
    winget settings --enable LocalManifestFiles
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to enable local manifests. You may need to run this as Administrator."
    }

    # Validate the manifest BEFORE we rewrite InstallerUrl/InstallerSha256. winget's
    # schema requires InstallerUrl to match ^https?:// (so any rewritten URL — even an
    # http://127.0.0.1/... one — passes validation, but file:// does not). Validating
    # the pristine yaml first catches manifest authoring problems before we mutate it.
    Write-Host ""
    Write-Host "Validating manifests..."
    winget validate --manifest $stagedManifestDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Manifest validation failed. Fix the manifests and try again."
        exit $LASTEXITCODE
    }
    Write-Host "Validation passed."

    if ($ArchiveRoot) {
        # winget's downloader is WinINet (InternetOpenUrl), which only supports
        # http/https/ftp/gopher — not file://. Serve the local archives over a loopback
        # HTTP listener and rewrite InstallerUrl to point at it.
        #
        # The PR-channel artifact is laid out as <ArchiveRoot>/Debug/Shipping/*.zip,
        # so we resolve archive paths via Find-Archive (recursive) and serve the same
        # map from the listener — otherwise the listener would 404 on the manifest's
        # rewritten URL because the file isn't at the root of $ArchiveRoot.
        $archiveFileMap = Resolve-ArchiveFileMap -ResolvedArchiveRoot $ArchiveRoot -Version $version
        Write-Host ""
        Write-Host "Starting local archive server..."
        $archiveServer = Start-LocalArchiveServer -FileMap $archiveFileMap
        Write-Host "  Serving $($archiveFileMap.Count) archive(s) at $($archiveServer.BaseUri)"
        foreach ($name in $archiveFileMap.Keys) {
            Write-Host "    $name -> $($archiveFileMap[$name])"
        }
        Set-LocalInstallerSources -InstallerManifestPath $installerManifestPath -ArchiveFileMap $archiveFileMap -BaseUri $archiveServer.BaseUri
    }

    # Install
    Write-Host ""
    Write-Host "Installing Aspire CLI from local manifest..."
    $installArgs = @("install", "--manifest", $stagedManifestDir, "--accept-package-agreements", "--accept-source-agreements")
    if ($Force) {
        $installArgs += "--force"
    }

    winget @installArgs
    $wingetExitCode = $LASTEXITCODE

    if ($wingetExitCode -eq 0) {
        Write-Host ""
        Write-Host "winget install succeeded."
    }
    else {
        $installInfo = Find-AspireBinaryOnPath -ExpectedVersion $version
        if ($installInfo -and $installInfo.ExpectedVersionMatched) {
            # winget exited non-zero but an aspire.exe on PATH reports the expected
            # version. See Find-AspireBinaryOnPath for context — this surfaces on
            # machines hitting winget bug
            # https://github.com/microsoft/winget-cli/issues/6230 (the post-install
            # Mark-of-the-Web step in winget's DownloadFlow aborts even though the
            # install completed). Fixed in v1.29.140-preview+.
            Write-Host ""
            Write-Warning @"
winget reported failure (exit code $wingetExitCode) but aspire $version is installed at:
  $($installInfo.Path)
This is winget-cli bug https://github.com/microsoft/winget-cli/issues/6230 (post-install
Mark-of-the-Web step fails on some machines, fixed in winget v1.29.140-preview+). The CLI
itself is deployed.

To avoid this on future installs, upgrade winget. Pre-release builds are at:
  https://github.com/microsoft/winget-cli/releases

Verify with:
    aspire --version
(You may need to open a new shell to pick up the updated PATH.)
"@
        }
        else {
            Show-LatestWinGetDiagLog -Reason "winget exit code $wingetExitCode"
            Write-Error "Installation failed with exit code $wingetExitCode"
            exit $wingetExitCode
        }
    }
}
finally {
    Stop-LocalArchiveServer -Server $archiveServer
    Remove-Item -Path $stagedManifestDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Refresh PATH so the verification subprocess can see machine/user changes that
# winget made during install (the parent process's environment block is a snapshot
# from before install). Tests set ASPIRE_TEST_MODE=true so the mock winget bin
# already on PATH isn't replaced by the machine/user PATH from the registry.
if ($env:ASPIRE_TEST_MODE -ne 'true') {
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
}

# Verify by walking PATH for the just-installed binary (matched by version), so
# an older aspire.exe earlier on PATH can't masquerade as the freshly-installed
# one. See Find-AspireBinaryOnPath for the matching strategy.
Write-Host ""
Write-Host "Verifying installation..."
$verifyInfo = Find-AspireBinaryOnPath -ExpectedVersion $version

if (-not $verifyInfo) {
    Write-Host ""
    Write-Error "Failed to verify Aspire CLI installation: aspire not found in PATH."
    exit 1
}

Write-Host "  Path:    $($verifyInfo.Path)"
Write-Host "  Version: $($verifyInfo.Version)"

if ($verifyInfo.ExitCode -ne 0) {
    Write-Host ""
    Write-Error "Failed to verify Aspire CLI installation: 'aspire --version' exited with code $($verifyInfo.ExitCode)."
    exit $verifyInfo.ExitCode
}

if ($env:ASPIRE_TEST_MODE -ne 'true' -and -not $verifyInfo.ExpectedVersionMatched) {
    # Treat shadowing as a hard failure rather than a warning: the script's whole
    # purpose is to validate that the freshly-built manifest is the one users will
    # run. Reporting "Installed successfully!" while an older aspire.exe is
    # masquerading on PATH would silently green-light broken dogfood runs in CI.
    Write-Error @"
Reported version does not match the just-installed manifest version ($version).
An older aspire.exe at the path above is shadowing the winget-installed binary on PATH.
Either reorder PATH so the winget-installed location wins, or uninstall the older copy.
"@
    exit 1
}

Write-Host ""
Write-Host "Installed successfully!"

Write-Host ""
Write-Host "To uninstall: .\dogfood.ps1 -Uninstall"
