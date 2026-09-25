// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Identity;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Azure.Messaging.WebPubSub.Tests;

public class ConformanceTests : ConformanceTests<WebPubSubServiceClient, AzureMessagingWebPubSubSettings>
{
    public const string Endpoint = "https://aspirewebpubsubtests.webpubsub.azure.com/";
    public const string ReverseProxyEndpoint = "https://reverse.com";
    // Fake connection string for cases when credentials are unavailable and need to switch to raw connection string
    protected const string ConnectionString = "Endpoint=https://aspirewebpubsubtests.webpubsub.azure.com/;AccessKey=fake;";

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Azure.Messaging.WebPubSub.*";

    protected override RequiredLogCategory[] RequiredLogCategories => [new("Azure.Core")];

    protected override bool SupportsKeyedRegistrations => true;

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Azure": {
              "Messaging": {
                "WebPubSub": {
                  "Endpoint": "https://endpoint.com",
                  "DisableHealthChecks": true,
                  "ClientOptions": {
                    "Retry": {
                      "Mode": "Fixed",
                      "MaxDelay": "00:03"
                    },
                    "ReverseProxyEndpoint": "https://reverse.com"
                  }
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Azure": { "Messaging":{ "WebPubSub": { "ClientOptions": { "ReverseProxyEndpoint": "EndPoint"}}}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Azure": { "Messaging":{ "WebPubSub": { "DisableHealthChecks": "hello"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Azure": { "Messaging":{ "WebPubSub": { "ClientOptions": { "Retry": { "Mode": "Fast"}}}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "Azure": { "Messaging":{ "WebPubSub": { "ClientOptions": { "Retry": { "MaxDelay": "3S"}}}}}}}""", "The string value is not a match for the indicated regular expression"),
        };

    // When credentials are not available, we switch to using raw connection string (otherwise we get CredentialUnavailableException)
    private KeyValuePair<string, string?> GetMainConfigEntry(string? key)
        => CanConnectToServer
                ? new(CreateConfigKey("Aspire:Azure:Messaging:WebPubSub", key is not null ? $"{key}:{key}" : null, nameof(AzureMessagingWebPubSubSettings.Endpoint)), Endpoint)
                : new(CreateConfigKey("Aspire:Azure:Messaging:WebPubSub", key is not null ? $"{key}:{key}" : null, nameof(AzureMessagingWebPubSubSettings.ConnectionString)), ConnectionString);

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(
        [
            GetMainConfigEntry(key)
        ]);

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureMessagingWebPubSubSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureWebPubSubServiceClient("wps", Configure);
        }
        else
        {
            builder.AddKeyedAzureWebPubSubServiceClient(key, key, Configure);
        }

        void Configure(AzureMessagingWebPubSubSettings settings)
        {
            settings.HubName = "hub1";
            if (CanConnectToServer)
            {
                settings.Credential = new DefaultAzureCredential();
            }
            configure?.Invoke(settings);
        }
    }

    protected override void SetHealthCheck(AzureMessagingWebPubSubSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(AzureMessagingWebPubSubSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(AzureMessagingWebPubSubSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void TriggerActivity(WebPubSubServiceClient service)
    {
        service.SendToAll("test message");
    }
}
