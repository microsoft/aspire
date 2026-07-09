// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Storage.Queues.Tests;

public class ConformanceTests : ConformanceTests<QueueServiceClient, AzureStorageQueuesSettings>
{
    // Authentication method: Azure AD User Account
    // Roles: Storage Queue Data Contributor
    public const string ServiceUri = "https://aspirestoragetests.queue.core.windows.net";

    private static readonly Lazy<bool> s_canConnectToServer = new(GetCanConnect);

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Azure.Storage.Queues.QueueClient";

    protected override bool CheckOptionClassSealed => false;

    protected override RequiredLogCategory[] RequiredLogCategories =>
    [
        new("Azure.Core"),
        new("Azure.Identity"),
    ];

    protected override bool SupportsKeyedRegistrations => true;

    protected override bool CanConnectToServer => s_canConnectToServer.Value;

    protected override string? ConfigurationSectionName => "Aspire:Azure:Storage:Queues";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Azure": {
              "Storage": {
                "Queues": {
                  "ServiceUri": "http://YOUR_URI",
                  "DisableHealthChecks": true,
                  "DisableTracing": false,
                  "ClientOptions": {
                    "EnableTenantDiscovery": true,
                    "MessageEncoding": "Base64",
                    "Retry": {
                      "Mode": "Exponential",
                      "Delay": "00:00:03"
                    }
                  }
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Azure": { "Storage":{ "Queues": { "ServiceUri": "YOUR_URI"}}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Azure": { "Storage":{ "Queues": { "ServiceUri": "http://YOUR_URI", "DisableHealthChecks": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Azure": { "Storage":{ "Queues": { "ClientOptions": {"MessageEncoding": "Fast"}}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "Azure": { "Storage":{ "Queues": { "ClientOptions": {"Retry": {"Mode": "Fast"}}}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "Azure": { "Storage":{ "Queues": { "ClientOptions": {"Retry": {"NetworkTimeout": "PT3S"}}}}}}}""", "The string value is not a match for the indicated regular expression")
        };

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new(CreateConfigKey("Aspire:Azure:Storage:Queues", key, "ServiceUri"), ServiceUri),
            new(CreateConfigKey("Aspire:Azure:Storage:Queues", key, "ClientOptions:Retry:MaxRetries"), "0")
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureStorageQueuesSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureQueueServiceClient("queue", ConfigureCredentials);
        }
        else
        {
            builder.AddKeyedAzureQueueServiceClient(key, ConfigureCredentials);
        }

        void ConfigureCredentials(AzureStorageQueuesSettings settings)
        {
            if (CanConnectToServer)
            {
                settings.Credential = new DefaultAzureCredential();
            }
            configure?.Invoke(settings);
        }
    }

    public ConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

    protected override void SetHealthCheck(AzureStorageQueuesSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(AzureStorageQueuesSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(AzureStorageQueuesSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void TriggerActivity(QueueServiceClient service)
    {
        string queueName = Guid.NewGuid().ToString();
        service.CreateQueue(queueName);
        service.DeleteQueue(queueName);
    }

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest(key: null), Output);

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest(key: "key"), Output);

    private static bool GetCanConnect()
    {
        QueueClientOptions clientOptions = new();
        clientOptions.Retry.MaxRetries = 0; // don't enable retries (test runs few times faster)
        QueueServiceClient tableClient = new(new Uri(ServiceUri), new DefaultAzureCredential(), clientOptions);

        try
        {
            tableClient.GetQueues().AsPages(pageSizeHint: 1).FirstOrDefault();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
