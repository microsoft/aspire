<#
.SYNOPSIS
Validate that every microsoft-aspire-cli*.tgz under a Shipping directory has a
companion .sig sidecar containing a plausible PGP signature.

.DESCRIPTION
This is the post-signing validation that catches the most likely silent failure
mode in Arcade/ESRP signing: the sidecar file gets emitted (so a naive
file-existence check passes) but the content is empty or garbage. We confirm the
file is large enough and starts with either the ASCII-armored PGP header
(RFC 9580 §6) or an OpenPGP binary signature packet (tag 2: old-format
0x88..0x8B or new-format 0xC2, RFC 9580 §4.3 / §5.2).

.PARAMETER ShippingDir
Directory to scan recursively for microsoft-aspire-cli*.tgz files. Each found
tarball must have a sibling <tarball>.sig file alongside it.
#>

param(
  [Parameter(Mandatory = $true)]
  [string]$ShippingDir
)

$ErrorActionPreference = 'Stop'

if (!(Test-Path -LiteralPath $ShippingDir -PathType Container)) {
  Write-Error "Shipping packages directory not found (or not a directory): $ShippingDir"
  exit 1
}

$npmPackages = @(Get-ChildItem -Path $ShippingDir -Filter "microsoft-aspire-cli*.tgz" -Recurse -File)
if ($npmPackages.Count -eq 0) {
  Write-Error "No Aspire CLI npm packages were found under '$ShippingDir'."
  exit 1
}

$missingSignatures = @()
$invalidSignatures = @()
foreach ($package in $npmPackages) {
  $signaturePath = "$($package.FullName).sig"
  if (!(Test-Path $signaturePath)) {
    $missingSignatures += $signaturePath
    continue
  }

  $signatureFile = Get-Item -LiteralPath $signaturePath
  if ($signatureFile.Length -lt 64) {
    $invalidSignatures += "$signaturePath (only $($signatureFile.Length) bytes)"
    continue
  }
  $bytes = [System.IO.File]::ReadAllBytes($signaturePath)[0..63]
  $asciiPreview = [System.Text.Encoding]::ASCII.GetString($bytes)
  $looksArmored = $asciiPreview.Contains('-----BEGIN PGP SIGNATURE-----')
  $firstByte = $bytes[0]
  $looksBinarySignaturePacket = ($firstByte -ge 0x88 -and $firstByte -le 0x8B) -or $firstByte -eq 0xC2
  if (-not $looksArmored -and -not $looksBinarySignaturePacket) {
    $hexPrefix = ($bytes[0..15] | ForEach-Object { '{0:x2}' -f $_ }) -join ' '
    $invalidSignatures += "$signaturePath (no PGP signature marker; first 16 bytes: $hexPrefix)"
    continue
  }

  Write-Host "Found npm package signature sidecar: $([System.IO.Path]::GetFileName($signaturePath))"
}

if ($missingSignatures.Count -gt 0) {
  # Use Write-Host "##[error]..." so both failure categories surface in
  # one CI run. Write-Error would be terminating under $ErrorActionPreference
  # = 'Stop' and hide the invalid-signature category from operators
  # diagnosing a real signing outage.
  Write-Host "##[error]Missing detached signature sidecar(s) for npm package(s): $($missingSignatures -join ', ')"
}

if ($invalidSignatures.Count -gt 0) {
  Write-Host "##[error]Detached signature sidecar(s) failed content sanity check: $($invalidSignatures -join '; ')"
}

if ($missingSignatures.Count -gt 0 -or $invalidSignatures.Count -gt 0) {
  exit 1
}

Write-Host "Validated $($npmPackages.Count) Aspire CLI npm package signature sidecar(s)."
