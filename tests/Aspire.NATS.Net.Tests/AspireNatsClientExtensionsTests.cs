// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.TestUtilities;
using Aspire.TestUtilities;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.NATS.Net.Tests;

public class AspireNatsClientExtensionsTests : IClassFixture<NatsContainerFixture>
{
    private const string DefaultConnectionName = "nats";

    private readonly NatsContainerFixture _containerFixture;
    private readonly string _connectionString;

    public AspireNatsClientExtensionsTests(NatsContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
        _connectionString = RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker)
            ? _containerFixture.GetConnectionString()
            : "nats://aspire-host:4222";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:nats", _connectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats");
        }
        else
        {
            builder.AddNatsClient("nats");
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Equal(_connectionString, connection.Opts.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanContainUserAndPassword(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:nats", "nats://nats:password@aspire-host:4222")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats");
        }
        else
        {
            builder.AddNatsClient("nats");
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Equal("nats://nats:password@aspire-host:4222", connection.Opts.Url);
        Assert.Equal("nats", connection.Opts.AuthOpts.Username);
        Assert.Equal("password", connection.Opts.AuthOpts.Password);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:nats", "unused")
        ]);

        void SetConnectionString(NatsClientSettings settings) => settings.ConnectionString = _connectionString;

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats", SetConnectionString);
        }
        else
        {
            builder.AddNatsClient("nats", SetConnectionString);
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Equal(_connectionString, connection.Opts.Url);
        // the connection string from config should not be used since code set it explicitly
        Assert.DoesNotContain("unused", connection.Opts.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "nats" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Nats:Client", key, "ConnectionString"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:nats", _connectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats");
        }
        else
        {
            builder.AddNatsClient("nats");
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Equal(_connectionString, connection.Opts.Url);
        // the connection string from config should not be used since it was found in ConnectionStrings
        Assert.DoesNotContain("unused", connection.Opts.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddNatsClient_HealthCheckShouldBeRegisteredByDefault(bool useKeyed)
    {
        var key = DefaultConnectionName;
        var builder = CreateBuilder(_connectionString);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient(key, ConfigureNoRetryOptions);
        }
        else
        {
            builder.AddNatsClient(DefaultConnectionName, ConfigureNoRetryOptions);
        }

        using var host = builder.Build();

        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        var healthCheckReport = await healthCheckService.CheckHealthAsync();

        var healthCheckName = useKeyed ? $"NATS_{key}" : "NATS";

        Assert.Contains(healthCheckReport.Entries, x => x.Key == healthCheckName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddNatsClient_HealthCheckReportsConnectionFailureDescription(bool useKeyed)
    {
        var key = DefaultConnectionName;
        var builder = CreateBuilder($"nats://127.0.0.1:{ComponentTestUrls.GetUnusedTcpPort()}");

        if (useKeyed)
        {
            builder.AddKeyedNatsClient(key, ConfigureNoRetryOptions);
        }
        else
        {
            builder.AddNatsClient(DefaultConnectionName, ConfigureNoRetryOptions);
        }

        using var host = builder.Build();
        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthCheckReport = await healthCheckService.CheckHealthAsync(cts.Token);
        var healthCheckName = useKeyed ? $"NATS_{key}" : "NATS";
        Assert.True(healthCheckReport.Entries.TryGetValue(healthCheckName, out var entry));

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal("Failed to connect to NATS server.", entry.Description);
        Assert.NotNull(entry.Exception);
    }

    private static NatsOpts ConfigureNoRetryOptions(NatsOpts options)
        => options with
        {
            RetryOnInitialConnect = false,
            MaxReconnectRetry = 0,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(1),
            ReconnectWaitMax = TimeSpan.FromMilliseconds(1)
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddNatsClient_HealthCheckShouldNotBeRegisteredWhenDisabled(bool useKeyed)
    {
        var builder = CreateBuilder(_connectionString);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient(DefaultConnectionName, settings =>
            {
                settings.DisableHealthChecks = true;
            });
        }
        else
        {
            builder.AddNatsClient(DefaultConnectionName, settings =>
            {
                settings.DisableHealthChecks = true;
            });
        }

        using var host = builder.Build();

        var healthCheckService = host.Services.GetService<HealthCheckService>();

        Assert.Null(healthCheckService);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void NatsInstrumentationEndToEnd()
    {
        RemoteExecutor.Invoke(async (connectionString) =>
        {
            var builder = CreateBuilder(connectionString);

            builder.AddNatsClient(DefaultConnectionName);

            using var notifier = new ActivityNotifier();
            builder.Services.AddOpenTelemetry().WithTracing(builder => builder.AddProcessor(notifier));

            using var host = builder.Build();
            host.Start();

            var nats = host.Services.GetRequiredService<INatsConnection>();
            await nats.PublishAsync("test");

            var activityList = await notifier.TakeAsync(1, TimeSpan.FromSeconds(10));
            Assert.Single(activityList);

            var activity = activityList[0];
            Assert.Equal("test publish", activity.OperationName);
            Assert.Contains(activity.Tags, kvp => kvp.Key == "messaging.system" && kvp.Value == "nats");
        }, _connectionString).Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistersINatsClient(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:nats", _connectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedNatsClient("nats");
        }
        else
        {
            builder.AddNatsClient("nats");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsClient>("nats") :
            host.Services.GetRequiredService<INatsClient>();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<INatsConnection>("nats") :
            host.Services.GetRequiredService<INatsConnection>();

        Assert.Same(connection, client);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:nats1", "nats://aspire-host1:4222"),
            new KeyValuePair<string, string?>("ConnectionStrings:nats2", "nats://aspire-host2:4222"),
            new KeyValuePair<string, string?>("ConnectionStrings:nats3", "nats://aspire-host3:4222"),
        ]);

        builder.AddNatsClient("nats1");
        builder.AddKeyedNatsClient("nats2");
        builder.AddKeyedNatsClient("nats3");

        using var host = builder.Build();

        var connection1 = host.Services.GetRequiredService<INatsConnection>();
        var connection2 = host.Services.GetRequiredKeyedService<INatsConnection>("nats2");
        var connection3 = host.Services.GetRequiredKeyedService<INatsConnection>("nats3");

        Assert.NotSame(connection1, connection2);
        Assert.NotSame(connection1, connection3);
        Assert.NotSame(connection2, connection3);

        Assert.Contains("aspire-host1", connection1.Opts.Url);
        Assert.Contains("aspire-host2", connection2.Opts.Url);
        Assert.Contains("aspire-host3", connection3.Opts.Url);
    }

    private static HostApplicationBuilder CreateBuilder(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>($"ConnectionStrings:{DefaultConnectionName}", connectionString)
        ]);
        return builder;
    }
}
