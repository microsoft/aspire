param(
    [Parameter(Mandatory)]
    [string]$BuildLog
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BuildLog -PathType Leaf)) {
    throw "Native AOT Dashboard build log was not found at '$BuildLog'."
}

$expectedOrigin = 'Microsoft.AspNetCore.Http.Json.JsonOptions.CreateDefaultTypeResolver()'
$warnings = @(
    Select-String -LiteralPath $BuildLog -Pattern '\bwarning IL2026:' |
        ForEach-Object {
            # MSBuild can repeat the same diagnostic in its final summary with a "1>" project prefix.
            # Count distinct diagnostics rather than console-rendering occurrences.
            ($_.Line -replace '\x1b\[[0-9;]*m', '' -replace '^\s*\d+>', '').Trim()
        } |
        Sort-Object -Unique
)

if ($warnings.Count -ne 1) {
    $details = if ($warnings.Count -eq 0) {
        'No IL2026 warnings were found.'
    }
    else {
        "Found warnings:$([Environment]::NewLine)$($warnings -join [Environment]::NewLine)"
    }

    throw "Expected exactly one IL2026 warning from '$expectedOrigin', but found $($warnings.Count). $details"
}

if (-not $warnings[0].Contains($expectedOrigin, [StringComparison]::Ordinal)) {
    throw "Expected the IL2026 warning to originate from '$expectedOrigin', but found: $($warnings[0])"
}

Write-Host "Verified the expected Native AOT Dashboard IL2026 warning from '$expectedOrigin'."
