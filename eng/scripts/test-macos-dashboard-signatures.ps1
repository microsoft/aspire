param(
    [Parameter(Mandatory)]
    [string]$DashboardPath,

    [string]$CodeSignPath = 'codesign'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $DashboardPath -PathType Leaf)) {
    throw "Native AOT Dashboard executable was not found at '$DashboardPath'."
}

function Get-TeamIdentifier {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    # codesign writes signature metadata to stderr as:
    #   Executable=/path/to/Aspire.Dashboard
    #   ...
    #   TeamIdentifier=UBF8T346G9
    $signatureOutput = @(& $CodeSignPath --display --verbose=4 -- $Path 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to inspect the code signature for '$Path': $($signatureOutput -join [Environment]::NewLine)"
    }

    $teamIdentifierMatches = @(
        $signatureOutput |
            ForEach-Object { $_.ToString() } |
            Select-String -Pattern '^TeamIdentifier=(?<TeamIdentifier>.+)$'
    )
    if ($teamIdentifierMatches.Count -ne 1) {
        throw "Expected one TeamIdentifier in the code signature for '$Path', found $($teamIdentifierMatches.Count)."
    }

    $teamIdentifier = $teamIdentifierMatches[0].Matches[0].Groups['TeamIdentifier'].Value
    if ([string]::IsNullOrWhiteSpace($teamIdentifier) -or $teamIdentifier -eq 'not set') {
        throw "The code signature for '$Path' does not have a TeamIdentifier."
    }

    return $teamIdentifier
}

$dashboardTeamIdentifier = Get-TeamIdentifier -Path $DashboardPath
$payloadDirectory = Split-Path -Parent $DashboardPath
$nativeLibraries = @(Get-ChildItem -LiteralPath $payloadDirectory -Filter '*.dylib' -File -Recurse)
if ($nativeLibraries.Count -eq 0) {
    throw "No native libraries were found next to the Native AOT Dashboard at '$payloadDirectory'."
}

foreach ($nativeLibrary in $nativeLibraries) {
    $libraryTeamIdentifier = Get-TeamIdentifier -Path $nativeLibrary.FullName
    if ($libraryTeamIdentifier -ne $dashboardTeamIdentifier) {
        throw "Native library '$($nativeLibrary.FullName)' has TeamIdentifier '$libraryTeamIdentifier', expected '$dashboardTeamIdentifier' from '$DashboardPath'."
    }
}

Write-Host "macOS Dashboard signature validation passed for $($nativeLibraries.Count) native libraries with Team ID '$dashboardTeamIdentifier'."
