// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a resource that represents an Azure Key Vault.
/// </summary>
[AspireExport]
public interface IAzureKeyVaultResource : IResource, IAzureResource
{
    /// <summary>
    /// Gets the output reference that represents the vault uri for the Azure Key Vault resource.
    /// </summary>
    BicepOutputReference VaultUriOutputReference { get; }

    /// <summary>
    /// Gets the output reference that represents the name of Azure Key Vault resource.
    /// </summary>
    BicepOutputReference NameOutputReference { get; }

    /// <summary>
    /// Gets or sets the secret resolver function used to resolve secrets at runtime.
    /// </summary>
    Func<IAzureKeyVaultSecretReference, CancellationToken, Task<string?>>? SecretResolver { get; set; }

    /// <summary>
    /// Gets a secret reference for the specified secret name.
    /// </summary>
    /// <param name="secretName">The name of the secret.</param>
    /// <returns>A reference to the secret.</returns>
    IAzureKeyVaultSecretReference GetSecret(string secretName);
}
