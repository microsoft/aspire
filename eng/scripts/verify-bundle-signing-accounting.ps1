#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $LayoutDirectory,
    [Parameter(Mandatory)][string] $Receipt,
    [Parameter(Mandatory)][string] $Policy,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64')][string] $Rid,
    [Parameter(Mandatory)][string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $LayoutDirectory).FullName
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Layout directory is missing: $root" }
$receiptData = Get-Content -LiteralPath $Receipt -Raw | ConvertFrom-Json
if ($receiptData.version -ne 1) { throw "Unsupported signing receipt version: $($receiptData.version)" }
$policyData = Get-Content -LiteralPath $Policy -Raw | ConvertFrom-Json
if ($policyData.version -ne 1) { throw "Unsupported signing policy version: $($policyData.version)" }
$receipts = @($receiptData.files)
foreach ($entry in $receipts) {
    if ($entry.disposition -notin @('processed', 'already-signed', 'whole-app-signing') -or
        $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or [string]::IsNullOrWhiteSpace($entry.source)) {
        throw 'Invalid signing receipt entry.'
    }
}
$entries = [Collections.Generic.List[object]]::new()
$missing = [Collections.Generic.List[string]]::new()
$nativeCount = 0
foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    $stream = [IO.File]::OpenRead($file.FullName)
    try {
        $header = [byte[]]::new(4)
        $read = $stream.Read($header, 0, 4)
    }
    finally { $stream.Dispose() }
    # PE starts MZ; Mach-O/fat headers include both endiannesses. This is file
    # discovery, not architecture or signature verification (handled separately).
    $native = ($read -ge 2 -and $header[0] -eq 0x4d -and $header[1] -eq 0x5a) -or
        ($read -eq 4 -and [Convert]::ToHexString($header) -in
            @('CFFAEDFE', 'CEFAEDFE', 'FEEDFACF', 'FEEDFACE', 'CAFEBABE', 'BEBAFECA', 'CAFEBABF', 'BFBAFECA'))
    $record = [ordered]@{ path = $relative; disposition = 'non-native'; source = $null; reason = $null }
    if ($native) {
        $nativeCount++
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $match = @($receipts | Where-Object { $_.sha256 -eq $hash -and $_.length -eq $file.Length })
        if ($match.Count) {
            $record.disposition = $match[0].disposition
            $record.source = $match[0].source
        }
        else {
            $rules = @($policyData.upstream | Where-Object { $_.rid -eq $Rid -and $_.path -ceq $relative })
            if ($rules.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($rules[0].reason)) {
                $record.disposition = 'preserved-upstream'
                $record.reason = $rules[0].reason
            }
            else {
                $record.disposition = 'unaccounted'
                $missing.Add($relative)
            }
        }
    }
    $entries.Add($record)
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))
$null = [IO.Directory]::CreateDirectory($parent)
[ordered]@{ version = 1; rid = $Rid; nativeFiles = $nativeCount; files = @($entries) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
if ($nativeCount -eq 0) { throw "No native files found in the bundle layout. Report: $ReportPath" }
if ($missing.Count) { throw "Unaccounted bundle signing inputs:`n  $($missing -join "`n  ")`nReport: $ReportPath" }
Write-Host "Signing accounting passed: $nativeCount native files, $($entries.Count) total files. Report: $ReportPath"
