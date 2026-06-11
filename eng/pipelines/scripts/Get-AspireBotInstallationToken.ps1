# Mints a GitHub App installation access token for a target repository.
#
# This is the auth half of every aspire-repo-bot pipeline interaction:
# the JWT mint, installation-id lookup, and token exchange flow described at
#   https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app
#
# Both dispatch-release-github-tasks.ps1 and publish-release-cli-assets.ps1
# call this so the JWT / token logic lives in exactly one place.
#
# Flow:
#   1. Sign a JWT (RS256) with the App's private key. iss=<AppId>, iat=now-60s, exp=now+540s.
#   2. GET  /repos/{owner}/{repo}/installation                  → installation id
#   3. POST /app/installations/{installationId}/access_tokens   → installation access token (~1h)
#
# The script writes the token to stdout as a single line so callers can
# capture it (e.g. `$token = & .\Get-AspireBotInstallationToken.ps1 -AppId ... -PrivateKeyPem ... -Owner ... -Repo ...`).
# All diagnostic output is sent to the information stream so it doesn't
# contaminate the token on stdout.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppId,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPem,
    [Parameter(Mandatory = $true)][string]$Owner,
    [Parameter(Mandatory = $true)][string]$Repo
)

$ErrorActionPreference = 'Stop'

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    # Base64url per RFC 7515 §2: standard Base64 with '+'->'-', '/'->'_', no '=' padding.
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-GitHubAppJwt {
    param([string]$AppId, [string]$PrivateKeyPem)

    # GitHub requires RS256 JWTs. iat may be backdated up to 60s to tolerate clock skew;
    # exp must be <=10 minutes from iat. We use 9 minutes to stay safely under the cap.
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $header = @{ alg = 'RS256'; typ = 'JWT' } | ConvertTo-Json -Compress
    $payload = @{ iat = $now - 60; exp = $now + 540; iss = $AppId } | ConvertTo-Json -Compress

    $headerB64 = ConvertTo-Base64Url -Bytes ([Text.Encoding]::UTF8.GetBytes($header))
    $payloadB64 = ConvertTo-Base64Url -Bytes ([Text.Encoding]::UTF8.GetBytes($payload))
    $signingInput = "$headerB64.$payloadB64"

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        # ImportFromPem handles both PKCS#1 ("BEGIN RSA PRIVATE KEY") and PKCS#8
        # ("BEGIN PRIVATE KEY") PEMs. GitHub Apps emit PKCS#1 by default.
        $rsa.ImportFromPem($PrivateKeyPem.ToCharArray())
        $sigBytes = $rsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($signingInput),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    }
    finally {
        $rsa.Dispose()
    }

    $sigB64 = ConvertTo-Base64Url -Bytes $sigBytes
    return "$signingInput.$sigB64"
}

function Invoke-GitHubApi {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$Token,
        [object]$Body
    )

    $headers = @{
        Authorization          = "Bearer $Token"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'aspire-release-pipeline'
    }

    $params = @{
        Method  = $Method
        Uri     = $Uri
        Headers = $headers
    }

    if ($null -ne $Body) {
        $params['Body'] = ($Body | ConvertTo-Json -Depth 8 -Compress)
        $params['ContentType'] = 'application/json'
    }

    return Invoke-RestMethod @params
}

Write-Information "Minting GitHub App JWT for app id $AppId..." -InformationAction Continue

$jwt = New-GitHubAppJwt -AppId $AppId -PrivateKeyPem $PrivateKeyPem

Write-Information "Looking up installation id for $Owner/$Repo..." -InformationAction Continue
$installation = Invoke-GitHubApi -Method GET -Uri "https://api.github.com/repos/$Owner/$Repo/installation" -Token $jwt
$installationId = $installation.id
Write-Information "Installation id: $installationId" -InformationAction Continue

Write-Information "Exchanging JWT for installation access token..." -InformationAction Continue
$tokenResp = Invoke-GitHubApi -Method POST -Uri "https://api.github.com/app/installations/$installationId/access_tokens" -Token $jwt
Write-Information "Installation token acquired (expires $($tokenResp.expires_at))." -InformationAction Continue

# Token is the only thing on stdout — callers capture this and use it for
# subsequent GitHub API calls.
$tokenResp.token
