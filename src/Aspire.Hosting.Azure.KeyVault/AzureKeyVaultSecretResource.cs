// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Key Vault Secret.
/// Initializes a new instance of the <see cref="AzureKeyVaultSecretResource"/> class.
/// </summary>
/// <remarks>
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/> to configure specific <see cref="Azure.Provisioning"/> properties.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, Secret = {SecretName}")]
public class AzureKeyVaultSecretResource(string name, string secretName, AzureKeyVaultResource parent, object value)
    : Resource(name), IResourceWithParent<AzureKeyVaultResource>, IAzureKeyVaultSecretReference
{
    private readonly IAzureKeyVaultSecretReference _secret = parent.GetSecret(secretName);

    /// <summary>
    /// Gets or sets the secret name.
    /// </summary>
    public string SecretName => _secret.SecretName;

    /// <summary>
    /// Gets the parent Azure Key Vault resource.
    /// </summary>
    public AzureKeyVaultResource Parent { get; } = parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Gets the value provider for the secret.
    /// </summary>
    public object Value { get; } = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>
    /// Gets the Azure Key Vault resource that contains this secret.
    /// </summary>
    IAzureKeyVaultResource IAzureKeyVaultSecretReference.Resource => Parent;

    IResource? IAzureKeyVaultSecretReference.SecretOwner { get; set; }

    /// <summary>
    /// Gets the expression for the secret value in the manifest.
    /// </summary>
    string IManifestExpressionProvider.ValueExpression => _secret.ValueExpression;

    /// <summary>
    /// Gets the secret value asynchronously.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The secret value.</returns>
    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken) =>
        _secret.GetValueAsync(cancellationToken);
}
