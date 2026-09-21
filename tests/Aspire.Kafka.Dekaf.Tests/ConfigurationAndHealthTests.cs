// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dekaf.Consumer;
using Dekaf.Extensions.HealthChecks;
using Dekaf.Producer;
using Dekaf.Security.Sasl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

public class ConfigurationAndHealthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionStringOverridesPreserveSecurityOptions(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        foreach (var role in new[] { "Producer", "Consumer" })
        {
            builder.Configuration[$"Aspire:Kafka:Dekaf:{role}:Config:UseTls"] = "true";
            builder.Configuration[$"Aspire:Kafka:Dekaf:{role}:Config:SaslMechanism"] = "ScramSha512";
            builder.Configuration[$"Aspire:Kafka:Dekaf:{role}:Config:SaslUsername"] = "test-user";
            builder.Configuration[$"Aspire:Kafka:Dekaf:{role}:Config:SaslPassword"] = "test-password";
        }

        if (keyed)
        {
            builder.AddKeyedDekafKafkaProducer<string, string>("messaging");
            builder.AddKeyedDekafKafkaConsumer<string, string>("messaging");
        }
        else
        {
            builder.AddDekafKafkaProducer<string, string>("messaging");
            builder.AddDekafKafkaConsumer<string, string>("messaging");
        }

        await using var services = builder.Services.BuildServiceProvider();
        var producer = ClientTestHelpers.GetOptions<ProducerOptions>(ClientTestHelpers.GetProducer(services, keyed));
        var consumer = ClientTestHelpers.GetOptions<ConsumerOptions>(ClientTestHelpers.GetConsumer(services, keyed));
        Assert.True(producer.UseTls);
        Assert.True(consumer.UseTls);
        Assert.Equal(SaslMechanism.ScramSha512, producer.SaslMechanism);
        Assert.Equal(SaslMechanism.ScramSha512, consumer.SaslMechanism);
        Assert.Equal("test-user", producer.SaslUsername);
        Assert.Equal("test-user", consumer.SaslUsername);
        Assert.Equal("test-password", producer.SaslPassword);
        Assert.Equal("test-password", consumer.SaslPassword);
        Assert.Equal(["localhost:19092"], producer.BootstrapServers);
        Assert.Equal(["localhost:19092"], consumer.BootstrapServers);
    }

    [Fact]
    public async Task HealthCheckOptionsAreBoundAndNamedOverridesWin()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["Aspire:Kafka:Dekaf:Consumer:HealthCheck:DegradedThreshold"] = "50";
        builder.Configuration["Aspire:Kafka:Dekaf:Consumer:HealthCheck:UnhealthyThreshold"] = "100";
        builder.Configuration["Aspire:Kafka:Dekaf:Consumer:messaging:HealthCheck:DegradedThreshold"] = "75";
        builder.Configuration["Aspire:Kafka:Dekaf:Consumer:messaging:HealthCheck:Timeout"] = "00:00:02";
        builder.AddKeyedDekafKafkaConsumer<string, string>("messaging");
        await using var services = builder.Services.BuildServiceProvider();
        var registration = Assert.Single(services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
        var options = ClientTestHelpers.GetOptions<DekafConsumerHealthCheckOptions>(registration.Factory(services));
        Assert.Equal(75, options.DegradedThreshold);
        Assert.Equal(100, options.UnhealthyThreshold);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Timeout);
    }

    [Fact]
    public async Task ProducerWithoutInitializationIsUnhealthy()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.AddDekafKafkaProducer<string, string>("messaging");
        await using var services = builder.Services.BuildServiceProvider();
        var report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        var check = Assert.Single(report.Entries).Value;
        Assert.Equal(HealthStatus.Unhealthy, check.Status);
        Assert.IsType<InvalidOperationException>(check.Exception);
    }

    [Fact]
    public async Task ConsumerWithoutLiveMembershipIsUnhealthy()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.AddDekafKafkaConsumer<string, string>("messaging");
        await using var services = builder.Services.BuildServiceProvider();
        var report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Single(report.Entries);
    }

    [Theory]
    [InlineData("Producer")]
    [InlineData("Consumer")]
    [InlineData("AdminClient")]
    public void InvalidBooleanConfigurationFailsAtRegistration(string role)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration[$"Aspire:Kafka:Dekaf:{role}:DisableMetrics"] = "invalid";
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (role)
            {
                case "Producer":
                    builder.AddDekafKafkaProducer<string, string>("messaging");
                    break;
                case "Consumer":
                    builder.AddDekafKafkaConsumer<string, string>("messaging");
                    break;
                default:
                    builder.AddDekafKafkaAdminClient("messaging");
                    break;
            }
        });
    }

    [Fact]
    public async Task TransactionalProducerCanBeConfigured()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.AddDekafKafkaProducer<string, string>("messaging", producer => producer.WithTransactionalId("orders-transaction"));
        await using var services = builder.Services.BuildServiceProvider();
        var options = ClientTestHelpers.GetOptions<ProducerOptions>(ClientTestHelpers.GetProducer(services, keyed: false));
        Assert.Equal("orders-transaction", options.TransactionalId);
        Assert.True(options.EnableIdempotence);
    }
}
