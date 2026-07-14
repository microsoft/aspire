// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Xunit;

namespace Aspire.Mcp.Client.Tests;

public class AspireMcpClientExtensionsTests
{
    [Fact]
    public void AddMcpClientRegistersUnkeyedClient()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddMcpClient("mcp");

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(McpClient) && descriptor.ServiceKey is null);
    }

    [Fact]
    public void AddKeyedMcpClientRegistersKeyedClient()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddKeyedMcpClient("mcp");

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(McpClient) && Equals(descriptor.ServiceKey, "mcp"));
    }

    [Fact]
    public void McpClientUsesServiceDiscoveryEndpoint()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        Action resolveClient = () => _ = host.Services.GetRequiredService<McpClient>();

        var exception = Record.Exception(resolveClient);

        Assert.NotNull(exception);
        Assert.All(handler.RequestUris, uri => Assert.Equal("https://mcp/mcp", uri.ToString()));
    }

    [Fact]
    public void KeyedMcpClientsUseTheirOwnServiceDiscoveryEndpoints()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddKeyedMcpClient("weather");
        builder.AddKeyedMcpClient("calendar");

        using var host = builder.Build();

        _ = Record.Exception(() => _ = host.Services.GetRequiredKeyedService<McpClient>("weather"));
        _ = Record.Exception(() => _ = host.Services.GetRequiredKeyedService<McpClient>("calendar"));

        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://weather/mcp");
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://calendar/mcp");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddMcpClientValidatesConnectionName(bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionName = isNull ? null! : string.Empty;

        Action action = () =>
        {
            builder.AddMcpClient(connectionName);
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);

        Assert.Equal(nameof(connectionName), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeyedMcpClientValidatesName(bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var name = isNull ? null! : string.Empty;

        Action action = () =>
        {
            builder.AddKeyedMcpClient(name);
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);

        Assert.Equal(nameof(name), exception.ParamName);
    }

    private sealed class RequestRecordingHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
