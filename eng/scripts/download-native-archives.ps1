#requires -Version 7.0
<#
.SYNOPSIS
    Downloads and unpacks AzDO build artifacts matching a name pattern, in parallel.

.DESCRIPTION
    Replaces the dual DownloadPipelineArtifact@2 pattern used in the Aspire assemble
    stage for native_archives_<rid> artifacts. The default behavior of
    DownloadPipelineArtifact@2 (with no `artifact:` filter and only `itemPattern:`)
    serializes both downloads and re-downloads each artifact zip once per pattern
    slice. For 7 native_archives_* artifacts that adds ~100 s wasted on the
    Assemble critical path.

    This script fetches each artifact's zip ONCE over its container `downloadUrl`
    to a temp file, opens it with [System.IO.Compression.ZipFile]::OpenRead, and
    extracts only the entries matching the archive / nupkg / npm-tgz shape to
    the configured target directories. Downloads run in parallel via
    Start-ThreadJob.

    Layout on disk after a successful run:
        <ArchivesTargetDir>/<artifactName>/<inner path>/aspire-cli-*.{zip,tar.gz}
        <NupkgsTargetDir>/<artifactName>/<inner path>/Aspire.Cli*.nupkg
        <NupkgsTargetDir>/<artifactName>/<inner path>/microsoft-aspire-cli*.tgz

    The <artifactName> path component is preserved so that
    stage-native-cli-tool-packages.ps1 (which the assemble job runs next) sees
    the same `native_archives_*` directory structure it would get from a normal
    DownloadPipelineArtifact@2 call.

.PARAMETER CollectionUri
    AzDO organization URL, e.g. `https://dev.azure.com/dnceng/`. Pass
    `$(System.CollectionUri)` from pipeline YAML.

.PARAMETER Project
    AzDO project name, e.g. `internal`. Pass `$(System.TeamProject)`.

.PARAMETER BuildId
    AzDO build ID whose artifacts will be enumerated. Pass `$(Build.BuildId)`.

.PARAMETER AccessToken
    OAuth bearer token for the AzDO REST API. Falls back to
    $env:SYSTEM_ACCESSTOKEN. The pipeline YAML must explicitly pass
    `SYSTEM_ACCESSTOKEN: $(System.AccessToken)` in the task's env: block to
    expose the token to the script.

.PARAMETER ArchivesTargetDir
    Destination directory for aspire-cli-*.zip and aspire-cli-*.tar.gz files.
    Created if it does not already exist.

.PARAMETER NupkgsTargetDir
    Destination directory for Aspire.Cli*.nupkg AND microsoft-aspire-cli*.tgz
    files. Created if it does not already exist. (Both land here because
    stage-native-cli-tool-packages.ps1 then walks this directory for both
    shapes when called with -RequireNpmPackages.)

.PARAMETER ArtifactNamePattern
    Wildcard for selecting artifacts. Defaults to `native_archives_*`.

.PARAMETER ThrottleLimit
    Maximum concurrent download jobs. Defaults to 10.

.PARAMETER ApiVersion
    AzDO REST API version. Defaults to `7.1`.

.EXAMPLE
    pwsh ./download-native-archives.ps1 `
        -CollectionUri 'https://dev.azure.com/dnceng/' `
        -Project 'internal' `
        -BuildId 2987730 `
        -ArchivesTargetDir 'artifacts/signed-archives/Release' `
        -NupkgsTargetDir 'artifacts/native-cli-packages/Release'

.NOTES
    Only Build/Container artifacts (the type produced by
    1ES.PublishBuildArtifacts@1) can be fetched via their `downloadUrl`. Pipeline
    artifacts (1ES.PublishPipelineArtifact@1) require DownloadPipelineArtifact@2
    and are out of scope here.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CollectionUri,

    [Parameter(Mandatory)]
    [string]$Project,

    [Parameter(Mandatory)]
    [string]$BuildId,

    [string]$AccessToken,

    [Parameter(Mandatory)]
    [string]$ArchivesTargetDir,

    [Parameter(Mandatory)]
    [string]$NupkgsTargetDir,

    [string]$ArtifactNamePattern = 'native_archives_*',

    [ValidateRange(1, 64)]
    [int]$ThrottleLimit = 10,

    [string]$ApiVersion = '7.1'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Fallback to the AzDO-conventional env var if no token was passed explicitly.
if (-not $AccessToken) {
    $AccessToken = $env:SYSTEM_ACCESSTOKEN
}
if (-not $AccessToken) {
    throw 'AccessToken parameter not set and SYSTEM_ACCESSTOKEN env var is empty. In pipeline YAML, pass `SYSTEM_ACCESSTOKEN: $(System.AccessToken)` in the task env: block.'
}

function Get-MatchingArtifacts {
    <#
    Lists build artifacts and filters down to ones whose name matches the
    wildcard pattern. Throws if zero match (empty result almost always means a
    misconfigured pattern or an upstream stage that did not publish what we
    expected to assemble).
    #>
    param(
        [Parameter(Mandatory)] [string]$CollectionUri,
        [Parameter(Mandatory)] [string]$Project,
        [Parameter(Mandatory)] [string]$BuildId,
        [Parameter(Mandatory)] [string]$AccessToken,
        [Parameter(Mandatory)] [string]$Pattern,
        [Parameter(Mandatory)] [string]$ApiVersion
    )

    $org = $CollectionUri.TrimEnd('/')
    $listUrl = "$org/$Project/_apis/build/builds/$BuildId/artifacts?api-version=$ApiVersion"
    Write-Host "Listing artifacts: $listUrl"

    try {
        $response = Invoke-RestMethod -Uri $listUrl -Headers @{ Authorization = "Bearer $AccessToken" } -Method Get
    }
    catch {
        # Construct the throw message from $_.Exception.Message + status code only,
        # not the full ErrorRecord. The ErrorRecord's string form can include the
        # request's Authorization header on some Invoke-RestMethod failure modes
        # (notably proxied 4xx with a body). AzDO log scrubbing masks
        # $(System.AccessToken), but the -AccessToken parameter is also designed
        # for non-AzDO use where nothing scrubs.
        $status = $null
        try { $status = $_.Exception.Response.StatusCode } catch { }
        $statusPart = if ($null -ne $status) { " (HTTP $status)" } else { "" }
        throw "Failed to list build artifacts from $listUrl$statusPart`: $($_.Exception.Message)"
    }

    $all = @($response.value)
    $matching = @($all | Where-Object { $_.name -like $Pattern })
    if ($matching.Count -eq 0) {
        $allNames = if ($all.Count -gt 0) { ($all.name | Sort-Object) -join ', ' } else { '(none)' }
        throw "No build artifacts on build $BuildId matched '$Pattern'. Available: $allNames"
    }

    Write-Host "Discovered $($matching.Count) artifact(s) matching '$Pattern':"
    foreach ($a in $matching) {
        Write-Host ("  - {0,-32} type={1}" -f $a.name, $a.resource.type)
    }

    # All Container-type so we can hit `downloadUrl` directly with Bearer auth.
    # If 1ES PT switches to a different storage backend, surface that with
    # context rather than letting Invoke-WebRequest fail with an opaque 404.
    $bad = @($matching | Where-Object { $_.resource.type -ne 'Container' })
    if ($bad.Count -gt 0) {
        $detail = ($bad | ForEach-Object { "$($_.name)=$($_.resource.type)" }) -join ', '
        throw "Expected all '$Pattern' artifacts to be 'Container' type for raw HTTP download. Got: $detail"
    }

    return $matching
}

# Run as a Start-ThreadJob ScriptBlock. Lives in this script for visibility
# (Start-ThreadJob receives this as -ScriptBlock arg) but defined here so
# the structure of the script reads top-down.
$DownloadOneArtifact = {
    param(
        [string]$AccessToken,
        [string]$ArtifactName,
        [string]$DownloadUrl,
        [string]$ArchivesTargetDir,
        [string]$NupkgsTargetDir
    )

    Set-StrictMode -Version 3.0
    $ErrorActionPreference = 'Stop'

    $tempBase = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
    $tempZip = Join-Path $tempBase "$ArtifactName.zip"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $tempZip `
            -Headers @{ Authorization = "Bearer $AccessToken" } -UseBasicParsing
    }
    catch {
        # Preserve $tempZip on failure (do NOT remove it) so a developer can
        # inspect partial download contents. See Get-MatchingArtifacts above
        # for why the throw message is constructed from $_.Exception.Message
        # rather than the full ErrorRecord (token-leak hardening).
        $status = $null
        try { $status = $_.Exception.Response.StatusCode } catch { }
        $statusPart = if ($null -ne $status) { " (HTTP $status)" } else { "" }
        throw "Download failed for '$ArtifactName' from $DownloadUrl$statusPart. Temp file (may be partial): $tempZip. $($_.Exception.Message)"
    }
    $downloadMs = $sw.ElapsedMilliseconds

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($tempZip)
    }
    catch {
        throw "Could not open downloaded zip for '$ArtifactName' at $tempZip. The file may be corrupt or not a zip. $_"
    }

    $archivesCount = 0
    $nupkgsCount = 0
    try {
        foreach ($entry in $zip.Entries) {
            # Skip directory entries (empty Name).
            if (-not $entry.Name) { continue }

            $isArchive = ($entry.Name -like 'aspire-cli-*.zip') -or ($entry.Name -like 'aspire-cli-*.tar.gz')
            $isNupkg = ($entry.Name -like 'Aspire.Cli*.nupkg') -or ($entry.Name -like 'microsoft-aspire-cli*.tgz')
            if (-not ($isArchive -or $isNupkg)) { continue }

            $target = if ($isArchive) { $ArchivesTargetDir } else { $NupkgsTargetDir }

            # Preserve the original layout: <target>/<artifactName>/<inner path>.
            # stage-native-cli-tool-packages.ps1 walks for `native_archives_*`
            # path components when classifying packages by RID.
            $dest = Join-Path $target (Join-Path $ArtifactName $entry.FullName)

            # Zip-slip defense: reject any entry whose canonicalized destination
            # is not under the canonical staging root. Today's callers point at
            # trusted same-pipeline `native_archives_*` artifacts, but this
            # script's params (-ArtifactNamePattern, -CollectionUri, -BuildId)
            # are deliberately general, so a future caller pointed at a less
            # trusted build could otherwise be coerced into writing outside the
            # staging dir under the agent identity.
            $artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $target $ArtifactName))
            $resolvedDest = [System.IO.Path]::GetFullPath($dest)
            $rootWithSep = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
            if (-not $resolvedDest.StartsWith($rootWithSep, [System.StringComparison]::Ordinal)) {
                throw "Refusing to extract '$($entry.FullName)' from artifact '$ArtifactName': resolves to '$resolvedDest', which is outside '$artifactRoot' (zip-slip)."
            }

            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) {
                New-Item -ItemType Directory -Force -Path $destDir | Out-Null
            }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)

            if ($isArchive) { $archivesCount++ } else { $nupkgsCount++ }
        }
    }
    finally {
        $zip.Dispose()
    }

    if (($archivesCount + $nupkgsCount) -eq 0) {
        # Zero matches isn't necessarily an error (an artifact might be all
        # symbols or sdl analysis files), but log so devs investigating an
        # empty-asset-manifest failure can see exactly what each artifact
        # contributed.
        Write-Warning "Artifact '$ArtifactName' had no entries matching aspire-cli-*, Aspire.Cli*.nupkg, or microsoft-aspire-cli*.tgz shapes. Inspect $tempZip if this was unexpected."
    }
    else {
        # Only remove the temp zip when at least something was extracted —
        # keep it around for inspection if the artifact looked empty.
        Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
    }

    $sw.Stop()
    return [pscustomobject]@{
        ArtifactName = $ArtifactName
        DownloadMs   = $downloadMs
        TotalMs      = $sw.ElapsedMilliseconds
        Archives     = $archivesCount
        Nupkgs       = $nupkgsCount
    }
}

function Invoke-ParallelDownloads {
    <#
    Fans the per-artifact download work out across Start-ThreadJob threads
    (PS7's in-process equivalent of Start-Job, ~10x cheaper to spin up).
    Each completed job's stdout is emitted under its artifact name and any
    failures are collected so the script can report them all and exit
    non-zero — partial success across artifacts is treated as failure.
    #>
    param(
        [Parameter(Mandatory)] [array]$Artifacts,
        [Parameter(Mandatory)] [string]$AccessToken,
        [Parameter(Mandatory)] [string]$ArchivesTargetDir,
        [Parameter(Mandatory)] [string]$NupkgsTargetDir,
        [Parameter(Mandatory)] [scriptblock]$Worker,
        [Parameter(Mandatory)] [int]$ThrottleLimit
    )

    # The cmdlet shipped under the bare name `ThreadJob` in PS 7.0-7.3 and was
    # renamed to `Microsoft.PowerShell.ThreadJob` (as a bundled module) in PS
    # 7.4+. Try the new name first, then the old name. Both names ship with
    # PowerShell 7 on every AzDO image we run on, so missing both signals a
    # broken / locked-down image rather than something a runtime PSGallery
    # install would paper over — and on 1ES agents without internet egress the
    # install would itself fail with a more confusing error. Fail fast with an
    # actionable message instead.
    #
    # Context: the previous unconditional `Import-Module ThreadJob -ErrorAction
    # Stop` failed on 1es-ubuntu-2204 (PS 7.4) where the module ships under the
    # new name only (build 2988632).
    $threadJobLoaded = $false
    foreach ($candidate in @('Microsoft.PowerShell.ThreadJob', 'ThreadJob')) {
        if (Get-Module -ListAvailable -Name $candidate -ErrorAction SilentlyContinue) {
            Import-Module $candidate -ErrorAction Stop
            Write-Host "Loaded ThreadJob module: $candidate"
            $threadJobLoaded = $true
            break
        }
    }
    if (-not $threadJobLoaded) {
        throw "Neither 'Microsoft.PowerShell.ThreadJob' (PS 7.4+) nor 'ThreadJob' (PS 7.0-7.3) is available on PSModulePath. Both ship with PowerShell 7; if neither is present the agent image is missing a built-in module. PowerShell version: $($PSVersionTable.PSVersion). PSModulePath: $env:PSModulePath"
    }

    $startedAt = Get-Date
    # Wrap in @() so $jobs is always an array — Start-ThreadJob returns a bare
    # Job object on a single-artifact run, and `$jobs.Count` then fails under
    # Set-StrictMode -Version 3.0 because Job objects have no Count member.
    $jobs = @(foreach ($a in $Artifacts) {
        Start-ThreadJob `
            -ScriptBlock $Worker `
            -ArgumentList @($AccessToken, $a.name, $a.resource.downloadUrl, $ArchivesTargetDir, $NupkgsTargetDir) `
            -ThrottleLimit $ThrottleLimit `
            -Name $a.name
    })
    Write-Host "Started $($jobs.Count) parallel download job(s) (throttle=$ThrottleLimit)..."
    $jobs | Wait-Job | Out-Null

    $failures = @()
    foreach ($job in $jobs) {
        try {
            $result = $job | Receive-Job -ErrorAction Stop
            Write-Host ("  {0,-32} download={1,7:N0}ms total={2,7:N0}ms archives={3} nupkgs={4}" `
                -f $result.ArtifactName, $result.DownloadMs, $result.TotalMs, $result.Archives, $result.Nupkgs)
        }
        catch {
            $failures += [pscustomobject]@{ Name = $job.Name; Error = $_ }
            Write-Host "##[error]Job '$($job.Name)' failed: $_"
        }
    }
    $jobs | Remove-Job

    $elapsed = (Get-Date) - $startedAt
    Write-Host "All downloads complete in $([math]::Round($elapsed.TotalSeconds, 1))s"

    if ($failures.Count -gt 0) {
        $names = ($failures.Name | Sort-Object) -join ', '
        throw "Failed downloads: $names. See per-job error messages above."
    }
}

# --- Main ---

New-Item -ItemType Directory -Force -Path $ArchivesTargetDir, $NupkgsTargetDir | Out-Null

$artifacts = Get-MatchingArtifacts `
    -CollectionUri $CollectionUri `
    -Project $Project `
    -BuildId $BuildId `
    -AccessToken $AccessToken `
    -Pattern $ArtifactNamePattern `
    -ApiVersion $ApiVersion

Invoke-ParallelDownloads `
    -Artifacts $artifacts `
    -AccessToken $AccessToken `
    -ArchivesTargetDir $ArchivesTargetDir `
    -NupkgsTargetDir $NupkgsTargetDir `
    -Worker $DownloadOneArtifact `
    -ThrottleLimit $ThrottleLimit
