# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$BuildId,
    [Parameter(Mandatory)]
    [ValidateSet('homebrew-release-handoff', 'homebrew-cask-stable')][string]$ArtifactName,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:SYSTEM_ACCESSTOKEN)) {
    throw 'System.AccessToken is required to read the selected internal build and artifact.'
}

# Keep the bearer token on the known internal collection. Do not retain artifact
# download URLs/tickets; only these provenance fields belong in diagnostics.
$root = "https://dev.azure.com/dnceng/internal/_apis/build/builds/$BuildId"
$headers = @{ Authorization = "Bearer $env:SYSTEM_ACCESSTOKEN" }
$request = @{
    Headers = $headers; ConnectionTimeoutSeconds = 30; OperationTimeoutSeconds = 30
    MaximumRetryCount = 2; RetryIntervalSec = 2; MaximumRedirection = 0
}
$build = Invoke-RestMethod -Uri "${root}?api-version=7.1" @request
$artifact = Invoke-RestMethod `
    -Uri "${root}/artifacts?artifactName=$ArtifactName&api-version=7.1" @request
$metadata = @{
    build = @{
        id = $build.id; definition = @{ id = $build.definition.id }
        project = @{ name = $build.project.name }; repository = @{ url = $build.repository.url }
        sourceVersion = $build.sourceVersion; sourceBranch = $build.sourceBranch
        status = $build.status; result = $build.result; tags = @($build.tags)
    }
    artifact = @{
        id = $artifact.id; name = $artifact.name
        resource = @{ type = $artifact.resource.type; data = $artifact.resource.data }
    }
}
$metadata | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -Path $OutputPath
Write-Host "Selected run $BuildId, commit $($build.sourceVersion), artifact $($artifact.id) ($ArtifactName)."
