// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Common;
using Aspire.Azure.Messaging.EventHubs;
using Azure.Core;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using HealthChecks.Azure.Messaging.EventHubs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

internal abstract class EventHubsComponent<TSettings, TClient, TClientOptions> :
    AzureComponent<TSettings, TClient, TClientOptions>
    where TClientOptions : class
    where TClient : class
    where TSettings : AzureMessagingEventHubsSettings, new()
{
    private EventHubProducerClient? _healthCheckClient;

    // each EventHub client class is in a different namespace, so the base AzureComponent.ActivitySourceNames logic doesn't work
    protected override string[] ActivitySourceNames => ["Azure.Messaging.EventHubs.*"];

    protected override IHealthCheck CreateHealthCheck(TClient client, TSettings settings)
    {
        // HealthChecks.Azure.Messaging.EventHubs currently only supports EventHubProducerClient.
        // https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks/issues/2258 tracks supporting other client types.

        // Reuse the client if it's an EventHubProducerClient
        if (client is EventHubProducerClient producerClient)
        {
            _healthCheckClient = producerClient;
        }

        // Create a custom EventHubProducerClient otherwise
        if (_healthCheckClient == null)
        {
            var producerClientOptions = new EventHubProducerClientOptions
            {
                Identifier = $"AspireEventHubHealthCheck-{settings.EventHubName}",
            };

            // If no connection is provided use TokenCredential
            if (string.IsNullOrEmpty(settings.ConnectionString))
            {
                _healthCheckClient = new EventHubProducerClient(settings.FullyQualifiedNamespace, settings.EventHubName, settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential(), producerClientOptions);
            }
            // If no specific EventHubName is provided, it has to be in the connection string
            else if (string.IsNullOrEmpty(settings.EventHubName))
            {
                _healthCheckClient = new EventHubProducerClient(settings.ConnectionString, producerClientOptions);
            }
            else
            {
                _healthCheckClient = new EventHubProducerClient(settings.ConnectionString, settings.EventHubName, producerClientOptions);
            }
        }

        return new AzureEventHubHealthCheck(_healthCheckClient);
    }

    protected override bool GetHealthCheckEnabled(TSettings settings)
        => !settings.DisableHealthChecks;

    protected override TokenCredential? GetTokenCredential(TSettings settings)
        => settings.Credential;

    protected override bool GetMetricsEnabled(TSettings settings)
        => false;

    protected override bool GetTracingEnabled(TSettings settings)
        => !settings.DisableTracing;

    protected static string GenerateClientIdentifier(string? eventHubName, string? consumerGroup)
    {
        // configure processor identifier
        var slug = Guid.NewGuid().ToString().Substring(24);
        var identifier = $"{Environment.MachineName}-{eventHubName}-" +
                         $"{consumerGroup ?? "default"}-{slug}";

        return identifier;
    }

    protected static string GetNamespaceFromSettings(AzureMessagingEventHubsSettings settings)
    {
        string ns;

        try
        {
            // Extract the namespace from the connection string or qualified namespace
            ns = string.IsNullOrWhiteSpace(settings.FullyQualifiedNamespace)
                ? EventHubsConnectionStringProperties.Parse(settings.ConnectionString).Endpoint.Host
                : new Uri(settings.FullyQualifiedNamespace).Host;

            // This is likely to be similar to {yournamespace}.servicebus.windows.net or {yournamespace}.servicebus.chinacloudapi.cn
            var serviceBusIndex = ns.IndexOf(".servicebus", StringComparison.OrdinalIgnoreCase);
            if (serviceBusIndex != -1)
            {
                ns = ns[..serviceBusIndex];
            }
            else
            {
                // sanitize the namespace if it's not a servicebus namespace
                ns = ns.Replace(".", "-");
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException(
                $"A {typeof(TClient).Name} could not be configured. Please ensure that the ConnectionString or FullyQualifiedNamespace is well-formed.");
        }

        return ns;
    }

    protected static void EnsureConnectionStringOrNamespaceProvided(AzureMessagingEventHubsSettings settings,
        string connectionName, string configurationSectionName)
    {
        var connectionString = settings.ConnectionString;

        // Are we missing both connection string and namespace? throw.
        if (string.IsNullOrEmpty(connectionString) && string.IsNullOrEmpty(settings.FullyQualifiedNamespace))
        {
            throw new InvalidOperationException(
                $"A {typeof(TClient).Name} could not be configured. Ensure valid connection information was provided in " +
                $"'ConnectionStrings:{connectionName}' or specify a 'ConnectionString' or 'FullyQualifiedNamespace' in the '{configurationSectionName}' configuration section.");
        }

        // If we have a connection string, ensure there's an EntityPath if settings.EventHubName is missing
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            // We have a connection string -- do we have an EventHubName?
            if (string.IsNullOrWhiteSpace(settings.EventHubName))
            {
                // look for EntityPath
                var props = EventHubsConnectionStringProperties.Parse(connectionString);

                // if EntityPath is missing, throw
                if (string.IsNullOrWhiteSpace(props.EventHubName))
                {
                    throw new InvalidOperationException(
                        $"A {typeof(TClient).Name} could not be configured. Ensure a valid EventHubName was provided in " +
                        $"the '{configurationSectionName}' configuration section, or include an EntityPath in the ConnectionString.");
                }
                // The connection string has an EventHubName, but we'll set this anyway so the health check can use it
                settings.EventHubName = props.EventHubName;
            }
        }
        // If we have a namespace and no connection string, ensure there's an EventHubName
        else if (!string.IsNullOrWhiteSpace(settings.FullyQualifiedNamespace) && string.IsNullOrWhiteSpace(settings.EventHubName))
        {
            throw new InvalidOperationException(
                $"A {typeof(TClient).Name} could not be configured. Ensure a valid EventHubName was provided in " +
                $"the '{configurationSectionName}' configuration section.");
        }
    }
}
