// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="McpClient"/> instances.
/// </summary>
public static class AspireMcpClientExtensions
{
    /// <summary>
    /// Registers an <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="connectionName">The service-discovery name of the MCP server.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{connectionName}/mcp</c>. Use <c>WithReference</c> in the AppHost to
    /// provide the server endpoint to this application.
    /// </remarks>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="connectionName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionName"/> is empty.</exception>
    public static IHostApplicationBuilder AddMcpClient(this IHostApplicationBuilder builder, string connectionName)
        => AddMcpClientCore(builder, connectionName, serviceKey: null, configureClientOptions: null, configureTransportOptions: null);

    /// <summary>
    /// Registers an <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="connectionName">The service-discovery name of the MCP server.</param>
    /// <param name="configureClientOptions">An optional delegate to configure <see cref="McpClientOptions"/> before the client is created.</param>
    /// <param name="configureTransportOptions">An optional delegate to configure <see cref="HttpClientTransportOptions"/> before the transport is created.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{connectionName}/mcp</c>. Use <c>WithReference</c> in the AppHost to
    /// provide the server endpoint to this application.
    /// </remarks>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="connectionName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionName"/> is empty.</exception>
    public static IHostApplicationBuilder AddMcpClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<McpClientOptions>? configureClientOptions,
        Action<HttpClientTransportOptions>? configureTransportOptions)
        => AddMcpClientCore(builder, connectionName, serviceKey: null, configureClientOptions, configureTransportOptions);

    /// <summary>
    /// Registers a keyed <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="name">The service-discovery name of the MCP server and the service key of the client.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{name}/mcp</c>. Use <c>WithReference</c> in the AppHost to provide the
    /// server endpoint to this application.
    /// </remarks>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty.</exception>
    public static IHostApplicationBuilder AddKeyedMcpClient(this IHostApplicationBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddMcpClientCore(builder, name, name, configureClientOptions: null, configureTransportOptions: null);
    }

    /// <summary>
    /// Registers a keyed <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="name">The service-discovery name of the MCP server and the service key of the client.</param>
    /// <param name="configureClientOptions">An optional delegate to configure <see cref="McpClientOptions"/> before the client is created.</param>
    /// <param name="configureTransportOptions">An optional delegate to configure <see cref="HttpClientTransportOptions"/> before the transport is created.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{name}/mcp</c>. Use <c>WithReference</c> in the AppHost to provide the
    /// server endpoint to this application.
    /// </remarks>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty.</exception>
    public static IHostApplicationBuilder AddKeyedMcpClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<McpClientOptions>? configureClientOptions,
        Action<HttpClientTransportOptions>? configureTransportOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddMcpClientCore(builder, name, name, configureClientOptions, configureTransportOptions);
    }

    private static IHostApplicationBuilder AddMcpClientCore(
        IHostApplicationBuilder builder,
        string connectionName,
        object? serviceKey,
        Action<McpClientOptions>? configureClientOptions,
        Action<HttpClientTransportOptions>? configureTransportOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var endpoint = new Uri($"https://{connectionName}/mcp", UriKind.Absolute);
        var registrationKey = new object();
        builder.Services.AddHttpClient();
        builder.Services.AddKeyedSingleton<McpClientRegistration>(registrationKey, (serviceProvider, _) => new McpClientRegistration(
            endpoint,
            configureClientOptions,
            configureTransportOptions,
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            serviceProvider.GetRequiredService<ILoggerFactory>()));

        if (serviceKey is null)
        {
            builder.Services.AddSingleton(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<McpClientRegistration>(registrationKey).GetClient());
        }
        else
        {
            builder.Services.AddKeyedSingleton<McpClient>(serviceKey, (serviceProvider, _) =>
                serviceProvider.GetRequiredKeyedService<McpClientRegistration>(registrationKey).GetClient());
        }

        return builder;
    }

    private sealed class McpClientRegistration : IDisposable
    {
        private readonly Lazy<Task<DisposableMcpClient>> _client;
        private readonly CancellationTokenSource _creationCancellation = new();
        private int _disposed;

        public McpClientRegistration(
            Uri endpoint,
            Action<McpClientOptions>? configureClientOptions,
            Action<HttpClientTransportOptions>? configureTransportOptions,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            _client = new(() => CreateClientAsync(endpoint, configureClientOptions, configureTransportOptions, httpClientFactory, loggerFactory));
        }

        public McpClient GetClient()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) is not 0, this);
            return _client.Value.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) is not 0)
            {
                return;
            }

            _creationCancellation.Cancel();
            _creationCancellation.Dispose();
        }

        private async Task<DisposableMcpClient> CreateClientAsync(
            Uri endpoint,
            Action<McpClientOptions>? configureClientOptions,
            Action<HttpClientTransportOptions>? configureTransportOptions,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            var httpClient = httpClientFactory.CreateClient();
            var transportOptions = new HttpClientTransportOptions { Endpoint = endpoint };
            configureTransportOptions?.Invoke(transportOptions);
            var transport = new HttpClientTransport(
                transportOptions,
                httpClient,
                loggerFactory,
                ownsHttpClient: true);
            var clientOptions = new McpClientOptions();
            configureClientOptions?.Invoke(clientOptions);

            try
            {
                var client = await McpClient.CreateAsync(
                    transport,
                    clientOptions,
                    loggerFactory: loggerFactory,
                    cancellationToken: _creationCancellation.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) is not 0)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw new ObjectDisposedException(nameof(McpClientRegistration));
                }

                return new DisposableMcpClient(client, transport);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    // The DI container disposes singleton factory results directly. Wrapping the async-only client
    // gives the host a synchronous disposal path while preserving the client API surface.
#pragma warning disable MCPEXP002 // McpClient constructor is required to provide a wrapper that supports IDisposable.
    private sealed class DisposableMcpClient(McpClient innerClient, HttpClientTransport transport) : McpClient, IDisposable
#pragma warning restore MCPEXP002
    {
        private int _disposed;

        public override ServerCapabilities ServerCapabilities => innerClient.ServerCapabilities;

        public override Implementation ServerInfo => innerClient.ServerInfo;

        public override string ServerInstructions => innerClient.ServerInstructions!;

        public override Task<ClientCompletionDetails> Completion => innerClient.Completion;

        public override string SessionId => innerClient.SessionId!;

        public override string NegotiatedProtocolVersion => innerClient.NegotiatedProtocolVersion!;

        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
            => innerClient.SendRequestAsync(request, cancellationToken);

        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
            => innerClient.SendMessageAsync(message, cancellationToken);

        public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
            => innerClient.RegisterNotificationHandler(method, handler);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) is 0)
            {
                try
                {
                    innerClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) is 0)
            {
                return DisposeAsyncCore();
            }

            return ValueTask.CompletedTask;
        }

        private async ValueTask DisposeAsyncCore()
        {
            try
            {
                await innerClient.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
