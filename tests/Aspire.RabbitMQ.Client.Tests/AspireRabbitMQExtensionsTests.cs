// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Aspire.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit;

namespace Aspire.RabbitMQ.Client.Tests;

public class AspireRabbitMQExtensionsTests : IClassFixture<RabbitMQContainerFixture>
{
    private readonly RabbitMQContainerFixture _containerFixture;

    public AspireRabbitMQExtensionsTests(RabbitMQContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
    }

    [Theory]
    [RequiresFeature(TestFeature.Docker)]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", _containerFixture.GetConnectionString())
        ]);

        if (useKeyed)
        {
            builder.AddKeyedRabbitMQClient("messaging");
        }
        else
        {
            builder.AddRabbitMQClient("messaging");
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<IConnection>("messaging") :
            host.Services.GetRequiredService<IConnection>();

        AssertEquals(_containerFixture.GetConnectionString(), connection.Endpoint);
    }

    [Theory]
    [RequiresFeature(TestFeature.Docker)]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", "unused")
        ]);

        void SetConnectionString(RabbitMQClientSettings settings) => settings.ConnectionString = _containerFixture.GetConnectionString();
        if (useKeyed)
        {
            builder.AddKeyedRabbitMQClient("messaging", SetConnectionString);
        }
        else
        {
            builder.AddRabbitMQClient("messaging", SetConnectionString);
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<IConnection>("messaging") :
            host.Services.GetRequiredService<IConnection>();

        AssertEquals(_containerFixture.GetConnectionString(), connection.Endpoint);
    }

    [Theory]
    [RequiresFeature(TestFeature.Docker)]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:RabbitMQ:Client", key, "ConnectionString"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", _containerFixture.GetConnectionString())
        ]);

        if (useKeyed)
        {
            builder.AddKeyedRabbitMQClient("messaging");
        }
        else
        {
            builder.AddRabbitMQClient("messaging");
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<IConnection>("messaging") :
            host.Services.GetRequiredService<IConnection>();

        AssertEquals(_containerFixture.GetConnectionString(), connection.Endpoint);
    }

    [Fact]
    public void ConnectionFactoryOptionsFromConfig()
    {
        static Stream CreateStreamFromString(string data) => new MemoryStream(Encoding.UTF8.GetBytes(data));

        using var jsonStream = CreateStreamFromString("""
        {
          "Aspire": {
            "RabbitMQ": {
              "Client": {
                "ConnectionFactory": {
                  "AmqpUriSslProtocols": "Tls12",
                  "AutomaticRecoveryEnabled": false,
                  "ConsumerDispatchConcurrency": 2,
                  "SocketReadTimeout": "00:00:03",
                  "Ssl": {
                    "AcceptablePolicyErrors": "RemoteCertificateNameMismatch",
                    "Enabled": true,
                    "Version": "Tls13"
                  },
                  "RequestedFrameMax": 304,
                  "ClientProvidedName": "aspire-app"
                }
              }
            }
          }
        }
        """);

        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddJsonStream(jsonStream);

        builder.AddRabbitMQClient("messaging");

        using var host = builder.Build();
        var connectionFactory = (ConnectionFactory)host.Services.GetRequiredService<IConnectionFactory>();

        Assert.Equal(SslProtocols.Tls12, connectionFactory.AmqpUriSslProtocols);
        Assert.False(connectionFactory.AutomaticRecoveryEnabled);
        Assert.Equal(2, connectionFactory.ConsumerDispatchConcurrency);
        Assert.Equal(SslPolicyErrors.RemoteCertificateNameMismatch, connectionFactory.Ssl.AcceptablePolicyErrors);
        Assert.True(connectionFactory.Ssl.Enabled);
        Assert.Equal(SslProtocols.Tls13, connectionFactory.Ssl.Version);
        Assert.Equal(TimeSpan.FromSeconds(3), connectionFactory.SocketReadTimeout);
        Assert.Equal((uint)304, connectionFactory.RequestedFrameMax);
        Assert.Equal("aspire-app", connectionFactory.ClientProvidedName);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task CanAddMultipleKeyedServices()
    {
        await using var container2 = await RabbitMQContainerFixture.CreateContainerAsync();
        await using var container3 = await RabbitMQContainerFixture.CreateContainerAsync();

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging1", _containerFixture.GetConnectionString()),
            new KeyValuePair<string, string?>("ConnectionStrings:messaging2", container2.GetConnectionString()),
            new KeyValuePair<string, string?>("ConnectionStrings:messaging3", container3.GetConnectionString())
        ]);

        builder.AddRabbitMQClient("messaging1");
        builder.AddKeyedRabbitMQClient("messaging2");
        builder.AddKeyedRabbitMQClient("messaging3");

        using var host = builder.Build();

        var connection1 = host.Services.GetRequiredService<IConnection>();
        var connection2 = host.Services.GetRequiredKeyedService<IConnection>("messaging2");
        var connection3 = host.Services.GetRequiredKeyedService<IConnection>("messaging3");

        Assert.NotSame(connection1, connection2);
        Assert.NotSame(connection1, connection3);
        Assert.NotSame(connection2, connection3);

        AssertEquals(_containerFixture.GetConnectionString(), connection1.Endpoint);
        AssertEquals(container2.GetConnectionString(), connection2.Endpoint);
        AssertEquals(container3.GetConnectionString(), connection3.Endpoint);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AutoActivationCanSet(bool useKeyed, bool autoActivate)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", _containerFixture.GetConnectionString())
        ]);

        void EnableAutoActivation(RabbitMQClientSettings settings) => settings.DisableAutoActivation = !autoActivate;

        if (useKeyed)
        {
            builder.AddKeyedRabbitMQClient("messaging", EnableAutoActivation);
        }
        else
        {
            builder.AddRabbitMQClient("messaging", EnableAutoActivation);
        }

        var connection = builder.Services.BuildServiceProvider()
            .GetRequiredKeyedService<IConnection>(useKeyed ? "messaging" : null);

        var connectionActivated = false;
        builder.Services.AddKeyedSingleton<IConnection>(useKeyed ? "messaging" : null, (sp, key) =>
        {
            connectionActivated = true;
            return connection;
        });

        using var host = builder.Build();
        await host.StartAsync();

        // When auto activation is enabled, the service should be registered and activated
        Assert.Equal(autoActivate, connectionActivated);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AutoActivationDisabledByDefault()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", _containerFixture.GetConnectionString())
        ]);

        builder.AddRabbitMQClient("messaging");

        var connection = builder.Services.BuildServiceProvider()
            .GetRequiredService<IConnection>();

        var connectionActivated = false;
        builder.Services.AddSingleton<IConnection>(sp =>
        {
            connectionActivated = true;
            return connection;
        });

        using var host = builder.Build();
        await host.StartAsync();

        // When auto activation is disabled, the service should not be activated
        Assert.False(connectionActivated);
    }

    private static void AssertEquals(string expectedUri, AmqpTcpEndpoint endpoint)
    {
        var uri = new Uri(expectedUri);
        Assert.Equal(uri.Host, endpoint.HostName);
        Assert.Equal(uri.Port, endpoint.Port);
    }
}
