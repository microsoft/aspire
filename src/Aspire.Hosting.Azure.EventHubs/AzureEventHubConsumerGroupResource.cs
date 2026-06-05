// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Event Hub Consumer Group.
/// Initializes a new instance of the <see cref="AzureEventHubConsumerGroupResource"/> class.
/// </summary>
/// <remarks>
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/> to configure specific <see cref="Azure.Provisioning"/> properties.
/// </remarks>
public class AzureEventHubConsumerGroupResource(string name, string consumerGroupName, AzureEventHubResource parent)
    : Resource(name), IResourceWithParent<AzureEventHubResource>, IResourceWithConnectionString, IResourceWithAzureFunctionsConfig
{
    /// <summary>
    /// The event hub consumer group name.
    /// </summary>
    public string ConsumerGroupName { get; set; } = ThrowIfNullOrEmpty(consumerGroupName);

    /// <summary>
    /// Gets the parent Azure Event Hub resource.
    /// </summary>
    public AzureEventHubResource Parent { get; } = parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Gets the connection string expression for the Azure Event Hub Consumer Group.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.Parent.GetConnectionString(Parent.HubName, ConsumerGroupName);

    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName)
    {
        Parent.Parent.ApplyAzureFunctionsConfiguration(target, connectionName, Parent.HubName, ConsumerGroupName);
    }

    /// <summary>
    /// Converts the current instance to a provisioning entity.
    /// </summary>
    /// <returns>A <see cref="global::Azure.Provisioning.EventHubs.EventHub"/> instance.</returns>
    internal global::Azure.Provisioning.EventHubs.EventHubsConsumerGroup ToProvisioningEntity()
    {
        var consumerGroup = new global::Azure.Provisioning.EventHubs.EventHubsConsumerGroup(Infrastructure.NormalizeBicepIdentifier(Name));

        consumerGroup.Name = ConsumerGroupName;

        return consumerGroup;
    }

    /// <summary>
    /// Converts the current instance to a JSON object.
    /// </summary>
    /// <param name="writer">The Utf8JsonWriter to write the JSON object to.</param>
    internal void WriteJsonObjectProperties(Utf8JsonWriter writer)
    {
        writer.WriteString(nameof(Name), ConsumerGroupName);
    }

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        foreach (var property in ((IResourceWithConnectionString)Parent).GetConnectionProperties())
        {
            yield return property;
        }

        yield return new("ConsumerGroupName", ReferenceExpression.Create($"{ConsumerGroupName}"));
    }
}
