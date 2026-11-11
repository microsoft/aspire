#requires -Version 7.4
<#
.SYNOPSIS
Validates the Windows tray publish output or actual embedded bundle archive.
#>
[CmdletBinding(DefaultParameterSetName = 'Archive')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Archive')][string] $Archive,
    [Parameter(Mandatory, ParameterSetName = 'Directory')][string] $PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string] $Rid,
    [ValidateSet('true', 'false')][string] $RequireSignature = 'false'
)

$ErrorActionPreference = 'Stop'
$temporary = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Archive') {
        $temporary = [IO.Directory]::CreateTempSubdirectory('aspire-tray-payload-').FullName
        & tar -xzf $Archive -C $temporary "$Rid/tray"
        if ($LASTEXITCODE -ne 0) { throw "Could not extract $Rid/tray from $Archive." }
        $tray = Join-Path $temporary "$Rid/tray"
        $actual = @(Get-ChildItem -LiteralPath $tray -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($tray, $_.FullName).Replace('\', '/')
        } | Sort-Object)
        $expected = @('Aspire.ico', 'aspire-tray.exe') | Sort-Object
        if (@(Compare-Object $expected $actual -CaseSensitive).Count -ne 0) {
            throw "Unexpected Windows tray payload: $($actual -join ', ')."
        }
    } else {
        $tray = [IO.Path]::GetFullPath($PublishDirectory)
    }
    foreach ($name in @('aspire-tray.exe', 'Aspire.ico')) {
        $file = Get-Item -LiteralPath (Join-Path $tray $name)
        if ($file.PSIsContainer -or $file.LinkType -or $file.Length -eq 0) {
            throw "Required Windows tray payload must be a nonempty regular file: $name."
        }
    }
    $icon = [IO.File]::ReadAllBytes((Join-Path $tray 'Aspire.ico'))
    $originalIcon = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../../src/Shared/Aspire.ico'))
    if ([Convert]::ToBase64String($icon) -cne [Convert]::ToBase64String($originalIcon)) {
        throw 'Windows tray must contain the original src/Shared/Aspire.ico.'
    }

    $executable = Join-Path $tray 'aspire-tray.exe'
    $stream = [IO.File]::OpenRead($executable)
    $reader = $null
    try {
        # Inspect PE/COFF metadata rather than trusting the publish directory name.
        # https://learn.microsoft.com/windows/win32/debug/pe-format
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        $headers = $reader.PEHeaders
        $machine = if ($Rid -eq 'win-arm64') { 'Arm64' } else { 'Amd64' }
        if ($headers.CoffHeader.Machine.ToString() -ne $machine -or
            $null -eq $headers.PEHeader -or $headers.PEHeader.Magic.ToString() -ne 'PE32Plus' -or
            $headers.PEHeader.Subsystem.ToString() -ne 'WindowsGui' -or
            $null -ne $headers.CorHeader -or
            ($headers.CoffHeader.Characteristics -band [System.Reflection.PortableExecutable.Characteristics]::Dll)) {
            throw "Windows tray payload must be a native WinExe for $Rid."
        }
    }
    finally {
        if ($reader) { $reader.Dispose() }
        $stream.Dispose()
    }

    if ($RequireSignature -eq 'true') {
        $signature = Get-AuthenticodeSignature -LiteralPath $executable
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
            throw "Windows tray must have a valid Microsoft Authenticode signature; status: $($signature.Status)."
        }
    }
    Write-Host "Verified $Rid Windows tray payload, original icon, native architecture and required signature."
}
finally {
    if ($temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
