// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Identity;
using Azure.Messaging.EventHubs;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Hosting;

namespace Aspire.Azure.Messaging.EventHubs.Tests;

public class ConformanceTests_EventProcessorClient : ConformanceTestsBase<EventProcessorClient, AzureMessagingEventHubsProcessorSettings>
{
    protected override RequiredLogCategory[] RequiredLogCategories => [new("Azure.Core"), new("Azure.Messaging.EventHubs.Processor.BlobEventStore")];

    protected override void SetHealthCheck(AzureMessagingEventHubsProcessorSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(AzureMessagingEventHubsProcessorSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(AzureMessagingEventHubsProcessorSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureMessagingEventHubsProcessorSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureEventProcessorClient("ehprc", settings => ConfigureCredentials(configure, settings));
        }
        else
        {
            builder.AddKeyedAzureEventProcessorClient(key, settings => ConfigureCredentials(configure, settings));
        }

        AspireEventHubsExtensionsTests.InjectMockBlobClient(builder);

        void ConfigureCredentials(Action<AzureMessagingEventHubsProcessorSettings>? configure, AzureMessagingEventHubsProcessorSettings settings)
        {
            if (CanConnectToServer)
            {
                settings.Credential = new DefaultAzureCredential();
            }
            configure?.Invoke(settings);
        }
    }

    protected override void TriggerActivity(EventProcessorClient service)
    {
        try
        {
            service.StartProcessing();
            service.StopProcessing();
        }
        catch (Exception)
        {
            // Expected exception
        }
    }
}
