// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using HealthChecks.Azure.KeyVault.Secrets;

namespace Aspire.Azure.Security.KeyVault;

/// <summary>
/// Provides the client configuration settings for connecting to Azure Key Vault.
/// </summary>
public sealed class AzureSecurityKeyVaultSettings : IConnectionStringSettings
{
    /// <summary>
    /// A <see cref="Uri"/> to the vault on which the client operates. Appears as "DNS Name" in the Azure portal.
    /// If you have a secret <see cref="Uri"/>, use <see cref="KeyVaultSecretIdentifier"/> to parse the <see cref="KeyVaultSecretIdentifier.VaultUri"/> and other information.
    /// You should validate that this URI references a valid Key Vault resource. See <see href="https://aka.ms/azsdk/blog/vault-uri"/> for details.
    /// </summary>
    public Uri? VaultUri { get; set; }

    /// <summary>
    /// Gets or sets the credential used to authenticate to the Azure Key Vault.
    /// </summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the Key Vault health check is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableTracing { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="AzureKeyVaultSecretsHealthCheckOptions"/> used to configure the Key Vault health check.
    /// </summary>
    /// <value>
    /// The default value is a new instance of <see cref="AzureKeyVaultSecretsHealthCheckOptions"/>.
    /// </value>
    public AzureKeyVaultSecretsHealthCheckOptions HealthCheckOptions { get; set; } = new();

    void IConnectionStringSettings.ParseConnectionString(string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString) &&
            Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            VaultUri = uri;
        }
    }
}
