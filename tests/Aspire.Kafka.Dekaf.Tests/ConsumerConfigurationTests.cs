// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dekaf;
using Dekaf.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

public class ConsumerConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationPrecedenceAndNativeOptions(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aspire:Kafka:Dekaf:Consumer:ConnectionString"] = "default:9092",
            ["Aspire:Kafka:Dekaf:Consumer:Config:ClientId"] = "default-client",
            ["Aspire:Kafka:Dekaf:Consumer:Config:RequestTimeoutMs"] = "2345",
            ["Aspire:Kafka:Dekaf:Consumer:messaging:ConnectionString"] = "named:9092",
            ["Aspire:Kafka:Dekaf:Consumer:messaging:Config:ClientId"] = "named-client",
            ["Aspire:Kafka:Dekaf:Consumer:messaging:DisableHealthChecks"] = "true"
        });

        Register(builder, keyed);
        await using var services = builder.Services.BuildServiceProvider();
        var client = ClientTestHelpers.GetConsumer(services, keyed);
        var options = ClientTestHelpers.GetOptions<ConsumerOptions>(client);

        Assert.Equal(["localhost:19092"], options.BootstrapServers);
        Assert.Equal("named-client", options.ClientId);
        Assert.Equal(2345, options.RequestTimeoutMs);
        Assert.Null(services.GetService<HealthCheckService>());
        Assert.Same(client, ClientTestHelpers.GetConsumer(services, keyed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsAndBuilderOverrideConfiguration(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        var settingsCalls = 0;
        var builderCalls = 0;
        builder.Services.AddSingleton("service-client");
        Register(builder, keyed, settings =>
        {
            settingsCalls++;
            settings.ConnectionString = "settings:9092";
        }, (services, client) =>
        {
            builderCalls++;
            client.WithBootstrapServers("builder:9092").WithClientId(services.GetRequiredService<string>());
        });
        Assert.Equal(1, settingsCalls);
        Assert.Equal(0, builderCalls);

        await using var services = builder.Services.BuildServiceProvider();
        var client = ClientTestHelpers.GetConsumer(services, keyed);
        var options = ClientTestHelpers.GetOptions<ConsumerOptions>(client);
        Assert.Equal(["builder:9092"], options.BootstrapServers);
        Assert.Equal("service-client", options.ClientId);
        Assert.Same(client, ClientTestHelpers.GetConsumer(services, keyed));
        Assert.Equal(1, builderCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanConfigureNativeOptionsWithoutConnectionString(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:messaging"] = null;
        Register(builder, keyed, configureBuilder: (_, client) => client
            .WithBootstrapServers("native:9092", "backup:9092")
            .WithClientId("native-client"));
        await using var services = builder.Services.BuildServiceProvider();
        var options = ClientTestHelpers.GetOptions<ConsumerOptions>(ClientTestHelpers.GetConsumer(services, keyed));
        Assert.Equal(["native:9092", "backup:9092"], options.BootstrapServers);
        Assert.Equal("native-client", options.ClientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsBootstrapServersFromConfigArray(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:messaging"] = null;
        builder.Configuration["Aspire:Kafka:Dekaf:Consumer:Config:BootstrapServers:0"] = "first:9092";
        builder.Configuration["Aspire:Kafka:Dekaf:Consumer:Config:BootstrapServers:1"] = "second:9092";
        Register(builder, keyed);
        await using var services = builder.Services.BuildServiceProvider();
        var options = ClientTestHelpers.GetOptions<ConsumerOptions>(ClientTestHelpers.GetConsumer(services, keyed));
        Assert.Equal(["first:9092", "second:9092"], options.BootstrapServers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TelemetryAndHealthChecksCanBeDisabled(bool keyed, bool disabled)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        Register(builder, keyed, settings =>
        {
            settings.DisableHealthChecks = disabled;
            settings.DisableMetrics = disabled;
            settings.DisableTracing = disabled;
        });
        await using var services = builder.Services.BuildServiceProvider();
        Assert.Equal(!disabled, services.GetService<HealthCheckService>() is not null);
        Assert.Equal(!disabled, services.GetService<MeterProvider>() is not null);
        Assert.Equal(!disabled, services.GetService<TracerProvider>() is not null);
    }

    [Fact]
    public async Task KeyedClientsAreIsolatedAndDisposedByContainer()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.AddDekafKafkaConsumer<string, string>("messaging");
        builder.AddKeyedDekafKafkaConsumer<string, string>("messaging");
        builder.AddKeyedDekafKafkaConsumer<string, string>("other", settings => settings.ConnectionString = "other:9092");
        var services = builder.Services.BuildServiceProvider();
        var first = services.GetRequiredService<IKafkaConsumer<string, string>>();
        var second = services.GetRequiredKeyedService<IKafkaConsumer<string, string>>("messaging");
        var third = services.GetRequiredKeyedService<IKafkaConsumer<string, string>>("other");
        Assert.NotSame(first, second);
        Assert.NotSame(second, third);
        Assert.Equal(["other:9092"], ClientTestHelpers.GetOptions<ConsumerOptions>(third).BootstrapServers);
        var checks = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Equal(["Kafka.Dekaf_consumer", "Kafka.Dekaf_consumer_messaging", "Kafka.Dekaf_consumer_other"], checks.Select(check => check.Name));

        await services.DisposeAsync();
        Assert.True(((global::Dekaf.Diagnostics.IKafkaClientStatusProvider)first).GetStatus().IsStopped);
        Assert.True(((global::Dekaf.Diagnostics.IKafkaClientStatusProvider)second).GetStatus().IsStopped);
        Assert.True(((global::Dekaf.Diagnostics.IKafkaClientStatusProvider)third).GetStatus().IsStopped);
    }

    [Fact]
    public async Task DuplicateRegistrationDoesNotDuplicateHealthChecks()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        Register(builder, keyed: false);
        Register(builder, keyed: false);
        await using var services = builder.Services.BuildServiceProvider();
        Assert.Single(services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingBootstrapServersFailsOnResolution(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:messaging"] = null;
        Register(builder, keyed);
        await using var services = builder.Services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => ClientTestHelpers.GetConsumer(services, keyed));
    }

    private static void Register(HostApplicationBuilder builder, bool keyed,
        Action<KafkaConsumerSettings>? configureSettings = null,
        Action<IServiceProvider, ConsumerBuilder<string, string>>? configureBuilder = null)
    {
        if (keyed)
        {
            builder.AddKeyedDekafKafkaConsumer("messaging", configureSettings, configureBuilder);
        }
        else
        {
            builder.AddDekafKafkaConsumer("messaging", configureSettings, configureBuilder);
        }
    }
}
