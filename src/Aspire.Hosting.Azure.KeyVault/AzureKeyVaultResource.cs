// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents an Azure Key Vault.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
public class AzureKeyVaultResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithEndpoints, IResourceWithConnectionString, IAzureKeyVaultResource
{
    /// <summary>
    /// The secrets for this Key Vault.
    /// </summary>
    internal List<AzureKeyVaultSecretResource> Secrets { get; } = [];
    /// <summary>
    /// Gets the "vaultUri" output reference for the Azure Key Vault resource.
    /// </summary>
    public BicepOutputReference VaultUri => new("vaultUri", this);

    /// <summary>
    /// Gets the "name" output reference for the Azure Key Vault resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets a value indicating whether the Azure Key Vault resource is running in the local emulator.
    /// </summary>
    public bool IsEmulator => this.IsContainer();

    internal EndpointReference EmulatorEndpoint => new(this, "https");

    /// <summary>
    /// Gets the endpoint URI expression for the Key Vault resource.
    /// </summary>
    /// <remarks>
    /// In container mode (emulator), resolves to the container's HTTPS endpoint URL.
    /// In Azure mode, resolves to the Azure Key Vault URI.
    /// Format: <c>https://{name}.vault.azure.net/</c>.
    /// </remarks>
    public ReferenceExpression Endpoint =>
        IsEmulator ?
            ReferenceExpression.Create($"{EmulatorEndpoint.Property(EndpointProperty.Url)}") :
            ReferenceExpression.Create($"{VaultUri}");

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure Key Vault resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            if (this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation))
            {
                return connectionStringAnnotation.Resource.ConnectionStringExpression;
            }

            return IsEmulator ?
                ReferenceExpression.Create($"{EmulatorEndpoint.Property(EndpointProperty.Url)}") :
                ReferenceExpression.Create($"{VaultUri}");
        }
    }

    /// <summary>
    /// Gets the connection string for the Azure Key Vault resource.
    /// </summary>
    /// <param name="cancellationToken"> A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>The connection string for the Azure Key Vault resource.</returns>
    public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        if (this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation))
        {
            return connectionStringAnnotation.Resource.GetConnectionStringAsync(cancellationToken);
        }

        return ConnectionStringExpression.GetValueAsync(cancellationToken);
    }

    BicepOutputReference IAzureKeyVaultResource.VaultUriOutputReference => VaultUri;

    // In run mode, this is set to the secret client used to access the Azure Key Vault.
    internal Func<IAzureKeyVaultSecretReference, CancellationToken, Task<string?>>? SecretResolver { get; set; }

    Func<IAzureKeyVaultSecretReference, CancellationToken, Task<string?>>? IAzureKeyVaultResource.SecretResolver
    {
        get => SecretResolver;
        set => SecretResolver = value;
    }

    /// <summary>
    /// Gets a secret reference for the specified secret name.
    /// </summary>
    /// <param name="secretName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public IAzureKeyVaultSecretReference GetSecret(string secretName)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretName, nameof(secretName));

        return new AzureKeyVaultSecretReference(secretName, this);
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a KeyVaultService with the same identifier already exists
        var existingStore = resources.OfType<KeyVaultService>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);

        if (existingStore is not null)
        {
            return existingStore;
        }

        // Create and add new resource if it doesn't exist
        var store = KeyVaultService.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            store))
        {
            store.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(store);
        return store;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", Endpoint);
    }
}
