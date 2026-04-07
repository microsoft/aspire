// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents an Azure Data Lake Storage.
/// </summary>
public class AzureDataLakeStorageResource(string name, AzureStorageResource storage) : Resource(name),
    IResourceWithConnectionString,
    IResourceWithParent<AzureStorageResource>,
    IResourceWithAzureFunctionsConfig,
    IAzurePrivateEndpointTarget
{
    /// <summary>
    /// Gets the parent AzureStorageResource of this AzureDataLakeResource.
    /// </summary>
    public AzureStorageResource Parent => storage ?? throw new ArgumentNullException(nameof(storage));

    /// <summary>
    /// Gets the connection URI expression for the data lake storage service.
    /// </summary>
    /// <remarks>
    /// Format: <c>{blobEndpoint}</c> for Azure.
    /// </remarks>
    public ReferenceExpression UriExpression => Parent.DataLakeUriExpression;

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure DataLake Storage resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetDataLakeConnectionString();

    internal ReferenceExpression GetConnectionString(string? fileSystemName)
    {
        if (Parent.IsEmulator)
        {
            throw new InvalidOperationException("Emulator currently does not support data lake.");
        }

        if (string.IsNullOrEmpty(fileSystemName))
        {
            return ConnectionStringExpression;
        }

        ReferenceExpressionBuilder builder = new();

        builder.Append($"Endpoint={ConnectionStringExpression}");

        builder.Append($";FileSystemName={fileSystemName}");

        return builder.Build();
    }

    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(
        IDictionary<string, object> target,
        string connectionName)
    {
        if (Parent.IsEmulator)
        {
            throw new InvalidOperationException("Emulator currently does not support data lake.");
        }

        target[$"{connectionName}__dataLakeServiceUri"] = Parent.DataLakeEndpoint;
        target[$"{connectionName}__queueServiceUri"] = Parent.QueueEndpoint;
        target[$"{AzureStorageResource.DataLakeConnectionKeyPrefix}__{connectionName}__ServiceUri"] =
            Parent.DataLakeEndpoint;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        if (Parent.IsEmulator)
        {
            throw new InvalidOperationException("Emulator currently does not support data lake.");
        }

        yield return new("Uri", UriExpression);
    }

    BicepOutputReference IAzurePrivateEndpointTarget.Id => Parent.Id;

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["dfs"];

    string IAzurePrivateEndpointTarget.GetPrivateDnsZoneName() => "privatelink.dfs.core.windows.net";
}
