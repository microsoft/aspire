// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

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
        => AddMcpClientCore(builder, connectionName, serviceKey: null);

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

        return AddMcpClientCore(builder, name, name);
    }

    private static IHostApplicationBuilder AddMcpClientCore(IHostApplicationBuilder builder, string connectionName, object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var endpoint = new Uri($"https://{connectionName}/mcp", UriKind.Absolute);
        var registrationKey = new object();
        builder.Services.AddHttpClient();
        builder.Services.AddKeyedSingleton<McpClientRegistration>(registrationKey, (serviceProvider, _) => new McpClientRegistration(
            endpoint,
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

    private sealed class McpClientRegistration : IAsyncDisposable, IDisposable
    {
        private readonly Lazy<Task<McpClient>> _client;

        public McpClientRegistration(Uri endpoint, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        {
            _client = new(() => CreateClientAsync(endpoint, httpClientFactory, loggerFactory));
        }

        public McpClient GetClient()
        {
            return _client.Value.GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            if (_client.IsValueCreated && _client.Value.IsCompletedSuccessfully)
            {
                await _client.Value.Result.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_client.IsValueCreated && _client.Value.IsCompletedSuccessfully)
            {
                _client.Value.Result.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        private static async Task<McpClient> CreateClientAsync(
            Uri endpoint,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            var httpClient = httpClientFactory.CreateClient();
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = endpoint },
                httpClient,
                loggerFactory);

            return await McpClient.CreateAsync(transport).ConfigureAwait(false);
        }
    }
}
