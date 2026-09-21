// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Dekaf.Admin;
using Dekaf.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

public class AdminClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionAndBuilderOverridesAreApplied(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Services.AddSingleton("custom-admin");
        if (keyed)
        {
            builder.AddKeyedDekafKafkaAdminClient("messaging",
                settings => settings.ConnectionString = "settings:9092",
                (services, client) => client.WithClientId(services.GetRequiredService<string>()));
        }
        else
        {
            builder.AddDekafKafkaAdminClient("messaging",
                settings => settings.ConnectionString = "settings:9092",
                (services, client) => client.WithClientId(services.GetRequiredService<string>()));
        }

        await using var services = builder.Services.BuildServiceProvider();
        var client = keyed ? services.GetRequiredKeyedService<IAdminClient>("messaging") : services.GetRequiredService<IAdminClient>();
        var options = ClientTestHelpers.GetOptions<AdminClientOptions>(client);
        Assert.Equal(["settings:9092"], options.BootstrapServers);
        Assert.Equal("custom-admin", options.ClientId);
        var check = Assert.Single(services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
        Assert.Equal(keyed ? "Kafka.Dekaf_admin_messaging" : "Kafka.Dekaf_admin", check.Name);
    }

    [Fact]
    public async Task KeyedAdminClientsAreIsolatedAndDisposed()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.AddDekafKafkaAdminClient("messaging");
        builder.AddKeyedDekafKafkaAdminClient("other", settings => settings.ConnectionString = "other:9092");
        var services = builder.Services.BuildServiceProvider();
        var first = services.GetRequiredService<IAdminClient>();
        var second = services.GetRequiredKeyedService<IAdminClient>("other");
        Assert.NotSame(first, second);
        Assert.Same(first, services.GetRequiredService<IAdminClient>());
        Assert.Equal(["other:9092"], ClientTestHelpers.GetOptions<AdminClientOptions>(second).BootstrapServers);
        await services.DisposeAsync();
        Assert.True(((IKafkaClientStatusProvider)first).GetStatus().IsStopped);
        Assert.True(((IKafkaClientStatusProvider)second).GetStatus().IsStopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerHealthCheckTimesOutAgainstUnresponsiveServer(bool keyed)
    {
        // Keep the port reserved without speaking Kafka. This exercises a real protocol request
        // and avoids a race with another process claiming a port between allocation and use.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:messaging"] = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        builder.Configuration["Aspire:Kafka:Dekaf:AdminClient:HealthCheck:Timeout"] = "00:00:00.200";
        if (keyed)
        {
            builder.AddKeyedDekafKafkaAdminClient("messaging");
        }
        else
        {
            builder.AddDekafKafkaAdminClient("messaging");
        }

        await using var services = builder.Services.BuildServiceProvider();
        var report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Single(report.Entries);
    }

    [Fact]
    public void PublicArgumentsAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => AspireDekafKafkaAdminClientExtensions.AddDekafKafkaAdminClient(null!, "messaging"));
        Assert.Throws<ArgumentNullException>(() => AspireDekafKafkaAdminClientExtensions.AddKeyedDekafKafkaAdminClient(null!, "messaging"));
        var builder = ClientTestHelpers.CreateBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.AddDekafKafkaAdminClient(null!));
        Assert.Throws<ArgumentException>(() => builder.AddDekafKafkaAdminClient(""));
        Assert.Throws<ArgumentNullException>(() => builder.AddKeyedDekafKafkaAdminClient(null!));
        Assert.Throws<ArgumentException>(() => builder.AddKeyedDekafKafkaAdminClient(""));
    }
}
