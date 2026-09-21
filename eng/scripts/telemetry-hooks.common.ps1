#!/usr/bin/env pwsh

# Hook sources are pinned to released main-line commits.
# Hash normalized UTF-8 so Windows checkouts and the canonical source have the same identity.
Set-StrictMode -Version Latest

$script:TelemetryHooksRepository = 'microsoft/aspire-skills'
$script:TelemetryHookFileNames = @('track-telemetry.sh', 'track-telemetry.ps1')
$script:TelemetryHooksMetadataFileName = 'telemetry-hooks.metadata.json'

function Get-TelemetryHookFileNames {
    return $script:TelemetryHookFileNames
}

function Invoke-TelemetryHooksGitHubApi {
    param([Parameter(Mandatory = $true)][string]$Endpoint)

    if (-not $Endpoint.StartsWith("repos/$script:TelemetryHooksRepository/", [StringComparison]::Ordinal)) {
        throw "Unexpected telemetry hook repository endpoint '$Endpoint'."
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "The GitHub CLI ('gh') is required to maintain telemetry hooks."
    }

    # Keep gh's diagnostics, including HTTP 404 and authentication failures. None are soft skips.
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & gh api --hostname github.com -H 'Accept: application/vnd.github+json' `
        -H 'X-GitHub-Api-Version: 2022-11-28' $Endpoint 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "gh api $Endpoint failed with exit code $LASTEXITCODE. $($output -join "`n")"
    }

    return $output -join "`n"
}

function ConvertTo-TelemetryHookBytes {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)

    # Strip EF BB BF, then normalize CRLF and bare CR to LF. Reject malformed UTF-8 rather
    # than hashing replacement characters that could conceal different source bytes.
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($Bytes)
    if ($text.StartsWith([string][char]0xFEFF, [StringComparison]::Ordinal)) {
        $text = $text.Substring(1)
    }

    return ,$utf8.GetBytes($text.Replace("`r`n", "`n").Replace("`r", "`n"))
}

function Get-TelemetryHookSha512Hex {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA512]::Create()
    try {
        return [Convert]::ToHexString($sha.ComputeHash($Bytes)).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-TelemetryHooksCommitSha {
    param([AllowNull()]$Value)

    if ($Value -isnot [string] -or $Value -cnotmatch '\A[0-9a-fA-F]{40}\z') {
        throw 'Telemetry hooks require a full, immutable 40-hex commit SHA.'
    }
}

function Get-TelemetryHooksVersion {
    param([AllowNull()]$Value)

    # SemVer 2.0: no v prefix, leading zeroes, or empty identifiers. Build metadata is
    # part of the exact source version, but not its precedence: https://semver.org/
    $identifier = '(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)'
    $pattern = "\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-($identifier(?:\.$identifier)*))?(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?\z"
    if ($Value -isnot [string] -or -not [regex]::IsMatch($Value, $pattern)) {
        throw "Telemetry hook version '$Value' must be an exact canonical SemVer version."
    }

    return [regex]::Match($Value, $pattern)
}

function Compare-TelemetryHooksVersion {
    param([Parameter(Mandatory = $true)][string]$Version, [Parameter(Mandatory = $true)][string]$PreviousVersion)

    $left = (Get-TelemetryHooksVersion $Version).Groups
    $right = (Get-TelemetryHooksVersion $PreviousVersion).Groups
    foreach ($index in 1..3) {
        $comparison = ([System.Numerics.BigInteger]::Parse($left[$index].Value)).CompareTo(
            [System.Numerics.BigInteger]::Parse($right[$index].Value))
        if ($comparison -ne 0) {
            return $comparison
        }
    }
    if (-not $left[4].Success -or -not $right[4].Success) {
        return [int]$right[4].Success - [int]$left[4].Success
    }

    $leftParts = $left[4].Value.Split('.')
    $rightParts = $right[4].Value.Split('.')
    for ($index = 0; $index -lt [Math]::Min($leftParts.Length, $rightParts.Length); $index++) {
        $a = $leftParts[$index]
        $b = $rightParts[$index]
        $aNumeric = $a -cmatch '\A[0-9]+\z'
        $bNumeric = $b -cmatch '\A[0-9]+\z'
        $comparison = if ($aNumeric -and $bNumeric) {
            ([System.Numerics.BigInteger]::Parse($a)).CompareTo([System.Numerics.BigInteger]::Parse($b))
        }
        elseif ($aNumeric -or $bNumeric) {
            [int]$bNumeric - [int]$aNumeric
        }
        else {
            [string]::CompareOrdinal($a, $b)
        }
        if ($comparison -ne 0) {
            return $comparison
        }
    }

    return $leftParts.Length.CompareTo($rightParts.Length)
}

function Assert-TelemetryHooksJsonProperties {
    param(
        [Parameter(Mandatory = $true)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    if ($Object.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw 'Telemetry hook metadata must contain JSON objects.'
    }
    # Enumerate before deserializing: ConvertFrom-Json alone silently accepts duplicate keys.
    $actual = @($Object.EnumerateObject() | ForEach-Object { $_.Name })
    if ($actual.Count -ne $Names.Count -or @($Names | Where-Object { $actual -cnotcontains $_ }).Count -ne 0) {
        throw "Telemetry hook metadata must contain exactly these properties: $($Names -join ', ')."
    }
}

function Read-TelemetryHooksMetadata {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [System.IO.File]::Exists($Path)) {
        throw "Telemetry hook metadata was not found at '$Path'."
    }
    $json = [System.Text.Encoding]::UTF8.GetString((ConvertTo-TelemetryHookBytes ([System.IO.File]::ReadAllBytes($Path))))
    $document = [System.Text.Json.JsonDocument]::Parse($json)
    try {
        Assert-TelemetryHooksJsonProperties $document.RootElement @('version', 'repository', 'commitSha', 'files')
        Assert-TelemetryHooksJsonProperties ($document.RootElement.GetProperty('files')) (Get-TelemetryHookFileNames)
    }
    finally {
        $document.Dispose()
    }
    $metadata = ConvertFrom-Json -InputObject $json -AsHashtable -NoEnumerate
    if ($metadata.repository -isnot [string] -or $metadata.repository -cne $script:TelemetryHooksRepository) {
        throw "Unexpected telemetry hook repository. Expected '$script:TelemetryHooksRepository'."
    }
    Assert-TelemetryHooksCommitSha $metadata.commitSha
    $null = Get-TelemetryHooksVersion $metadata.version
    foreach ($name in Get-TelemetryHookFileNames) {
        if ($metadata.files[$name] -isnot [string] -or $metadata.files[$name] -cnotmatch '\A[0-9a-f]{128}\z') {
            throw "Telemetry hook '$name' must have a lowercase SHA-512 hash."
        }
    }

    return $metadata
}

function Get-TelemetryHooksCommit {
    param([Parameter(Mandatory = $true)][string]$Reference)

    if ($Reference -cne 'main') {
        Assert-TelemetryHooksCommitSha $Reference
    }
    $commit = ConvertFrom-Json -AsHashtable -NoEnumerate -InputObject (
        Invoke-TelemetryHooksGitHubApi "repos/$script:TelemetryHooksRepository/commits/$Reference")
    if ($commit -isnot [System.Collections.IDictionary]) {
        throw "Invalid commit response for '$Reference'."
    }
    Assert-TelemetryHooksCommitSha $commit.sha
    if ($Reference -cne 'main' -and $commit.sha -ine $Reference) {
        throw "Commit response SHA does not match '$Reference'."
    }
    if ($commit.parents -isnot [array]) {
        throw "Invalid commit parents for '$Reference'."
    }
    foreach ($parent in $commit.parents) {
        if ($parent -isnot [System.Collections.IDictionary]) {
            throw "Invalid commit parent for '$Reference'."
        }
        Assert-TelemetryHooksCommitSha $parent.sha
    }

    return $commit
}

function Get-TelemetryHooksComparison {
    param([Parameter(Mandatory = $true)][string]$Base, [Parameter(Mandatory = $true)][string]$Head)

    Assert-TelemetryHooksCommitSha $Base
    Assert-TelemetryHooksCommitSha $Head
    # Only the ancestry summary is needed, not every commit or repository file.
    # https://docs.github.com/rest/commits/commits#compare-two-commits
    $comparison = ConvertFrom-Json -AsHashtable -NoEnumerate -InputObject (
        Invoke-TelemetryHooksGitHubApi "repos/$script:TelemetryHooksRepository/compare/$Base...${Head}?per_page=1")
    if ($comparison -isnot [System.Collections.IDictionary] -or $comparison.status -isnot [string] -or
        $comparison.status -cnotin @('ahead', 'identical', 'behind', 'diverged')) {
        throw 'Invalid telemetry hook ancestry comparison.'
    }
    Assert-TelemetryHooksCommitSha $comparison.base_commit.sha
    Assert-TelemetryHooksCommitSha $comparison.merge_base_commit.sha
    if ($comparison.base_commit.sha -ine $Base -or
        (($comparison.status -ceq 'identical') -ne ($Base -ieq $Head)) -or
        ($comparison.status -cin @('ahead', 'identical') -and $comparison.merge_base_commit.sha -ine $Base) -or
        ($comparison.status -ceq 'behind' -and $comparison.merge_base_commit.sha -ine $Head)) {
        throw 'Telemetry hook ancestry comparison does not match the requested commits.'
    }

    return $comparison.status
}

function Assert-TelemetryHooksReleasedCommit {
    param([Parameter(Mandatory = $true)][string]$CommitSha)

    Assert-TelemetryHooksCommitSha $CommitSha
    $commit = Get-TelemetryHooksCommit 'main'
    if ($commit.sha -ine $CommitSha -and (Get-TelemetryHooksComparison $CommitSha $commit.sha) -cne 'ahead') {
        throw "Telemetry hook commit '$CommitSha' is not on released main lineage."
    }

    # An all-ancestor check also accepts dev commits merged through parents[1]. Walk
    # only parents[0], with no history cutoff, so supported main releases remain verifiable.
    # At merges, reject a second-parent-only ancestor before walking unrelated history.
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($commit.sha -ine $CommitSha) {
        if (-not $seen.Add($commit.sha) -or $commit.parents.Count -eq 0) {
            throw "Telemetry hook commit '$CommitSha' is not on released first-parent main lineage."
        }
        $parent = $commit.parents[0].sha
        if ($commit.parents.Count -gt 1 -and $parent -ine $CommitSha -and
            (Get-TelemetryHooksComparison $CommitSha $parent) -cne 'ahead') {
            throw "Telemetry hook commit '$CommitSha' is not on released first-parent main lineage."
        }
        $commit = Get-TelemetryHooksCommit $parent
    }
}

function Get-TelemetryHooksSourceContent {
    param(
        [Parameter(Mandatory = $true)][string]$CommitSha,
        [Parameter(Mandatory = $true)]
        [ValidateSet('.claude-plugin/plugin.json', 'hooks/scripts/track-telemetry.sh', 'hooks/scripts/track-telemetry.ps1')]
        [string]$Path
    )

    Assert-TelemetryHooksCommitSha $CommitSha
    $response = ConvertFrom-Json -AsHashtable -NoEnumerate -InputObject (
        Invoke-TelemetryHooksGitHubApi "repos/$script:TelemetryHooksRepository/contents/${Path}?ref=$CommitSha")
    # Contents responses look like {"type":"file","name":"track-telemetry.sh",
    # "path":"hooks/scripts/track-telemetry.sh","encoding":"base64","content":"IyEv...\n"}.
    # Never follow download_url or accept a symlink/directory in place of the pinned source.
    # https://docs.github.com/rest/repos/contents#get-repository-content
    if ($response -isnot [System.Collections.IDictionary] -or
        $response.type -isnot [string] -or $response.type -cne 'file' -or
        $response.name -isnot [string] -or $response.name -cne ($Path.Split('/')[-1]) -or
        $response.path -isnot [string] -or $response.path -cne $Path -or
        $response.encoding -isnot [string] -or $response.encoding -cne 'base64' -or $response.content -isnot [string]) {
        throw "Invalid canonical telemetry hook content response for '$Path' at '$CommitSha'."
    }
    Assert-TelemetryHooksCommitSha $response.sha
    # GitHub inserts whitespace in base64 content; FromBase64String accepts those line breaks.
    $bytes = ConvertTo-TelemetryHookBytes ([Convert]::FromBase64String($response.content))
    if ($bytes.Length -eq 0) {
        throw "Canonical telemetry hook source '$Path' is empty."
    }

    return ,$bytes
}

function Get-TelemetryHooksSource {
    param([Parameter(Mandatory = $true)][string]$CommitSha, [Parameter(Mandatory = $true)][string]$Version)

    Assert-TelemetryHooksCommitSha $CommitSha
    $null = Get-TelemetryHooksVersion $Version
    $CommitSha = $CommitSha.ToLowerInvariant()
    Assert-TelemetryHooksReleasedCommit $CommitSha
    $pluginBytes = Get-TelemetryHooksSourceContent $CommitSha '.claude-plugin/plugin.json'
    $plugin = ConvertFrom-Json -AsHashtable -NoEnumerate -InputObject ([System.Text.Encoding]::UTF8.GetString($pluginBytes))
    if ($plugin -isnot [System.Collections.IDictionary]) {
        throw 'Invalid canonical plugin version metadata.'
    }
    $null = Get-TelemetryHooksVersion $plugin.version
    if ($plugin.version -cne $Version) {
        throw "Requested version '$Version' does not match canonical plugin version '$($plugin.version)' at '$CommitSha'."
    }

    $contents = [ordered]@{}
    $hashes = [ordered]@{}
    foreach ($name in Get-TelemetryHookFileNames) {
        $contents[$name] = Get-TelemetryHooksSourceContent $CommitSha "hooks/scripts/$name"
        $hashes[$name] = Get-TelemetryHookSha512Hex $contents[$name]
    }

    return @{
        metadata = [ordered]@{
            version = $Version
            repository = $script:TelemetryHooksRepository
            commitSha = $CommitSha
            files = $hashes
        }
        contents = $contents
    }
}

function Assert-TelemetryHooksLocalFiles {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)]$Metadata)

    foreach ($name in Get-TelemetryHookFileNames) {
        $path = Join-Path $Directory $name
        if (-not [System.IO.File]::Exists($path)) {
            throw "Embedded telemetry hook was not found at '$path'."
        }
        $hash = Get-TelemetryHookSha512Hex (ConvertTo-TelemetryHookBytes ([System.IO.File]::ReadAllBytes($path)))
        if ($hash -cne $Metadata.files[$name]) {
            throw "Embedded telemetry hook '$name' SHA-512 mismatch."
        }
    }
}

function Assert-TelemetryHooksSourceMetadata {
    param([Parameter(Mandatory = $true)]$Metadata, [Parameter(Mandatory = $true)]$Source)

    if ($Metadata.version -cne $Source.metadata.version) {
        throw 'Pinned telemetry hook version does not match canonical source.'
    }
    foreach ($name in Get-TelemetryHookFileNames) {
        if ($Metadata.files[$name] -cne $Source.metadata.files[$name]) {
            throw "Telemetry hook '$name' does not match canonical source at '$($Metadata.commitSha)'."
        }
    }
}

function Update-TelemetryHooks {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$SourceCommit,
        [Parameter(Mandatory = $true)][string]$Version,
        [ValidateNotNullOrEmpty()][string]$BaselineMetadataPath
    )

    $metadata = Read-TelemetryHooksMetadata (Join-Path $Directory $script:TelemetryHooksMetadataFileName)
    $baselines = @($metadata)
    if ($PSBoundParameters.ContainsKey('BaselineMetadataPath')) {
        # An open automation PR can hold a newer pin than the tracked checkout. The caller
        # supplies that metadata; direct/manual invocations need only the tracked baseline.
        $baselines += Read-TelemetryHooksMetadata $BaselineMetadataPath
    }
    Assert-TelemetryHooksLocalFiles $Directory $metadata
    Assert-TelemetryHooksCommitSha $SourceCommit
    foreach ($baseline in $baselines) {
        if ((Compare-TelemetryHooksVersion $Version $baseline.version) -lt 0) {
            throw "Telemetry hook version regression from '$($baseline.version)' at '$($baseline.commitSha)' to '$Version' is not allowed."
        }
    }

    $source = Get-TelemetryHooksSource $SourceCommit $Version
    foreach ($baseline in $baselines) {
        $comparison = Get-TelemetryHooksComparison $baseline.commitSha $source.metadata.commitSha
        if ($comparison -cnotin @('identical', 'ahead')) {
            throw "Telemetry hook source '$SourceCommit' is behind or diverged from baseline pin '$($baseline.commitSha)'."
        }

        # Do not silently repair corrupt pins while updating. Reuse the fetched source on replay.
        $previousSource = if ($comparison -ceq 'identical') {
            $source
        }
        else {
            Get-TelemetryHooksSource $baseline.commitSha $baseline.version
        }
        Assert-TelemetryHooksSourceMetadata $baseline $previousSource
    }
    Publish-TelemetryHooks $Directory $source

    Write-Host "Telemetry hooks updated to '$Version' at '$($source.metadata.commitSha)'."
}

function Publish-TelemetryHooks {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)]$Source)

    $files = $Source.contents
    $json = ($Source.metadata | ConvertTo-Json -Depth 4) + "`n"
    $files[$script:TelemetryHooksMetadataFileName] = ConvertTo-TelemetryHookBytes ([System.Text.Encoding]::UTF8.GetBytes($json))
    $changes = @(
        foreach ($name in $files.Keys) {
            $path = Join-Path $Directory $name
            $existing = [System.IO.File]::ReadAllBytes($path)
            if ([Convert]::ToBase64String($existing) -cne [Convert]::ToBase64String($files[$name])) {
                [pscustomobject]@{ Name = $name; Path = $path; LastWriteTimeUtc = [System.IO.File]::GetLastWriteTimeUtc($path) }
            }
        }
    )
    if ($changes.Count -eq 0) {
        return
    }

    # Stage all three outputs on the same filesystem only after every remote/local check.
    # Keep rollback bounded to files actually replaced; metadata is published last.
    $staging = Join-Path $Directory ".telemetry-hooks-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $staging -ErrorAction Stop
    $published = [System.Collections.Generic.List[object]]::new()
    $cleanup = $true
    try {
        foreach ($name in $files.Keys) {
            [System.IO.File]::WriteAllBytes((Join-Path $staging $name), $files[$name])
        }
        foreach ($change in $changes) {
            [System.IO.File]::Copy($change.Path, (Join-Path $staging "$($change.Name).backup"))
        }
        foreach ($change in $changes) {
            [System.IO.File]::Move((Join-Path $staging $change.Name), $change.Path, $true)
            $published.Add($change)
        }
    }
    catch {
        $publicationError = $_
        for ($index = $published.Count - 1; $index -ge 0; $index--) {
            $change = $published[$index]
            try {
                [System.IO.File]::Move((Join-Path $staging "$($change.Name).backup"), $change.Path, $true)
                [System.IO.File]::SetLastWriteTimeUtc($change.Path, $change.LastWriteTimeUtc)
            }
            catch {
                $cleanup = $false
                Write-Warning "Could not restore '$($change.Path)'. Recovery files remain in '$staging': $_"
            }
        }
        throw $publicationError
    }
    finally {
        if ($cleanup) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}
