// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit;

#if !RABBITMQ_V6
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
#endif

namespace Aspire.RabbitMQ.Client.Tests;

public class ConformanceTests : ConformanceTests<IConnection, RabbitMQClientSettings>, IClassFixture<RabbitMQContainerFixture>
{
    private readonly RabbitMQContainerFixture? _containerFixture;
    private string ConnectionString { get; set; }

    public ConformanceTests(RabbitMQContainerFixture? containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
                                    ? _containerFixture.GetConnectionString()
                                    : "amqp://localhost:5672";
    }

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    // IConnectionMultiplexer can be created only via call to ConnectionMultiplexer.Connect
    protected override bool CanCreateClientWithoutConnectingToServer => false;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override bool SupportsKeyedRegistrations => true;

    protected override string[] RequiredLogCategories => Array.Empty<string>();

#if RABBITMQ_V6
    protected override string ActivitySourceName => "Aspire.RabbitMQ.Client";
#else
    protected override string ActivitySourceName => "RabbitMQ.Client.Publisher";
#endif

    protected override string? ConfigurationSectionName => "Aspire:RabbitMQ:Client";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "RabbitMQ": {
              "Client": {
                "ConnectionFactory": {
                  "AutomaticRecoveryEnabled": false,
                  "ConsumerDispatchConcurrency": 2,
                  "Ssl": {
                    "AcceptablePolicyErrors": "None",
                    "Enabled": false,
                    "Version": "Tls13"
                  }
                },
                "ConnectionString": "amqp://localhost:5672",
                "MaxConnectRetryCount": 10,
                "DisableHealthChecks": false,
                "DisableTracing": true
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "RabbitMQ": { "Client":{ "ConnectionFactory": "YOUR_OPTION"}}}}""", "Value is \"string\" but should be \"object\""),
            ("""{"Aspire": { "RabbitMQ": { "Client":{ "ConnectionFactory": { "AmqpUriSslProtocols": "Fast"}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "RabbitMQ": { "Client":{ "ConnectionFactory": { "Ssl":{ "AcceptablePolicyErrors": "Fast"}}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "RabbitMQ": { "Client":{ "ConnectionFactory": { "Ssl":{ "Version": "Fast"}}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "RabbitMQ": { "Client":{ "ConnectionFactory": { "RequestedConnectionTimeout": "3S"}}}}}""", "The string value is not a match for the indicated regular expression")
        };

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection([
            new(CreateConfigKey("Aspire:RabbitMQ:Client", key, "ConnectionString"), ConnectionString)
        ]);

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<RabbitMQClientSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddRabbitMQClient("rabbit", configure);
        }
        else
        {
            builder.AddKeyedRabbitMQClient(key, configure);
        }
    }

    protected override void SetHealthCheck(RabbitMQClientSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void DisableRetries(RabbitMQClientSettings options)
    {
        options.MaxConnectRetryCount = 0;
    }

    protected override void SetTracing(RabbitMQClientSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(RabbitMQClientSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void TriggerActivity(IConnection service)
    {
#if RABBITMQ_V6
        var channel = service.CreateModel();
        channel.QueueDeclare("test-queue", exclusive: false);
        channel.BasicPublish(
            exchange: "",
            routingKey: "test-queue",
            basicProperties: null,
            body: "hello world"u8.ToArray());
#else
        Task.Run(async () =>
        {
            using var channel = await service.CreateChannelAsync();
            await channel.QueueDeclareAsync("test-queue", exclusive: false);
            await channel.BasicPublishAsync(
                exchange: "",
                routingKey: "test-queue",
                body: "hello world"u8.ToArray());
        }).Wait();
#endif
    }

    protected override void SetupConnectionInformationIsDelayValidated()
    {
        Assert.Skip("RabbitMQ connects to localhost by default if the connection information isn't available.");
    }

#if !RABBITMQ_V6
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(static connectionStringToUse =>
            RunWithConnectionString(connectionStringToUse, static obj => obj.RunActivitySourceTest(key: null)),
            ConnectionString, Output);

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(static connectionStringToUse =>
            RunWithConnectionString(connectionStringToUse, static obj => obj.RunActivitySourceTest(key: "key")),
            ConnectionString, Output);

    private void RunActivitySourceTest(string? key)
    {
        HostApplicationBuilder builder = CreateHostBuilder(key: key);
        builder.Logging.AddConsole();
        RegisterComponent(builder, options => SetTracing(options, true), key);

        List<Activity> exportedActivities = [];
        builder.Services.AddOpenTelemetry().WithTracing(traceBuilder => traceBuilder.AddInMemoryExporter(exportedActivities));

        using IHost host = builder.Build();
        host.Start();

        IConnection service = key is null
            ? host.Services.GetRequiredService<IConnection>()
            : host.Services.GetRequiredKeyedService<IConnection>(key);

        // Clear activities generated during connection establishment (from "Aspire.RabbitMQ.Client" source)
        exportedActivities.Clear();

        TriggerActivity(service);

        Assert.NotEmpty(exportedActivities);
        Assert.Contains(exportedActivities, activity => activity.Source.Name == ActivitySourceName);
    }

    private static void RunWithConnectionString(string connectionString, Action<ConformanceTests> test)
        => test(new ConformanceTests(null) { ConnectionString = connectionString });
#endif
}
