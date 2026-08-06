#!/usr/bin/env pwsh

# Shared helpers for syncing and verifying the embedded Aspire telemetry hook scripts
# (track-telemetry.sh / track-telemetry.ps1).
#
# The hook scripts live canonically in microsoft/aspire-skills under hooks/scripts/. They are SOURCE
# files (not build outputs), so they are pinned to the immutable commit that an aspire-skills release
# tag points at and fetched through the GitHub contents API. Hashing is always done over
# LF-normalized UTF-8 (no BOM) content so the recorded hash is stable regardless of the checkout's
# line-ending policy: .sh is `eol=lf` while .ps1 is `text=auto` (checked out CRLF on Windows).

Set-StrictMode -Version Latest

$script:AspireSkillsHookFileNames = @('track-telemetry.sh', 'track-telemetry.ps1')
$script:AspireSkillsHookRepoDirectory = 'hooks/scripts'

function Get-AspireSkillsHookFileNames {
    return $script:AspireSkillsHookFileNames
}

function Invoke-AspireSkillsGitHubApi {
    param([Parameter(Mandatory = $true)][string]$Endpoint)

    # Suppress the native-command auto-throw locally so the explicit exit-code check below runs and
    # gh's own diagnostics (for example "gh: Not Found (HTTP 404)") are preserved in the thrown
    # message. Callers use that text to tell an absent path (an expected 404) from a real failure.
    $PSNativeCommandUseErrorActionPreference = $false

    $stderr = ''
    $stdout = & gh api $Endpoint 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            $stderr += "$($_.ToString())`n"
        }
        else {
            $_
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "gh api $Endpoint failed with exit code $LASTEXITCODE. $($stderr.Trim())"
    }

    return $stdout
}

function ConvertTo-LfUtf8Bytes {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)

    # Strip a UTF-8 BOM if present, then normalize CRLF/CR to LF. A BOM or CRLF in track-telemetry.sh
    # would break the shebang/shell on POSIX hosts, and normalizing makes the recorded hash independent
    # of how git checked the file out.
    $text = [System.Text.UTF8Encoding]::new($false).GetString($Bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
        $text = $text.Substring(1)
    }

    $text = $text -replace "`r`n", "`n" -replace "`r", "`n"
    return [System.Text.UTF8Encoding]::new($false).GetBytes($text)
}

function Get-AspireSkillsSha256Hex {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-AspireSkillsReleaseCommitSha {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag
    )

    # Resolve the tag to the commit it points at (dereferences annotated tags). Pinning to the commit
    # rather than the tag means a force-moved tag cannot silently change what gets synced or verified.
    $sha = (Invoke-AspireSkillsGitHubApi "repos/$Repository/commits/$Tag" | ConvertFrom-Json).sha
    if ([string]::IsNullOrWhiteSpace($sha)) {
        throw "Could not resolve a commit SHA for tag '$Tag' in '$Repository'."
    }

    return $sha
}

function Get-AspireSkillsHookContent {
    # Fetch a single hook script from aspire-skills at an immutable commit and return its
    # LF-normalized UTF-8 bytes.
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$CommitSha,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    $path = "$script:AspireSkillsHookRepoDirectory/$FileName"
    $endpoint = "repos/$Repository/contents/$path" + "?ref=$CommitSha"
    $response = Invoke-AspireSkillsGitHubApi $endpoint | ConvertFrom-Json

    if ($response.type -ne 'file') {
        throw "Aspire skills hook '$path' at commit '$CommitSha' is not a file (type '$($response.type)')."
    }

    if ($response.name -ne $FileName) {
        throw "Aspire skills hook response name '$($response.name)' did not match expected '$FileName'."
    }

    # The contents API returns base64 with embedded newlines; strip all whitespace before decoding.
    $rawBytes = [System.Convert]::FromBase64String(($response.content -replace '\s', ''))
    return ConvertTo-LfUtf8Bytes -Bytes $rawBytes
}
