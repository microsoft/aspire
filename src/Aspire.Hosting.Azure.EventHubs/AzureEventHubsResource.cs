// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.EventHubs;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Event Hubs resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure Event Hubs resource.</param>
public class AzureEventHubsResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithConnectionString, IResourceWithEndpoints, IResourceWithAzureFunctionsConfig
{
    private static readonly string[] s_eventHubClientNames =
    [
        "EventHubProducerClient",
        "EventHubConsumerClient",
        "EventProcessorClient",
        "PartitionReceiver",
        "EventHubBufferedProducerClient"
    ];

    private const string ConnectionKeyPrefix = "Aspire__Azure__Messaging__EventHubs";

    internal List<AzureEventHubResource> Hubs { get; } = [];

    /// <summary>
    /// Gets the "eventHubsEndpoint" output reference from the bicep template for the Azure Event Hubs resource.
    /// </summary>
    public BicepOutputReference EventHubsEndpoint => new("eventHubsEndpoint", this);

    /// <summary>
    /// Gets the "eventHubsHostName" output reference from the bicep template for the Azure Event Hubs resource.
    /// </summary>
    internal BicepOutputReference EventHubsHostName => new("eventHubsHostName", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    internal EndpointReference EmulatorEndpoint => new(this, "emulator");

    /// <summary>
    /// Gets a value indicating whether the Azure Event Hubs resource is running in the local emulator.
    /// </summary>
    public bool IsEmulator => this.IsContainer();

    /// <summary>
    /// Gets the host name for the Event Hubs namespace.
    /// </summary>
    public ReferenceExpression HostName =>
        IsEmulator ?
            ReferenceExpression.Create($"{EmulatorEndpoint.Property(EndpointProperty.Host)}") :
            ReferenceExpression.Create($"{EventHubsHostName}");

    /// <summary>
    /// Gets the port for the Event Hubs namespace.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint port.
    /// In Azure mode, return null.
    /// </remarks>
    public ReferenceExpression? Port =>
        IsEmulator ?
            ReferenceExpression.Create($"{EmulatorEndpoint.Property(EndpointProperty.Port)}") :
            null;

    /// <summary>
    /// Gets the connection URI expression for the Event Hubs namespace.
    /// </summary>
    /// <remarks>
    /// Format: <c>sb://{host}:{port}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression =>
        IsEmulator ?
            ReferenceExpression.Create($"sb://{EmulatorEndpoint.Property(EndpointProperty.HostAndPort)}") :
            ReferenceExpression.Create($"{EventHubsEndpoint}");

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure Event Hubs endpoint.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => GetConnectionString();

    internal ReferenceExpression GetConnectionString(string? eventHub = null, string? consumerGroup = null)
    {
        var builder = new ReferenceExpressionBuilder();

        if (IsEmulator)
        {
            builder.Append($"Endpoint=sb://{EmulatorEndpoint.Property(EndpointProperty.HostAndPort)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true");
        }
        else
        {
            if (eventHub is null && consumerGroup is null)
            {
                // for backwards compatibility - if there is no event hub or consumer group, return just the endpoint
                builder.AppendFormatted(EventHubsEndpoint);
            }
            else
            {
                builder.Append($"Endpoint={EventHubsEndpoint}");
            }
        }

        if (eventHub is not null)
        {
            builder.Append($";EntityPath={eventHub}");
        }

        if (consumerGroup is not null)
        {
            builder.Append($";ConsumerGroup={consumerGroup}");
        }

        return builder.Build();
    }

    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName) =>
            ApplyAzureFunctionsConfiguration(target, connectionName);

    internal void ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName, string? eventHub = null, string? consumerGroup = null)
    {
        if (IsEmulator)
        {
            // Injected to support Azure Functions listener initialization.
            target[connectionName] = ConnectionStringExpression;
            // Injected to support Aspire client integration for each EventHubs client in Azure Functions projects.
            foreach (var clientName in s_eventHubClientNames)
            {
                target[$"{ConnectionKeyPrefix}__{clientName}__{connectionName}__ConnectionString"] = ConnectionStringExpression;
            }
        }
        else
        {
            // Injected to support Azure Functions listener initialization.
            target[$"{connectionName}__fullyQualifiedNamespace"] = EventHubsEndpoint;
            // Injected to support Aspire client integration for each EventHubs client in Azure Functions projects.
            foreach (var clientName in s_eventHubClientNames)
            {
                target[$"{ConnectionKeyPrefix}__{clientName}__{connectionName}__FullyQualifiedNamespace"] = EventHubsEndpoint;
            }
        }

        // Injected to support Aspire client integration for each EventHubs client in Azure Functions projects.
        foreach (var clientName in s_eventHubClientNames)
        {
            if (eventHub is not null)
            {
                target[$"{ConnectionKeyPrefix}__{clientName}__{connectionName}__EventHubName"] = eventHub;
            }
            if (consumerGroup is not null)
            {
                target[$"{ConnectionKeyPrefix}__{clientName}__{connectionName}__ConsumerGroup"] = consumerGroup;
            }
        }
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if an EventHubsNamespace with the same identifier already exists
        var existingHubs = resources.OfType<EventHubsNamespace>().SingleOrDefault(hubs => hubs.BicepIdentifier == bicepIdentifier);

        if (existingHubs is not null)
        {
            return existingHubs;
        }

        // Create and add new resource if it doesn't exist
        var hubs = EventHubsNamespace.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            hubs))
        {
            hubs.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(hubs);
        return hubs;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Host", HostName);

        if (Port is not null)
        {
            yield return new("Port", Port);
        }

        yield return new("Uri", UriExpression);

        if (IsEmulator)
        {
            yield return new("ConnectionString", ReferenceExpression.Create($"Endpoint={EmulatorEndpoint.Property(EndpointProperty.HostAndPort)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true"));
        }
    }
}
