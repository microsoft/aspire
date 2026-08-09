// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a reference to a secret in an Azure Key Vault resource.
/// </summary>
/// <param name="secretName">The name of the secret.</param>
/// <param name="azureKeyVaultResource">The Azure Key Vault resource.</param>
internal sealed class AzureKeyVaultSecretReference(string secretName, AzureKeyVaultResource azureKeyVaultResource) : IAzureKeyVaultSecretReference
{
    /// <summary>
    /// Gets the name of the secret.
    /// </summary>
    public string SecretName => secretName;

    /// <summary>
    /// Gets the Azure Key Vault resource.
    /// </summary>
    public IAzureKeyVaultResource Resource => azureKeyVaultResource;

    /// <summary>
    /// Gets or sets the resource that writes this secret to the Key Vault.
    /// </summary>
    public IResource? SecretOwner { get; set; }

    string IManifestExpressionProvider.ValueExpression => $"{{{azureKeyVaultResource.Name}.secrets.{SecretName}}}";

    async ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
    {
        if (azureKeyVaultResource.SecretResolver is { } secretResolver)
        {
            return await secretResolver(this, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Secret '{secretName}' not found in Key Vault '{azureKeyVaultResource.Name}'.");
    }
}
