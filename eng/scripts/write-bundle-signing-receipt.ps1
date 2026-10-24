#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SigningLog,
    [Parameter(Mandatory)][string] $OutputPath,
    [Parameter(Mandatory)][string] $SigningRequestsDirectory,
    [string] $SignedAppPath
)

$ErrorActionPreference = 'Stop'
$lines = @(Get-Content -LiteralPath $SigningLog)
if (-not ($lines -match 'DryRun: False') -or -not ($lines -match 'Signing mode: Real') -or
    ($lines -match 'DryRun: True')) {
    throw 'A receipt requires real signing, not a test or dry-run pass.'
}
if (-not ($lines -match 'Build artifacts signed and validated\.')) {
    throw 'The signing pass did not complete successfully.'
}

$files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
function Add-ReceiptFile([string] $Path, [string] $Disposition) {
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer) { throw "Expected a signing output file: $Path" }
    $fullPath = $file.FullName
    $files[$fullPath] = [ordered]@{
        source = $fullPath
        disposition = $Disposition
        length = $file.Length
        # This hash binds accounting to the signed output bytes, not a similarly
        # named file in a later NuGet signing pass.
        sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    }
}

# Arcade's resolved MicroBuild requests use URI-escaped absolute paths:
#   <FilesToSign Include="%2Fpublish%2Fhelper.zip">
#     <Authenticode>MacDeveloperHarden</Authenticode>
#   </FilesToSign>
# Mach-O signing transports helper as helper.zip, then extracts it back in place.
# These files are read only after the containing real signing pass succeeds.
# https://github.com/dotnet/arcade/blob/614293a9b3aa17dda9dc7e69c4dfcd576f59741a/src/Microsoft.DotNet.SignTool/src/SignTool.cs
foreach ($request in Get-ChildItem -LiteralPath $SigningRequestsDirectory -Filter 'Round*-Sign.proj' -File -Recurse) {
    [xml] $document = Get-Content -LiteralPath $request.FullName -Raw
    foreach ($item in $document.SelectNodes("//*[local-name()='FilesToSign']")) {
        $certificate = [string] $item.Authenticode
        if ([string]::IsNullOrWhiteSpace($certificate) -or $certificate -eq 'None') {
            throw "Signing request has no signing operation: $($request.FullName)"
        }
        $path = [Uri]::UnescapeDataString([string] $item.Include)
        if ($path.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $path.Substring(0, $path.Length - 4) -PathType Leaf)) {
            $path = $path.Substring(0, $path.Length - 4)
        }
        Add-ReceiptFile $path 'processed'
    }
}

# Pinned Arcade SignTool emits these resolved-file records, for example:
#   PE file C:\publish\helper.exe is signed properly
#   File '/publish/helper' is already signed.
# Diagnostic file logging may append " (TaskId:3)". Do not count the broader
# "Tracking file" or "ItemsToSign" records as proof of successful processing.
# https://github.com/dotnet/arcade/blob/614293a9b3aa17dda9dc7e69c4dfcd576f59741a/src/Microsoft.DotNet.SignTool/src/BatchSignUtil.cs
foreach ($line in $lines) {
    if ($line -match "Updating '(.+)' with signed content(?:\s+\(TaskId:\d+\))?\s*$") {
        Add-ReceiptFile $Matches[1] 'processed'
    }
    elseif ($line -match '(?:PE|MachO|ELF) file (.+) is signed properly(?:\s+\(TaskId:\d+\))?\s*$') {
        Add-ReceiptFile $Matches[1] 'processed'
    }
    elseif ($line -match "File '(.+)' is already signed\.(?:\s+\(TaskId:\d+\))?\s*$") {
        Add-ReceiptFile $Matches[1] 'already-signed'
    }
}
if ($SignedAppPath) {
    # The caller supplies this only after the separate whole-app signing,
    # notarization, and RestoreSignedTray targets have all succeeded.
    $app = Get-Item -LiteralPath $SignedAppPath
    if (-not $app.PSIsContainer) { throw "Signed app directory is missing: $SignedAppPath" }
    foreach ($file in Get-ChildItem -LiteralPath $app.FullName -Recurse -File -Force) {
        Add-ReceiptFile $file.FullName 'whole-app-signing'
    }
}

$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
$null = [IO.Directory]::CreateDirectory($parent)
[ordered]@{ version = 1; files = @($files.Values | Sort-Object source) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Recorded $($files.Count) resolved signing outputs: $OutputPath"
