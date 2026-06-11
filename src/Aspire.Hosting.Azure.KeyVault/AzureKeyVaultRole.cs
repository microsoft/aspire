// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Key Vault roles.
/// </summary>
internal enum AzureKeyVaultRole
{
    KeyVaultAdministrator,
    KeyVaultCertificateUser,
    KeyVaultCertificatesOfficer,
    KeyVaultContributor,
    KeyVaultCryptoOfficer,
    KeyVaultCryptoServiceEncryptionUser,
    KeyVaultCryptoServiceReleaseUser,
    KeyVaultCryptoUser,
    KeyVaultDataAccessAdministrator,
    KeyVaultReader,
    KeyVaultSecretsOfficer,
    KeyVaultSecretsUser,
    ManagedHsmContributor,
}
