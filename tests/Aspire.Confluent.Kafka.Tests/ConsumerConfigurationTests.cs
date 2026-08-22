// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Confluent.Kafka.Tests;

public class ConsumerConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", CommonHelpers.TestingEndpoint),
            new KeyValuePair<string, string?>(ConsumerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"), "unused")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedKafkaConsumer<string, string>("messaging");
        }
        else
        {
            builder.AddKafkaConsumer<string, string>("messaging");
        }

        using var host = builder.Build();
        var connectionFactory = useKeyed
            ? host.Services.GetRequiredKeyedService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value, "messaging")
            : host.Services.GetRequiredService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value);

        var config = GetConsumerConfig(connectionFactory)!;

        Assert.Equal(CommonHelpers.TestingEndpoint, config.BootstrapServers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", "unused"),
            new KeyValuePair<string, string?>(ConsumerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"), "unused")
        ]);

        static void SetConnectionString(KafkaConsumerSettings settings) => settings.ConnectionString = CommonHelpers.TestingEndpoint;
        if (useKeyed)
        {
            builder.AddKeyedKafkaConsumer<string, string>("messaging", configureSettings: SetConnectionString);
        }
        else
        {
            builder.AddKafkaConsumer<string, string>("messaging", configureSettings: SetConnectionString);
        }

        using var host = builder.Build();
        var connectionFactory = useKeyed
            ? host.Services.GetRequiredKeyedService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value, "messaging")
            : host.Services.GetRequiredService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value);

        var config = GetConsumerConfig(connectionFactory)!;

        Assert.Equal(CommonHelpers.TestingEndpoint, config.BootstrapServers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConsumerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "ConnectionString"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", CommonHelpers.TestingEndpoint),
            new KeyValuePair<string, string?>(ConsumerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"), "unused")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedKafkaConsumer<string, string>("messaging");
        }
        else
        {
            builder.AddKafkaConsumer<string, string>("messaging");
        }

        using var host = builder.Build();
        var connectionFactory = useKeyed
            ? host.Services.GetRequiredKeyedService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value, "messaging")
            : host.Services.GetRequiredService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value);

        var config = GetConsumerConfig(connectionFactory)!;

        Assert.Equal(CommonHelpers.TestingEndpoint, config.BootstrapServers);
    }

    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]

    public void ConfigureConsumerBuilder(bool useKeyed, bool useConfigureSettings, bool useConfigureBuilder, bool useConfigureBuilderWithServiceProvider)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", CommonHelpers.TestingEndpoint),
            new KeyValuePair<string, string?>(ConsumerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"), "unused")
        ]);

        bool configureBuilderIsCalled = false, configureSettingsIsCalled = false;

        Action act =
            (useKeyed, useConfigureSettings, useConfigureBuilder, useConfigureBuilderWithServiceProvider) switch
            {
                (true, false, false, false) => () => builder.AddKeyedKafkaConsumer<string, string>("messaging"),
                (false, false, false, false) => () => builder.AddKafkaConsumer<string, string>("messaging"),

                // only configureSettings
                (true, true, false, false) => () => builder.AddKeyedKafkaConsumer<string, string>("messaging",
                    configureSettings: ConfigureSettings),
                (false, true, false, false) => () => builder.AddKafkaConsumer<string, string>("messaging",
                    configureSettings: ConfigureSettings),

                // only configureBuilder
                (true, false, true, false) => () => builder.AddKeyedKafkaConsumer<string, string>("messaging",
                    configureBuilder: ConfigureBuilder),
                (false, false, true, false) => () => builder.AddKafkaConsumer<string, string>("messaging",
                    configureBuilder: ConfigureBuilder),

                (true, false, false, true) => () => builder.AddKeyedKafkaConsumer<string, string>("messaging",
                    configureBuilder: ConfigureBuilderWithServiceProvider),
                (false, false, false, true) => () => builder.AddKafkaConsumer<string, string>("messaging",
                    configureBuilder: ConfigureBuilderWithServiceProvider),

                // both configureSettings, and configureBuilder
                (true, true, true, false) => () => builder.AddKeyedKafkaConsumer<string, string>("messaging",
                    configureSettings: ConfigureSettings,
                    configureBuilder: ConfigureBuilder),
                (false, true, true, false) => () => builder.AddKafkaConsumer<string, string>("messaging",
                    configureSettings: ConfigureSettings,
                    configureBuilder: ConfigureBuilder),

                (true, true, false, true) => () => builder.AddKeyedKafkaConsumer<string, string>("messaging",
                    configureSettings: ConfigureSettings,
                    configureBuilder: ConfigureBuilderWithServiceProvider),
                (false, true, false, true) => () => builder.AddKafkaConsumer<string, string>("messaging",
                    configureSettings: ConfigureSettings,
                    configureBuilder: ConfigureBuilderWithServiceProvider),

                _ => throw new InvalidOperationException()
            };

        act();

        using var host = builder.Build();
        var connectionFactory = useKeyed
            ? host.Services.GetRequiredKeyedService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value, "messaging")
            : host.Services.GetRequiredService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value);

        var config = GetConsumerConfig(connectionFactory)!;

        if (useConfigureBuilder || useConfigureBuilderWithServiceProvider)
        {
            Assert.True(configureBuilderIsCalled);
        }

        if (useConfigureSettings)
        {
            Assert.True(configureSettingsIsCalled);
        }

        Assert.Equal(CommonHelpers.TestingEndpoint, config.BootstrapServers);
        return;

        void ConfigureBuilder(ConsumerBuilder<string, string> _)
        {
            configureBuilderIsCalled = true;
        }

        void ConfigureBuilderWithServiceProvider(IServiceProvider provider, ConsumerBuilder<string, string> _)
        {
            var __ = provider.GetRequiredService<IConfiguration>();
            configureBuilderIsCalled = true;
        }

        void ConfigureSettings(KafkaConsumerSettings _)
        {
            configureSettingsIsCalled = true;
        }
    }

    [Fact]
    public void ConsumerConfigOptionsFromConfig()
    {
        static Stream CreateStreamFromString(string data) => new MemoryStream(Encoding.UTF8.GetBytes(data));

        using var jsonStream = CreateStreamFromString("""
        {
          "Aspire": {
            "Confluent": {
              "Kafka": {
                "Consumer": {
                  "Config": {
                    "BootstrapServers": "localhost:9092",
                    "AutoOffsetReset": "Earliest",
                    "GroupId": "consumer-group",
                    "SaslUsername": "user",
                    "SaslPassword": "password",
                    "SaslMechanism": "Plain",
                    "SecurityProtocol": "Plaintext"
                  }
                }
              }
            }
          }
        }
        """);

        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddJsonStream(jsonStream);

        builder.AddKafkaConsumer<string, string>("messaging");

        using var host = builder.Build();
        var connectionFactory = host.Services.GetRequiredService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value);

        var config = GetConsumerConfig(connectionFactory)!;

        Assert.Equal(AutoOffsetReset.Earliest, config.AutoOffsetReset);
        Assert.Equal("consumer-group", config.GroupId);
        Assert.Equal("user", config.SaslUsername);
        Assert.Equal("password", config.SaslPassword);
        Assert.Equal(SaslMechanism.Plain, config.SaslMechanism);
        Assert.Equal(SecurityProtocol.Plaintext, config.SecurityProtocol);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsSaslCredentialsFromConnectionString(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", CommonHelpers.TestingSaslConnectionString),
            new KeyValuePair<string, string?>(ConsumerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"), "unused")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedKafkaConsumer<string, string>("messaging");
        }
        else
        {
            builder.AddKafkaConsumer<string, string>("messaging");
        }

        using var host = builder.Build();
        var connectionFactory = useKeyed ?
            host.Services.GetRequiredKeyedService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value, "messaging") :
            host.Services.GetRequiredService(ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value);

        var config = GetConsumerConfig(connectionFactory)!;

        Assert.Equal(CommonHelpers.TestingEndpoint, config.BootstrapServers);
        Assert.Equal(SecurityProtocol.SaslPlaintext, config.SecurityProtocol);
        Assert.Equal(SaslMechanism.Plain, config.SaslMechanism);
        Assert.Equal("kafka", config.SaslUsername);
        Assert.Equal(CommonHelpers.TestingPassword, config.SaslPassword);
    }

    private static ConsumerConfig? GetConsumerConfig(object o) => ReflectionHelpers.ConsumerConnectionFactoryStringKeyStringValueType.Value.GetProperty("Config")!.GetValue(o) as ConsumerConfig;
}
