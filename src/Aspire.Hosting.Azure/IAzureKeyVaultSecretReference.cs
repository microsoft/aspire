// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a reference to a secret in an Azure Key Vault resource.
/// </summary>
[AspireExport]
public interface IAzureKeyVaultSecretReference : IValueProvider, IManifestExpressionProvider, IValueWithReferences
{
    /// <summary>
    /// Gets the name of the secret.
    /// </summary>
    string SecretName { get; }

    /// <summary>
    /// Gets the Azure Key Vault resource.
    /// </summary>
    IAzureKeyVaultResource Resource { get; }

    /// <summary>
    /// Gets or sets the resource that writes this secret to the Key Vault.
    /// </summary>
    /// <value>
    /// The <see cref="IResource"/> that is responsible for writing this secret to the Key Vault, or <c>null</c> if not set.
    /// </value>
    /// <remarks>
    /// Implementers must provide both a getter and setter for this property. If not implemented, attempts to set <see cref="SecretOwner"/> will throw an exception.
    /// </remarks>
    public IResource? SecretOwner
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    IEnumerable<object> IValueWithReferences.References => SecretOwner is null ? [Resource] : [Resource, SecretOwner];
}
