param(
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$StoreLocation = 'CurrentUser',

    [string]$DotNetPath = 'dotnet',

    [string]$OutputDirectory = (Join-Path (Get-Location) 'artifacts/dev-cert-trust-experiment'),

    [switch]$Clean,

    [switch]$KeepCertificate
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows))
{
    throw 'This script can only run on Windows because it uses the Windows certificate store.'
}

if (-not (Get-Command Import-Certificate -ErrorAction SilentlyContinue))
{
    Import-Module PKI -ErrorAction Stop
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-dev-cert-trust-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null

$resultPath = Join-Path $OutputDirectory "result-$StoreLocation.json"
$targetStorePath = "Cert:\$StoreLocation\Root"
$storeLocationEnum = if ($StoreLocation -eq 'CurrentUser')
{
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
}
else
{
    [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
}
$failure = $null
$certificate = $null

$result = [ordered]@{
    TimestampUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
    StoreLocation = $StoreLocation
    TargetStorePath = $targetStorePath
    DotNetPath = $DotNetPath
    CleanRequested = [bool]$Clean
    KeepCertificate = [bool]$KeepCertificate
    ResultPath = $resultPath
}

function Invoke-DotNetDevCerts
{
    param(
        [string]$Label,
        [string[]]$Arguments
    )

    Write-Host "Running dotnet dev-certs command: $Label"
    $output = & $DotNetPath @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $exitCode = $LASTEXITCODE

    return [pscustomobject][ordered]@{
        ExitCode = $exitCode
        Output = @($output)
    }
}

function Test-CertificateInStore
{
    param(
        [System.Security.Cryptography.X509Certificates.StoreName]$StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]$StoreLocationValue,
        [string]$Thumbprint
    )

    $normalizedThumbprint = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $StoreLocationValue)

    try
    {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)

        foreach ($storeCertificate in $store.Certificates)
        {
            if ($storeCertificate.Thumbprint -and $storeCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant() -eq $normalizedThumbprint)
            {
                return $true
            }
        }

        return $false
    }
    finally
    {
        $store.Dispose()
    }
}

function Remove-CertificateFromStore
{
    param(
        [System.Security.Cryptography.X509Certificates.StoreName]$StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]$StoreLocationValue,
        [string]$Thumbprint
    )

    $normalizedThumbprint = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $StoreLocationValue)

    try
    {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $matches = @(
            foreach ($storeCertificate in $store.Certificates)
            {
                if ($storeCertificate.Thumbprint -and $storeCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant() -eq $normalizedThumbprint)
                {
                    $storeCertificate
                }
            }
        )

        foreach ($match in $matches)
        {
            $store.Remove($match)
        }

        return $matches.Count
    }
    finally
    {
        $store.Dispose()
    }
}

try
{
    $pfxPath = Join-Path $tempDirectory 'aspnetcore-dev-cert.pfx'
    $cerPath = Join-Path $tempDirectory 'aspnetcore-dev-cert.cer'
    $password = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

    if ($Clean)
    {
        $result['DotNetCleanBeforeExport'] = Invoke-DotNetDevCerts -Label 'https --clean' -Arguments @('dev-certs', 'https', '--clean')
    }

    $result['DotNetExport'] = Invoke-DotNetDevCerts -Label 'https --export-path <temp-pfx> --password <redacted> --format Pfx' -Arguments @('dev-certs', 'https', '--export-path', $pfxPath, '--password', $password, '--format', 'Pfx')
    if ($result['DotNetExport'].ExitCode -ne 0)
    {
        throw "Failed to export the ASP.NET Core HTTPS development certificate. Exit code: $($result['DotNetExport'].ExitCode)"
    }

    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $pfxPath,
        $password,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)

    $thumbprint = $certificate.Thumbprint
    $result['CertificateSubject'] = $certificate.Subject
    $result['CertificateThumbprint'] = $thumbprint
    $result['CertificateNotBefore'] = $certificate.NotBefore.ToUniversalTime().ToString('O')
    $result['CertificateNotAfter'] = $certificate.NotAfter.ToUniversalTime().ToString('O')
    $result['CertificateHasPrivateKey'] = $certificate.HasPrivateKey

    [System.IO.File]::WriteAllBytes($cerPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    $result['CurrentUserMyContainsCertificateAfterExport'] = Test-CertificateInStore -StoreName My -StoreLocationValue ([System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser) -Thumbprint $thumbprint
    $result['CurrentUserRootContainsCertificateBeforeImport'] = Test-CertificateInStore -StoreName Root -StoreLocationValue ([System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser) -Thumbprint $thumbprint
    $result['LocalMachineRootContainsCertificateBeforeImport'] = Test-CertificateInStore -StoreName Root -StoreLocationValue ([System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine) -Thumbprint $thumbprint
    $result['TargetRootContainsCertificateBeforeImport'] = Test-CertificateInStore -StoreName Root -StoreLocationValue $storeLocationEnum -Thumbprint $thumbprint

    Write-Host "Importing certificate $thumbprint into $targetStorePath"
    $importedCertificates = @(Import-Certificate -FilePath $cerPath -CertStoreLocation $targetStorePath -ErrorAction Stop)
    $result['ImportedCertificateThumbprints'] = @($importedCertificates | ForEach-Object { $_.Thumbprint })

    $result['CurrentUserRootContainsCertificateAfterImport'] = Test-CertificateInStore -StoreName Root -StoreLocationValue ([System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser) -Thumbprint $thumbprint
    $result['LocalMachineRootContainsCertificateAfterImport'] = Test-CertificateInStore -StoreName Root -StoreLocationValue ([System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine) -Thumbprint $thumbprint
    $result['TargetRootContainsCertificateAfterImport'] = Test-CertificateInStore -StoreName Root -StoreLocationValue $storeLocationEnum -Thumbprint $thumbprint

    if (-not $result['TargetRootContainsCertificateAfterImport'])
    {
        throw "Import-Certificate completed, but certificate $thumbprint was not found in $targetStorePath."
    }

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try
    {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $result['X509ChainBuildResult'] = $chain.Build($certificate)
        $result['X509ChainStatus'] = @(
            foreach ($status in $chain.ChainStatus)
            {
                [ordered]@{
                    Status = $status.Status.ToString()
                    StatusInformation = $status.StatusInformation
                }
            }
        )
    }
    finally
    {
        $chain.Dispose()
    }

    $result['DotNetCheckTrust'] = Invoke-DotNetDevCerts -Label 'https --check --trust' -Arguments @('dev-certs', 'https', '--check', '--trust')
    $result['DotNetCheckTrustMachineReadable'] = Invoke-DotNetDevCerts -Label 'https --check-trust-machine-readable' -Arguments @('dev-certs', 'https', '--check-trust-machine-readable')
    $result['AspireCurrentWindowsDetectionWouldTrustCertificate'] = [bool]($result['X509ChainBuildResult'] -and $result['CurrentUserRootContainsCertificateAfterImport'])
}
catch
{
    $failure = $_
    $result['Error'] = $_.Exception.Message
    $result['ErrorType'] = $_.Exception.GetType().FullName
}
finally
{
    if ($certificate)
    {
        $certificate.Dispose()
    }

    if ($result['CertificateThumbprint'])
    {
        $result['CleanupAttempted'] = -not [bool]$KeepCertificate
        if ($KeepCertificate)
        {
            $result['CleanupSkippedReason'] = 'KeepCertificate was specified.'
        }
        elseif ($result['TargetRootContainsCertificateBeforeImport'])
        {
            $result['CleanupSkippedReason'] = "Certificate existed in $targetStorePath before this script imported it."
        }
        else
        {
            try
            {
                $result['RemovedRootCertificateCount'] = Remove-CertificateFromStore -StoreName Root -StoreLocationValue $storeLocationEnum -Thumbprint $result['CertificateThumbprint']
                $result['TargetRootContainsCertificateAfterCleanup'] = Test-CertificateInStore -StoreName Root -StoreLocationValue $storeLocationEnum -Thumbprint $result['CertificateThumbprint']

                if ($result['TargetRootContainsCertificateAfterCleanup'])
                {
                    throw "Certificate $($result['CertificateThumbprint']) is still present in $targetStorePath after cleanup."
                }
            }
            catch
            {
                $result['CleanupError'] = $_.Exception.Message
                if (-not $failure)
                {
                    $failure = $_
                }
            }
        }
    }

    if (Test-Path $tempDirectory)
    {
        Remove-Item -Path $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    $result | ConvertTo-Json -Depth 8 | Set-Content -Path $resultPath -Encoding UTF8
    Write-Host "Wrote dev-cert trust experiment result to $resultPath"
}

if ($failure)
{
    throw $failure
}

if (-not $result['TargetRootContainsCertificateAfterImport'])
{
    throw "Certificate was not imported into $targetStorePath."
}

Write-Host "Import-Certificate trust probe for $StoreLocation completed."
