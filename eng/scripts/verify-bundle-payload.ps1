#requires -Version 7.4
<#
.SYNOPSIS
Validates the assembled bundle archive or the layout extracted by the CLI.
#>
[CmdletBinding(DefaultParameterSetName = 'Archive')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Archive')][string] $Archive,
    [Parameter(Mandatory, ParameterSetName = 'Layout')][string] $LayoutDirectory,
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64')]
    [string] $Rid,
    [ValidateSet('true', 'false')][string] $RequireSignature = 'false'
)

$ErrorActionPreference = 'Stop'
$temporary = $null

function Assert-PayloadFile([string] $RelativePath) {
    $path = Join-Path $layout $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Required bundle payload is missing or empty: $RelativePath"
    }
    return $path
}

function Assert-NativeExecutable([string] $Path) {
    if ($Rid.StartsWith('win-')) {
        $stream = [IO.File]::OpenRead($Path)
        $reader = $null
        try {
            $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
            $machine = if ($Rid -eq 'win-arm64') { 'Arm64' } else { 'Amd64' }
            if ($reader.PEHeaders.CoffHeader.Machine.ToString() -ne $machine -or $reader.PEHeaders.CorHeader) {
                throw "Bundle executable is not native $Rid output: $Path"
            }
        }
        finally {
            if ($reader) { $reader.Dispose() }
            $stream.Dispose()
        }
    }
    elseif ($Rid.StartsWith('osx-')) {
        $architecture = if ($Rid -eq 'osx-arm64') { 'arm64' } else { 'x86_64' }
        & lipo $Path -verify_arch $architecture
        if ($LASTEXITCODE -ne 0) { throw "Bundle executable has the wrong architecture for ${Rid}: $Path" }
    }
    else {
        $stream = [IO.File]::OpenRead($Path)
        try {
            $header = [byte[]]::new(20)
            $stream.ReadExactly($header)
            # ELF64 little-endian: 7f 45 4c 46 02 01 ...; e_machine is at offset 18.
            # https://refspecs.linuxfoundation.org/elf/gabi4+/ch4.eheader.html
            $machine = if ($Rid -eq 'linux-arm64') { 183 } else { 62 }
            if ([Convert]::ToHexString($header[0..5]) -ne '7F454C460201' -or
                [BitConverter]::ToUInt16($header, 18) -ne $machine) {
                throw "Bundle executable is not ELF64 $Rid output: $Path"
            }
        }
        finally { $stream.Dispose() }
    }

    if (-not $Rid.StartsWith('win-') -and -not $IsWindows) {
        $mode = [IO.File]::GetUnixFileMode($Path)
        if (($mode -band [IO.UnixFileMode]::UserExecute) -eq 0) {
            throw "Bundle executable lost executable permissions: $Path"
        }
    }

    if ($RequireSignature -eq 'true') {
        if ($Rid.StartsWith('win-')) {
            if (-not $IsWindows) { throw 'Authenticode verification requires a Windows host.' }
            $signature = Get-AuthenticodeSignature -LiteralPath $Path
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
                throw "Bundle executable requires a valid Microsoft Authenticode signature: $Path (status: $($signature.Status))"
            }
        }
        elseif ($Rid.StartsWith('osx-')) {
            & codesign --verify --strict '-R=anchor apple generic' $Path
            if ($LASTEXITCODE -ne 0) { throw "Bundle executable signature verification failed: $Path" }
        }
        else { throw 'Official executable signature verification is supported only for Windows and macOS bundles.' }
    }
}

try {
    if ($PSCmdlet.ParameterSetName -eq 'Archive') {
        $temporary = [IO.Directory]::CreateTempSubdirectory('aspire-bundle-verification-').FullName
        & tar -xzf $Archive -C $temporary
        if ($LASTEXITCODE -ne 0) { throw "Could not extract bundle archive: $Archive" }
        $roots = @(Get-ChildItem -LiteralPath $temporary -Force)
        if ($roots.Count -ne 1 -or -not $roots[0].PSIsContainer -or $roots[0].Name -cne $Rid) {
            throw "Bundle archive must contain exactly one '$Rid' root directory."
        }
        $layout = $roots[0].FullName
    }
    else { $layout = [IO.Path]::GetFullPath($LayoutDirectory) }

    # Runtime requirements follow BundleService.IsVersionedLayoutValid and
    # src/Shared/TerminalHostPayload.cs, but inspect the packaged files themselves.
    $extension = if ($Rid.StartsWith('win-')) { '.exe' } else { '' }
    $executables = @(
        "managed/aspire-managed$extension"
        "apphost-server/aspire-apphost-server$extension"
        "dashboard/Aspire.Dashboard$extension"
        "terminalhost/Aspire.TerminalHost$extension"
    )
    foreach ($relative in $executables) {
        Assert-NativeExecutable (Assert-PayloadFile $relative)
    }
    $null = Assert-PayloadFile "dcp/dcp$extension"
    $null = Assert-PayloadFile 'apphost-server/ats-contract.json'
    $null = Assert-PayloadFile 'dashboard/wwwroot/_framework/blazor.web.js'
    $sqlite = if ($Rid.StartsWith('win-')) { 'e_sqlite3.dll' } elseif ($Rid.StartsWith('osx-')) { 'libe_sqlite3.dylib' } else { 'libe_sqlite3.so' }
    $null = Assert-PayloadFile "dashboard/$sqlite"

    if ($Rid.StartsWith('win-')) {
        $ptyFiles = @('hex1bpty.exe', 'conpty.dll', 'arm64/OpenConsole.exe')
        if ($Rid -eq 'win-x64') { $ptyFiles += 'x64/OpenConsole.exe' }
        foreach ($file in $ptyFiles) {
            $null = Assert-PayloadFile "terminalhost/$file"
            $null = Assert-PayloadFile "managed/$file"
        }
    }
    else {
        $interop = if ($Rid.StartsWith('osx-')) { 'libhex1binterop.dylib' } else { 'libhex1binterop.so' }
        $null = Assert-PayloadFile "terminalhost/$interop"
    }

    foreach ($directory in @('managed', 'apphost-server', 'dashboard', 'terminalhost')) {
        $symbols = @(Get-ChildItem -LiteralPath (Join-Path $layout $directory) -Recurse -Force |
            Where-Object { $_.Name -match '\.(pdb|dbg|dSYM)$' })
        if ($symbols.Count -gt 0) { throw "Bundle runtime payload contains debug symbols: $($symbols[0].FullName)" }
    }
    if ($Rid.StartsWith('linux-') -and (Test-Path -LiteralPath (Join-Path $layout 'tray'))) {
        throw 'Linux bundles must not contain a tray payload.'
    }
    if ($PSCmdlet.ParameterSetName -eq 'Layout' -and $Rid.StartsWith('win-')) {
        & "$PSScriptRoot/../../tools/CreateLayout/verify-windows-tray-payload.ps1" -PublishDirectory (Join-Path $layout 'tray') -Rid $Rid -RequireSignature $RequireSignature
        if ($LASTEXITCODE -ne 0) { throw 'Extracted Windows tray verification failed.' }
    }
    if ($PSCmdlet.ParameterSetName -eq 'Layout' -and $Rid.StartsWith('osx-')) {
        foreach ($file in @('Info.plist', 'Resources/Aspire.icns', '_CodeSignature/CodeResources')) {
            $null = Assert-PayloadFile "tray/Aspire Tray.app/Contents/$file"
        }
        Assert-NativeExecutable (Assert-PayloadFile 'tray/Aspire Tray.app/Contents/MacOS/aspire-tray')
    }
    # Archive builds run the dedicated tray verifier afterward. Recheck the
    # app here only for the layout extracted from the final distributed CLI.
    if ($PSCmdlet.ParameterSetName -eq 'Layout' -and $Rid.StartsWith('osx-') -and $RequireSignature -eq 'true') {
        $app = Join-Path $layout 'tray/Aspire Tray.app'
        & codesign --verify --strict '-R=anchor apple generic' $app
        if ($LASTEXITCODE -ne 0) { throw 'Packaged macOS tray signature verification failed.' }
        & xcrun stapler validate $app
        if ($LASTEXITCODE -ne 0) { throw 'Packaged macOS tray is missing a valid notarization ticket.' }
    }
    Write-Host "Verified $Rid bundle payload (required files, native architectures, executable permissions; official signatures: $RequireSignature)."
}
finally {
    if ($temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
