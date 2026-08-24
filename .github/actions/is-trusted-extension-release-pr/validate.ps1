# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

$ErrorActionPreference = 'Stop'

function Set-TrustedOutput {
    param([bool] $IsTrusted)

    $value = $IsTrusted.ToString().ToLowerInvariant()
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "is_trusted=$value"
    Write-Host "is_trusted=$value"
}

function Invoke-Git {
    param([string[]] $Arguments)

    # ProcessStartInfo.ArgumentList passes each value directly to git. This avoids interpreting
    # revision content or event metadata as PowerShell syntax.
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.WorkingDirectory = (Get-Location).Path
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false

    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        if (-not $process.Start()) {
            throw 'Failed to start git.'
        }

        # Drain both redirected streams concurrently so git cannot block on a full pipe.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        if ($process.ExitCode -ne 0) {
            throw "git failed with exit code $($process.ExitCode): $stderr"
        }

        return $stdout
    }
    finally {
        $process.Dispose()
    }
}

try {
    if ($env:REPOSITORY -cne 'microsoft/aspire' -or
        $env:BASE_REF -cne 'main' -or
        $env:HEAD_REPO -cne 'microsoft/aspire' -or
        $env:AUTHOR -cne 'aspire-repo-bot[bot]' -or
        $env:ACTOR -cne 'aspire-repo-bot[bot]' -or
        $env:HEAD_REF -cnotmatch '^extension-release/v.+$') {
        Set-TrustedOutput $false
        exit 0
    }

    if ($env:BASE_SHA -cnotmatch '^[0-9a-f]{40}$' -or
        $env:HEAD_SHA -cnotmatch '^[0-9a-f]{40}$') {
        throw 'The base or head revision is not a full lowercase Git SHA.'
    }

    $changedFilesText = Invoke-Git @(
        'diff',
        '--name-only',
        '--no-renames',
        $env:BASE_SHA,
        $env:HEAD_SHA,
        '--'
    )
    $changedFiles = @($changedFilesText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
    $expectedChangedFiles = @('extension/CHANGELOG.md', 'extension/package.json')
    if ($changedFiles.Count -ne $expectedChangedFiles.Count -or
        (Compare-Object $changedFiles $expectedChangedFiles).Count -ne 0) {
        Set-TrustedOutput $false
        exit 0
    }

    $basePackageJson = Invoke-Git @('show', "$($env:BASE_SHA):extension/package.json")
    $headPackageJson = Invoke-Git @('show', "$($env:HEAD_SHA):extension/package.json")
    $basePackage = $basePackageJson | ConvertFrom-Json -AsHashtable -Depth 100
    $headPackage = $headPackageJson | ConvertFrom-Json -AsHashtable -Depth 100

    if ($basePackage -isnot [System.Collections.IDictionary] -or
        $headPackage -isnot [System.Collections.IDictionary] -or
        -not $basePackage.Contains('version') -or
        -not $headPackage.Contains('version')) {
        Set-TrustedOutput $false
        exit 0
    }

    $baseVersion = $basePackage['version']
    $headVersion = $headPackage['version']
    if ($baseVersion -isnot [string] -or
        $headVersion -isnot [string] -or
        $baseVersion -cnotmatch '^\d+\.\d+\.\d+$' -or
        $headVersion -cnotmatch '^\d+\.\d+\.\d+$' -or
        $baseVersion -ceq $headVersion) {
        Set-TrustedOutput $false
        exit 0
    }

    $basePackage.Remove('version')
    $headPackage.Remove('version')
    $basePackageWithoutVersion = $basePackage | ConvertTo-Json -Compress -Depth 100
    $headPackageWithoutVersion = $headPackage | ConvertTo-Json -Compress -Depth 100
    if ($basePackageWithoutVersion -cne $headPackageWithoutVersion) {
        Set-TrustedOutput $false
        exit 0
    }

    $baseChangelog = Invoke-Git @('show', "$($env:BASE_SHA):extension/CHANGELOG.md")
    $headChangelog = Invoke-Git @('show', "$($env:HEAD_SHA):extension/CHANGELOG.md")
    if ($headChangelog.Length -le $baseChangelog.Length -or
        -not $headChangelog.EndsWith($baseChangelog, [System.StringComparison]::Ordinal)) {
        Set-TrustedOutput $false
        exit 0
    }

    $prefix = $headChangelog.Substring(0, $headChangelog.Length - $baseChangelog.Length)
    $firstPrefixLine = ($prefix -split '\r?\n', 2)[0]
    if ($prefix.Length -eq 0 -or $firstPrefixLine -cne "## v$headVersion") {
        Set-TrustedOutput $false
        exit 0
    }

    Set-TrustedOutput $true
}
catch {
    Write-Warning "Unable to validate the trusted extension release PR patch ($($_.Exception.GetType().Name))."
    Set-TrustedOutput $false
}

exit 0
