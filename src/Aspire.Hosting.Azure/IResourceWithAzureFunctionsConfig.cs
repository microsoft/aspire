// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an resource that can provide configuration for Azure Functions.
/// </summary>
public interface IResourceWithAzureFunctionsConfig : IResource
{
    /// <summary>
    /// Applies the Azure Functions configuration to the target dictionary.
    /// </summary>
    /// <param name="target">The dictionary to which the Azure Functions configuration will be applied.</param>
    /// <param name="connectionName">The name of the connection key to be used for the given configuration.</param>
    /// <remarks>
    /// <para>
    /// Implementations should populate the <paramref name="target"/> dictionary with keys that match
    /// the format expected by the Azure Functions runtime, and values that are valid connection strings
    /// or service URIs.
    /// </para>
    /// <para>
    /// For Azure Functions listener initialization, use keys like:
    /// <list type="bullet">
    /// <item><c>{connectionName}</c> for connection strings (typically used with emulators)</item>
    /// <item><c>{connectionName}__fullyQualifiedNamespace</c> for Azure Event Hubs and Azure Service Bus endpoints</item>
    /// <item><c>{connectionName}__blobServiceUri</c> and <c>{connectionName}__queueServiceUri</c> for Azure Blob Storage and Azure Queue Storage</item>
    /// <item><c>{connectionName}__accountEndpoint</c> for Azure Cosmos DB account endpoints</item>
    /// </list>
    /// </para>
    /// <para>
    /// For Aspire client integration support, use keys following the pattern:
    /// <c>Aspire__{ServiceType}__{connectionName}__{Property}</c> where <c>{ServiceType}</c> is the
    /// service namespace (e.g., <c>Azure__Messaging__EventHubs</c>, <c>Azure__Messaging__ServiceBus</c>,
    /// <c>Azure__Storage__Blobs</c>, <c>Microsoft__Azure__Cosmos</c>) and <c>{Property}</c> is typically
    /// <c>ConnectionString</c>, <c>ServiceUri</c>, <c>FullyQualifiedNamespace</c>, or <c>AccountEndpoint</c>.
    /// </para>
    /// </remarks>
    void ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName);
}
