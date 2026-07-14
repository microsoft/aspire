// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using System.Net;
using System.Text;
using System.Text.Json;
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
    public void AddMcpClientInvokesConfigurationDelegates()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        var clientOptionsConfigured = false;
        var transportOptionsConfigured = false;

        builder.AddMcpClient(
            "mcp",
            _ => clientOptionsConfigured = true,
            options =>
            {
                transportOptionsConfigured = true;
                options.Endpoint = new Uri("https://custom/mcp", UriKind.Absolute);
            });

        using var host = builder.Build();
        _ = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.True(clientOptionsConfigured);
        Assert.True(transportOptionsConfigured);
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://custom/mcp");
    }

    [Fact]
    public void AddKeyedMcpClientInvokesConfigurationDelegates()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        var clientOptionsConfigured = false;
        var transportOptionsConfigured = false;

        builder.AddKeyedMcpClient(
            "mcp",
            _ => clientOptionsConfigured = true,
            options =>
            {
                transportOptionsConfigured = true;
                options.Endpoint = new Uri("https://keyed/mcp", UriKind.Absolute);
            });

        using var host = builder.Build();
        _ = Record.Exception(() => _ = host.Services.GetRequiredKeyedService<McpClient>("mcp"));

        Assert.True(clientOptionsConfigured);
        Assert.True(transportOptionsConfigured);
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://keyed/mcp");
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
        Assert.NotEmpty(handler.RequestUris);
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

    [Fact]
    public void AddMcpClientAllowsSynchronousHostDisposalAfterSuccessfulResolution()
    {
        var handler = new SuccessfulInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        var host = builder.Build();
        var resolveException = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Null(resolveException);
        Assert.Null(Record.Exception(host.Dispose));
    }

    [Fact]
    public void AddMcpClientDisposesHttpClientTransportOnHostDisposal()
    {
        var handler = new SuccessfulInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMcpClient("mcp");
        builder.Services.AddSingleton<IHttpClientFactory>(new TrackingHttpClientFactory(handler));

        var host = builder.Build();
        var resolveException = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Null(resolveException);
        Assert.Null(Record.Exception(host.Dispose));
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task AddMcpClientDisposesHttpClientTransportOnAsyncHostDisposal()
    {
        var handler = new SuccessfulInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMcpClient("mcp");
        builder.Services.AddSingleton<IHttpClientFactory>(new TrackingHttpClientFactory(handler));

        var host = builder.Build();
        var resolveException = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Null(resolveException);
        await ((IAsyncDisposable)host).DisposeAsync();
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task AddMcpClientCancelsInFlightInitializationOnHostDisposal()
    {
        var handler = new BlockingInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        var host = builder.Build();
        var resolutionTask = Task.Run(() => Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>()));
        await handler.InitializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var disposeException = Record.Exception(host.Dispose);
        var resolutionException = await resolutionTask;

        Assert.Null(disposeException);
        Assert.NotNull(resolutionException);
        Assert.IsAssignableFrom<OperationCanceledException>(resolutionException);
        Assert.True(handler.InitializeCanceled);
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

    private sealed class SuccessfulInitializationHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var requestJson = JsonDocument.Parse(requestBody);
            if (requestJson.RootElement.TryGetProperty("method", out var methodElement))
            {
                if (string.Equals(methodElement.GetString(), "initialize", StringComparison.Ordinal))
                {
                    var id = requestJson.RootElement.GetProperty("id").GetInt32();
                    var initializeResponse = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = new
                        {
                            protocolVersion = "2025-11-25",
                            capabilities = new { },
                            serverInfo = new
                            {
                                name = "test",
                                version = "1.0.0",
                            },
                        },
                    });

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(initializeResponse, Encoding.UTF8, "application/json"),
                    };
                }

                if (string.Equals(methodElement.GetString(), "notifications/initialized", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BlockingInitializationHandler : HttpMessageHandler
    {
        public TaskCompletionSource InitializeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool InitializeCanceled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var requestJson = JsonDocument.Parse(requestBody);
            if (requestJson.RootElement.TryGetProperty("method", out var methodElement) &&
                string.Equals(methodElement.GetString(), "initialize", StringComparison.Ordinal))
            {
                InitializeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    InitializeCanceled = true;
                    throw;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class TrackingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
