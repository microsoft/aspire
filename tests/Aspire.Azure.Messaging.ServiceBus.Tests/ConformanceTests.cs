// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Messaging.ServiceBus.Tests;

public abstract class ConformanceTests : ConformanceTests<ServiceBusClient, AzureMessagingServiceBusSettings>
{
    // Roles: Azure Service Bus Data Owner
    public const string FullyQualifiedNamespace = "aspireservicebustests.servicebus.windows.net";
    // Fake connection string for cases when credentials are unavailable and need to switch to raw connection string
    protected const string ConnectionString = "Endpoint=sb://foo.servicebus.windows.net/;SharedAccessKeyName=fake;SharedAccessKey=fake";

    public ConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Azure.Messaging.ServiceBus.ServiceBusReceiver";

    protected override RequiredLogCategory[] RequiredLogCategories => [new("Azure.Messaging.ServiceBus")];

    protected override bool SupportsKeyedRegistrations => true;

    protected override string? ConfigurationSectionName => "Aspire:Azure:Messaging:ServiceBus";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Azure": {
              "Messaging": {
                "ServiceBus": {
                  "Namespace": "YOUR_NAMESPACE",
                  "DisableHealthChecks": false,
                  "ClientOptions": {
                    "ConnectionIdleTimeout": "00:01",
                    "EnableCrossEntityTransactions": true,
                    "RetryOptions": {
                      "Mode": "Fixed",
                      "MaxDelay": "00:03"
                    },
                    "TransportType": "AmqpWebSockets"
                  }
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Azure": { "Messaging":{ "ServiceBus": {"ClientOptions": {"CustomEndpointAddress": "EndPoint"}}}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Azure": { "Messaging":{ "ServiceBus": {"ClientOptions": {"EnableCrossEntityTransactions": "false"}}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Azure": { "Messaging":{ "ServiceBus": {"ClientOptions": {"RetryOptions": {"Mode": "Fast"}}}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "Azure": { "Messaging":{ "ServiceBus": {"ClientOptions": {"RetryOptions": {"TryTimeout": "3S"}}}}}}}""", "The string value is not a match for the indicated regular expression"),
            ("""{"Aspire": { "Azure": { "Messaging":{ "ServiceBus": {"ClientOptions": {"TransportType": "HTTP"}}}}}}""", "Value should match one of the values specified by the enum")
        };

    // When credentials are not available, we switch to using raw connection string (otherwise we get CredentialUnavailableException)
    protected KeyValuePair<string, string?> GetMainConfigEntry(string? key)
        => CanConnectToServer
                ? new(CreateConfigKey("Aspire:Azure:Messaging:ServiceBus", key, nameof(AzureMessagingServiceBusSettings.FullyQualifiedNamespace)), FullyQualifiedNamespace)
                : new(CreateConfigKey("Aspire:Azure:Messaging:ServiceBus", key, nameof(AzureMessagingServiceBusSettings.ConnectionString)), ConnectionString);

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureMessagingServiceBusSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureServiceBusClient("sb", ConfigureCredentials);
        }
        else
        {
            builder.AddKeyedAzureServiceBusClient(key, ConfigureCredentials);
        }

        void ConfigureCredentials(AzureMessagingServiceBusSettings settings)
        {
            if (CanConnectToServer)
            {
                settings.Credential = new DefaultAzureCredential();
            }
            configure?.Invoke(settings);
        }
    }

    protected override void SetMetrics(AzureMessagingServiceBusSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(AzureMessagingServiceBusSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    public static RemoteInvokeOptions EnableTracingForAzureSdk()
        => new()
        {
            RuntimeConfigurationOptions = { { "Azure.Experimental.EnableActivitySource", true } }
        };
}
