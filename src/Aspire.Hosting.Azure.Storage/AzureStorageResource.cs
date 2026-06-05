// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Storage resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
public class AzureStorageResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithEndpoints, IResourceWithAzureFunctionsConfig
{
    internal const string BlobsConnectionKeyPrefix = "Aspire__Azure__Storage__Blobs";
    internal const string QueuesConnectionKeyPrefix = "Aspire__Azure__Storage__Queues";
    internal const string TablesConnectionKeyPrefix = "Aspire__Azure__Data__Tables";

    private EndpointReference EmulatorBlobEndpoint => new(this, "blob");
    private EndpointReference EmulatorQueueEndpoint => new(this, "queue");
    private EndpointReference EmulatorTableEndpoint => new(this, "table");

    internal IResourceBuilder<AzureBlobStorageResource>? BlobStorageBuilder { get; set; }
    internal IResourceBuilder<AzureQueueStorageResource>? QueueStorageBuilder { get; set; }
    internal IResourceBuilder<AzureTableStorageResource>? TableStorageBuilder { get; set; }

    internal List<AzureBlobStorageContainerResource> BlobContainers { get; } = [];

    internal List<AzureQueueStorageQueueResource> Queues { get; } = [];

    /// <summary>
    /// Gets the "blobEndpoint" output reference from the bicep template for the Azure Storage resource.
    /// </summary>
    public BicepOutputReference BlobEndpoint => new("blobEndpoint", this);

    /// <summary>
    /// Gets the "queueEndpoint" output reference from the bicep template for the Azure Storage resource.
    /// </summary>
    public BicepOutputReference QueueEndpoint => new("queueEndpoint", this);

    /// <summary>
    /// Gets the "tableEndpoint" output reference from the bicep template for the Azure Storage resource.
    /// </summary>
    public BicepOutputReference TableEndpoint => new("tableEndpoint", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets a value indicating whether the Azure Storage resource is running in the local emulator.
    /// </summary>
    public bool IsEmulator => this.IsContainer();

    /// <summary>
    /// Gets the connection URI expression for the blob storage service.
    /// </summary>
    /// <remarks>
    /// Format: <c>https://{host}:{port}</c> for emulator or <c>{blobEndpoint}</c> for Azure.
    /// </remarks>
    public ReferenceExpression BlobServiceUriExpression => IsEmulator
        ? ReferenceExpression.Create($"{EmulatorBlobEndpoint.Property(EndpointProperty.Url)}")
        : ReferenceExpression.Create($"{BlobEndpoint}");

    /// <summary>
    /// Gets the connection URI expression for the queue storage service.
    /// </summary>
    /// <remarks>
    /// Format: <c>https://{host}:{port}</c> for emulator or <c>{queueEndpoint}</c> for Azure.
    /// </remarks>
    public ReferenceExpression QueueServiceUriExpression => IsEmulator
        ? ReferenceExpression.Create($"{EmulatorQueueEndpoint.Property(EndpointProperty.Url)}")
        : ReferenceExpression.Create($"{QueueEndpoint}");

    /// <summary>
    /// Gets the connection URI expression for the table storage service.
    /// </summary>
    /// <remarks>
    /// Format: <c>https://{host}:{port}</c> for emulator or <c>{tableEndpoint}</c> for Azure.
    /// </remarks>
    public ReferenceExpression TableServiceUriExpression => IsEmulator
        ? ReferenceExpression.Create($"{EmulatorTableEndpoint.Property(EndpointProperty.Url)}")
        : ReferenceExpression.Create($"{TableEndpoint}");

    /// <summary>
    /// Gets the connection string for the Azure Storage emulator.
    /// </summary>
    /// <returns>The connection string for the Azure Storage emulator.</returns>
    internal ReferenceExpression GetEmulatorConnectionString() => IsEmulator
       ? AzureStorageEmulatorConnectionString.Create(blobEndpoint: EmulatorBlobEndpoint, queueEndpoint: EmulatorQueueEndpoint, tableEndpoint: EmulatorTableEndpoint)
       : throw new InvalidOperationException("The Azure Storage resource is not running in the local emulator.");

    /// <summary>
    /// Gets the connection string for the Azure Storage resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => GetEmulatorConnectionString();

    internal ReferenceExpression GetTableConnectionString() => IsEmulator
        ? AzureStorageEmulatorConnectionString.Create(tableEndpoint: EmulatorTableEndpoint)
        : ReferenceExpression.Create($"{TableEndpoint}");

    internal ReferenceExpression GetQueueConnectionString() => IsEmulator
        ? AzureStorageEmulatorConnectionString.Create(queueEndpoint: EmulatorQueueEndpoint)
        : ReferenceExpression.Create($"{QueueEndpoint}");

    internal ReferenceExpression GetBlobConnectionString() => IsEmulator
        ? AzureStorageEmulatorConnectionString.Create(blobEndpoint: EmulatorBlobEndpoint)
        : ReferenceExpression.Create($"{BlobEndpoint}");

    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName)
    {
        if (IsEmulator)
        {
            // Injected to support Azure Functions listener initialization.
            var connectionString = GetEmulatorConnectionString();
            target[connectionName] = connectionString;
            // Injected to support Aspire client integration for Azure Storage.
            target[$"{BlobsConnectionKeyPrefix}__{connectionName}__ConnectionString"] = connectionString;
            target[$"{QueuesConnectionKeyPrefix}__{connectionName}__ConnectionString"] = connectionString;
            target[$"{TablesConnectionKeyPrefix}__{connectionName}__ConnectionString"] = connectionString;
        }
        else
        {
            // Injected to support Azure Functions listener initialization.
            target[$"{connectionName}__blobServiceUri"] = BlobEndpoint;
            target[$"{connectionName}__queueServiceUri"] = QueueEndpoint;
            target[$"{connectionName}__tableServiceUri"] = TableEndpoint;
            // Injected to support Aspire client integration for Azure Storage.
            target[$"{BlobsConnectionKeyPrefix}__{connectionName}__ServiceUri"] = BlobEndpoint;
            target[$"{QueuesConnectionKeyPrefix}__{connectionName}__ServiceUri"] = QueueEndpoint;
            target[$"{TablesConnectionKeyPrefix}__{connectionName}__ServiceUri"] = TableEndpoint;
        }
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a StorageAccount with the same identifier already exists
        var existingAccount = resources.OfType<StorageAccount>().SingleOrDefault(account => account.BicepIdentifier == bicepIdentifier);

        if (existingAccount is not null)
        {
            return existingAccount;
        }

        // Create and add new resource if it doesn't exist
        var account = StorageAccount.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            account))
        {
            account.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(account);
        return account;
    }

    internal IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties()
    {
        if (IsEmulator)
        {
            yield return new("ConnectionString", ConnectionStringExpression);
        }
    }
}
