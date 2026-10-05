// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.TestUtilities;
using Azure.Core.Extensions;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Messaging.WebPubSub.Tests;

public class AspireWebPubSubExtensionsTests
{
    private const string ConnectionString = "Endpoint=https://aspirewebpubsubtests.webpubsub.azure.com/;AccessKey=fake;";
    private const string UnusedConnectionString = "Endpoint=https://unused.webpubsub.azure.com/;AccessKey=fake;";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureWebPubSubServiceClient("wps", "wps", o => o.HubName = "hub1");
        }
        else
        {
            builder.AddAzureWebPubSubServiceClient("wps", o => o.HubName = "hub1");
        }

        var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("wps") :
            host.Services.GetRequiredService<WebPubSubServiceClient>();

        Assert.Equal(ConformanceTests.Endpoint, client.Endpoint.AbsoluteUri);
        Assert.Equal("hub1", client.Hub);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", UnusedConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureWebPubSubServiceClient("wps", "wps", settings =>
            {
                settings.ConnectionString = ConnectionString;
                settings.HubName = "hub1";
            });
        }
        else
        {
            builder.AddAzureWebPubSubServiceClient("wps", settings => {
                settings.ConnectionString = ConnectionString;
                settings.HubName = "hub1";
            });
        }

        var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("wps") :
            host.Services.GetRequiredService<WebPubSubServiceClient>();

        Assert.Equal(ConformanceTests.Endpoint, client.Endpoint.AbsoluteUri);
        Assert.Equal("hub1", client.Hub);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "wps" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Azure:Messaging:WebPubSub", key, "ConnectionString"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:wps", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureWebPubSubServiceClient("wps", "wps", o => o.HubName = "hub1");
        }
        else
        {
            builder.AddAzureWebPubSubServiceClient("wps", o => o.HubName = "hub1");
        }

        var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("wps") :
            host.Services.GetRequiredService<WebPubSubServiceClient>();

        Assert.Equal(ConformanceTests.Endpoint, client.Endpoint.AbsoluteUri);
        Assert.Equal("hub1", client.Hub);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EndpointWorksInConnectionStrings(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", ConformanceTests.Endpoint)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureWebPubSubServiceClient("wps", "wps", o => o.HubName = "hub1");
        }
        else
        {
            builder.AddAzureWebPubSubServiceClient("wps", o => o.HubName = "hub1");
        }

        var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("wps") :
            host.Services.GetRequiredService<WebPubSubServiceClient>();

        Assert.Equal(ConformanceTests.Endpoint, client.Endpoint.AbsoluteUri);
        Assert.Equal("hub1", client.Hub);
    }

    [Fact]
    public void AddInvalidHubThrows()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", ConnectionString)
        ]);
        builder.AddAzureWebPubSubServiceClient("wps");

        var host = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(host.Services.GetRequiredService<WebPubSubServiceClient>);
        Assert.Equal("A WebPubSubServiceClient could not be configured. Ensure a valid HubName was configured or provided in the 'Aspire:Azure:Messaging:WebPubSub' configuration section.", ex.Message);
    }

    [Fact]
    public void AddKeyedServiceWithInvalidHubThrows()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", ConnectionString)
        ]);

        Assert.Throws<ArgumentException>(() => builder.AddKeyedAzureWebPubSubServiceClient("wps", ""));
    }

    [Fact]
    public void ServiceKeyDefaultsToHubName()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", ConnectionString)
        ]);
        builder.AddKeyedAzureWebPubSubServiceClient("wps", "key");

        var host = builder.Build();
        var client = host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("key");
        Assert.Equal(ConformanceTests.Endpoint, client.Endpoint.AbsoluteUri);
        Assert.Equal("key", client.Hub);
    }

    [Fact]
    public void ConfigSectionWorksForMultipleHubs()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Azure:Messaging:WebPubSub:wps", "hub1", "ConnectionString"), ConnectionString),
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Azure:Messaging:WebPubSub:wps", "hub2", "ConnectionString"), ConnectionString),
        ]);

        builder.AddKeyedAzureWebPubSubServiceClient("wps", "hub1");
        builder.AddKeyedAzureWebPubSubServiceClient("wps", "hub2");

        var host = builder.Build();
        var client1 = host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("hub1");
        Assert.Equal("hub1", client1.Hub);
        Assert.Equal(ConformanceTests.Endpoint, client1.Endpoint.AbsoluteUri);
        var client2 = host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("hub2");
        Assert.Equal("hub2", client2.Hub);
        Assert.Equal(ConformanceTests.Endpoint, client2.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringWithHubNameIsUsed(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionStringWithHub = $"{ConnectionString};Hub=embedded-hub;";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", connectionStringWithHub)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureWebPubSubServiceClient("wps");
        }
        else
        {
            builder.AddAzureWebPubSubServiceClient("wps");
        }

        var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("wps") :
            host.Services.GetRequiredService<WebPubSubServiceClient>();

        Assert.Equal(ConformanceTests.Endpoint, client.Endpoint.AbsoluteUri);
        Assert.Equal("embedded-hub", client.Hub);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitHubNameOverridesConnectionStringHub(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionStringWithHub = $"{ConnectionString};Hub=embedded-hub;";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", connectionStringWithHub)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureWebPubSubServiceClient("wps", o => o.HubName = "explicit-hub");
        }
        else
        {
            builder.AddAzureWebPubSubServiceClient("wps", o => o.HubName = "explicit-hub");
        }

        var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<WebPubSubServiceClient>("wps") :
            host.Services.GetRequiredService<WebPubSubServiceClient>();

        Assert.Equal(ConformanceTests.Endpoint, client.Endpoint.AbsoluteUri);
        Assert.Equal("explicit-hub", client.Hub);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddAzureWebPubSubServiceClient_HealthCheckReportsConnectionFailureDescription(bool useKeyed)
    {
        var endpoint = ComponentTestUrls.CreateUnavailableHttpsUri();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:wps", $"Endpoint={endpoint};AccessKey=fake;Hub=hub1;")
        ]);

        static void ConfigureClientBuilder(IAzureClientBuilder<WebPubSubServiceClient, WebPubSubServiceClientOptions> clientBuilder)
        {
            clientBuilder.ConfigureOptions(options => options.Retry.MaxRetries = 0);
        }

        if (useKeyed)
        {
            builder.AddKeyedAzureWebPubSubServiceClient("wps", configureClientBuilder: ConfigureClientBuilder);
        }
        else
        {
            builder.AddAzureWebPubSubServiceClient("wps", configureClientBuilder: ConfigureClientBuilder);
        }

        using var host = builder.Build();
        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthCheckReport = await healthCheckService.CheckHealthAsync(cts.Token);
        var healthCheckName = useKeyed ? "Azure_WebPubSubServiceClient_wps" : "Azure_WebPubSubServiceClient";
        Assert.True(healthCheckReport.Entries.TryGetValue(healthCheckName, out var entry));

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal("Failed to connect to Azure Web PubSub service.", entry.Description);
        Assert.NotNull(entry.Exception);
    }
}
