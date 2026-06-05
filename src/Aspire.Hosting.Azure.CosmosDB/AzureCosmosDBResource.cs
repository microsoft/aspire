// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.CosmosDB;
using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting;

/// <summary>
/// A resource that represents an Azure Cosmos DB.
/// </summary>
public class AzureCosmosDBResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure),
    IResourceWithConnectionString,
    IResourceWithEndpoints,
    IResourceWithAzureFunctionsConfig
{
    internal List<AzureCosmosDBDatabaseResource> Databases { get; } = [];

    internal EndpointReference EmulatorEndpoint => new(this, "emulator");

    /// <summary>
    /// Gets the "connectionString" reference from the secret outputs of the Azure Cosmos DB resource.
    /// </summary>
    [Obsolete("BicepSecretOutputReference is no longer supported. Use ConnectionStringOutput instead.")]
    public BicepSecretOutputReference ConnectionString => new("connectionString", this);

    /// <summary>
    /// Gets the "connectionString" output reference from the bicep template for the Azure Cosmos DB resource.
    ///
    /// This is used when Entra ID authentication is used. The connection string is an output of the bicep template.
    /// </summary>
    /// <remarks>
    /// The value is taken from the cosmos.properties.documentEndpoint bicep property and contains an endpoint.
    /// </remarks>
    public BicepOutputReference ConnectionStringOutput => new("connectionString", this);

    /// <summary>
    /// Whether or not to use the default Azure Cosmos DB SKU. By default, this is set to false, and the Cosmos DB
    /// account is created as a serverless account. If true, it will use the default Azure Cosmos DB SKU, which is
    /// typically a provisioned throughput account.
    /// </summary>
    internal bool UseDefaultAzureSku { get; set; } // Default to false

    /// <summary>
    /// Gets the "connectionString" secret reference from the key vault associated with this resource.
    ///
    /// This is set when access key authentication is used. The connection string is stored in a secret in the Azure Key Vault.
    /// </summary>
    internal IAzureKeyVaultSecretReference? ConnectionStringSecretOutput { get; set; }

    /// <summary>
    /// Gets the "primaryAccessKey" secret reference from the key vault associated with this resource.
    ///
    /// This is set when access key authentication is used. The Access Key string is stored in a secret in the Azure Key Vault.
    /// </summary>
    internal IAzureKeyVaultSecretReference? PrimaryAccessKeySecretOutput { get; set; }

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets a value indicating whether the resource uses access key authentication.
    /// </summary>
    [MemberNotNullWhen(true, nameof(ConnectionStringSecretOutput))]
    [MemberNotNullWhen(true, nameof(PrimaryAccessKeySecretOutput))]
    public bool UseAccessKeyAuthentication => ConnectionStringSecretOutput is not null;

    /// <summary>
    /// Gets a value indicating whether the Azure Cosmos DB resource is running in the local emulator.
    /// </summary>
    public bool IsEmulator => this.IsContainer();

    internal bool IsPreviewEmulator =>
        this.TryGetContainerImageName(out var imageName) &&
        imageName == $"{CosmosDBEmulatorContainerImageTags.Registry}/{CosmosDBEmulatorContainerImageTags.Image}:{CosmosDBEmulatorContainerImageTags.TagVNextPreview}";

    /// <summary>
    /// Gets the account endpoint URI expression for the Cosmos DB account.
    /// </summary>
    /// <remarks>
    /// Format: <c>https://{name}.documents.azure.com:443/</c>.
    /// </remarks>
    public ReferenceExpression Uri =>
        IsEmulator ?
            IsPreviewEmulator ?
                ReferenceExpression.Create($"{EmulatorEndpoint.Property(EndpointProperty.Url)}") :
                ReferenceExpression.Create($"https://{EmulatorEndpoint.Property(EndpointProperty.IPV4Host)}:{EmulatorEndpoint.Property(EndpointProperty.Port)}") :
            ReferenceExpression.Create($"{ConnectionStringOutput}");

    /// <summary>
    /// Gets the account key expression for the Cosmos DB account.
    /// </summary>
    /// <remarks>
    /// This is only available for emulator and access key authentication. For Entra ID authentication, this property will be null.
    /// </remarks>
    public ReferenceExpression? AccountKey =>
        IsEmulator ?
            ReferenceExpression.Create($"{CosmosConstants.EmulatorAccountKey}") :
            UseAccessKeyAuthentication ?
                ReferenceExpression.Create($"{PrimaryAccessKeySecretOutput}") :
                null;

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure Cosmos DB resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        IsEmulator ?
            AzureCosmosDBEmulatorConnectionString.Create(EmulatorEndpoint, IsPreviewEmulator) :
            UseAccessKeyAuthentication ?
                ReferenceExpression.Create($"{ConnectionStringSecretOutput}") :
                ReferenceExpression.Create($"{ConnectionStringOutput}");

    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName)
    {
        if (IsEmulator || UseAccessKeyAuthentication)
        {
            SetConnectionString(target, connectionName, ConnectionStringExpression);
        }
        else
        {
            SetAccountEndpoint(target, connectionName);
        }
    }

    internal void SetConnectionString(IDictionary<string, object> target, string connectionName, ReferenceExpression connectionStringExpression)
    {
        // Always inject the connection string associated with the top-level resource
        // for the Azure Functions host.
        target[connectionName] = ConnectionStringExpression;
        // Injected to support Aspire client integration for CosmosDB in Azure Functions projects.
        // Use the child resource connection string here to support child resource integrations.
        target[$"Aspire__Microsoft__EntityFrameworkCore__Cosmos__{connectionName}__ConnectionString"] = connectionStringExpression;
        target[$"Aspire__Microsoft__Azure__Cosmos__{connectionName}__ConnectionString"] = connectionStringExpression;
    }

    internal void SetAccountEndpoint(IDictionary<string, object> target, string connectionName)
    {
        // Always inject the connection string associated with the top-level resource
        // for the Azure Functions host.
        target[$"{connectionName}__accountEndpoint"] = ConnectionStringExpression;
        // Injected to support Aspire client integration for CosmosDB in Azure Functions projects.
        target[$"Aspire__Microsoft__EntityFrameworkCore__Cosmos__{connectionName}__AccountEndpoint"] = ConnectionStringExpression;
        target[$"Aspire__Microsoft__Azure__Cosmos__{connectionName}__AccountEndpoint"] = ConnectionStringExpression;
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a CosmosDBAccount with the same identifier already exists
        var existingStore = resources.OfType<CosmosDBAccount>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);

        if (existingStore is not null)
        {
            return existingStore;
        }

        // Create and add new resource if it doesn't exist
        var store = CosmosDBAccount.FromExisting(bicepIdentifier);

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

    /// <inheritdoc/>
    public override void AddRoleAssignments(IAddRoleAssignmentsContext roleAssignmentContext)
    {
        Debug.Assert(!UseAccessKeyAuthentication, "AddRoleAssignments should not be called when using AccessKeyAuthentication");

        var infra = roleAssignmentContext.Infrastructure;
        var cosmosAccount = (CosmosDBAccount)AddAsExistingResource(infra);

        var principalId = roleAssignmentContext.PrincipalId;

        AzureCosmosExtensions.AddContributorRoleAssignment(infra, cosmosAccount, principalId);
    }

    internal ReferenceExpression GetChildConnectionString(string childResourceName, string? databaseName = null, string? containerName = null)
    {
        if (string.IsNullOrEmpty(databaseName) && string.IsNullOrEmpty(containerName))
        {
            return ConnectionStringExpression;
        }

        var builder = new ReferenceExpressionBuilder();

        if (UseAccessKeyAuthentication && !IsEmulator)
        {
            var dbSecret = ConnectionStringSecretOutput.Resource.GetSecret(GetKeyValueSecretName(childResourceName));
            dbSecret.SecretOwner = ConnectionStringSecretOutput.SecretOwner;
            builder.AppendFormatted(dbSecret);
        }
        else
        {
            if (IsEmulator)
            {
                builder.AppendFormatted(ConnectionStringExpression);
            }
            else
            {
                // The ConnectionString output contains the account endpoint URI.
                builder.Append($"AccountEndpoint={ConnectionStringOutput}");
            }

            if (!string.IsNullOrEmpty(databaseName))
            {
                builder.Append($";Database={databaseName}");

                if (!string.IsNullOrEmpty(containerName))
                {
                    builder.Append($";Container={containerName}");
                }
            }
        }

        return builder.Build();
    }

    internal static string GetKeyValueSecretName(string resourceName)
        => $"connectionstrings--{resourceName}";

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", ReferenceExpression.Create($"{Uri}"));

        if (AccountKey is not null)
        {
            yield return new("AccountKey", AccountKey);
        }

        if (IsEmulator || UseAccessKeyAuthentication)
        {
            yield return new("ConnectionString", ConnectionStringExpression);
        }
    }
}
