param(
  [Parameter(Mandatory = $true)]
  [string] $VsixPath,

  [Parameter(Mandatory = $true)]
  [ValidateSet('Present', 'Absent')]
  [string] $Expected
)

$ErrorActionPreference = 'Stop'

$marker = 'Unsupported Aspire extension E2E control command:'
$resolvedVsixPath = (Resolve-Path -LiteralPath $VsixPath).Path
$extractRoot = Join-Path (Split-Path -Parent $resolvedVsixPath) 'assert-e2e-bridge-vsix'

if (Test-Path -LiteralPath $extractRoot) {
  Remove-Item -LiteralPath $extractRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
Expand-Archive -LiteralPath $resolvedVsixPath -DestinationPath $extractRoot -Force

$bundlePath = Join-Path $extractRoot 'extension/dist/extension.js'
if (-not (Test-Path -LiteralPath $bundlePath)) {
  throw "Expected VSIX bundle at '$bundlePath', but it was not found."
}

$bundle = Get-Content -LiteralPath $bundlePath -Raw
$containsMarker = $bundle.Contains($marker)

if ($Expected -eq 'Present' -and -not $containsMarker) {
  throw "Expected E2E bridge marker '$marker' to be present in '$bundlePath', but it was absent."
}

if ($Expected -eq 'Absent' -and $containsMarker) {
  throw "Expected E2E bridge marker '$marker' to be absent from '$bundlePath', but it was present."
}

$state = if ($containsMarker) { 'present' } else { 'absent' }
Write-Host "E2E bridge marker '$marker' is $state in '$bundlePath' as expected ($Expected)."
