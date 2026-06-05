// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Kusto;
using Kusto.Data;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Kusto read-write database resource, which is a child resource of a <see cref="AzureKustoClusterResource"/>.
/// </summary>
public class AzureKustoReadWriteDatabaseResource : Resource, IResourceWithParent<AzureKustoClusterResource>, IResourceWithConnectionString
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureKustoReadWriteDatabaseResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="databaseName">The database name.</param>
    /// <param name="kustoParentResource">The Kusto parent resource associated with this database.</param>
    public AzureKustoReadWriteDatabaseResource(string name, string databaseName, AzureKustoClusterResource kustoParentResource)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(kustoParentResource);

        DatabaseName = databaseName;
        Parent = kustoParentResource;
    }

    /// <summary>
    /// Gets the parent Kusto resource.
    /// </summary>
    public AzureKustoClusterResource Parent { get; }

    /// <summary>
    /// Gets the connection string expression for the Kusto database.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            var connectionStringBuilder = new KustoConnectionStringBuilder
            {
                InitialCatalog = DatabaseName
            };

            return ReferenceExpression.Create($"{Parent};{connectionStringBuilder.ToString()}");
        }
    }

    /// <summary>
    /// Gets the database name.
    /// </summary>
    public string DatabaseName { get; }

    /// <summary>
    /// Converts the current instance to a provisioning entity.
    /// </summary>
    /// <returns>A <see cref="KustoReadWriteDatabase"/> instance.</returns>
    internal KustoReadWriteDatabase ToProvisioningEntity()
    {
        var database = new KustoReadWriteDatabase(Infrastructure.NormalizeBicepIdentifier(Name));
        database.Name = DatabaseName;
        return database;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        foreach (var property in ((IResourceWithConnectionString)Parent).GetConnectionProperties())
        {
            yield return property;
        }

        yield return new("DatabaseName", ReferenceExpression.Create($"{DatabaseName}"));
    }
}
