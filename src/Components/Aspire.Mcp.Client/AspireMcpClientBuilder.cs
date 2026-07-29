// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace Aspire.Mcp.Client;

/// <summary>
/// Provides a builder for configuring MCP client registrations in Aspire.
/// </summary>
public sealed class AspireMcpClientBuilder
{
    private readonly string _httpClientName;
    private readonly Action<Action<McpClientOptions>> _addClientOptionsAction;
    private readonly Action<Action<HttpClientTransportOptions>> _addTransportOptionsAction;
    private readonly Action<ClientOAuthOptions> _setOAuthOptions;
    private readonly Action<Func<IServiceProvider, HttpRequestMessage, CancellationToken, ValueTask<string?>>> _setBearerTokenProvider;

    internal AspireMcpClientBuilder(
        IHostApplicationBuilder hostBuilder,
        string connectionName,
        object? serviceKey,
        McpClientSettings settings,
        string httpClientName,
        Action<Action<McpClientOptions>> addClientOptionsAction,
        Action<Action<HttpClientTransportOptions>> addTransportOptionsAction,
        Action<ClientOAuthOptions> setOAuthOptions,
        Action<Func<IServiceProvider, HttpRequestMessage, CancellationToken, ValueTask<string?>>> setBearerTokenProvider)
    {
        HostBuilder = hostBuilder ?? throw new ArgumentNullException(nameof(hostBuilder));
        ConnectionName = connectionName ?? throw new ArgumentNullException(nameof(connectionName));
        ServiceKey = serviceKey;
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClientName = httpClientName ?? throw new ArgumentNullException(nameof(httpClientName));
        _addClientOptionsAction = addClientOptionsAction ?? throw new ArgumentNullException(nameof(addClientOptionsAction));
        _addTransportOptionsAction = addTransportOptionsAction ?? throw new ArgumentNullException(nameof(addTransportOptionsAction));
        _setOAuthOptions = setOAuthOptions ?? throw new ArgumentNullException(nameof(setOAuthOptions));
        _setBearerTokenProvider = setBearerTokenProvider ?? throw new ArgumentNullException(nameof(setBearerTokenProvider));
    }

    /// <summary>
    /// Gets the <see cref="IHostApplicationBuilder"/> with which services are being registered.
    /// </summary>
    public IHostApplicationBuilder HostBuilder { get; }

    /// <summary>
    /// Gets the service-discovery connection name used by this MCP client registration.
    /// </summary>
    public string ConnectionName { get; }

    /// <summary>
    /// Gets the service key used to register the client, when using keyed registration.
    /// </summary>
    public object? ServiceKey { get; }

    /// <summary>
    /// Gets the Aspire settings used by the registration.
    /// </summary>
    public McpClientSettings Settings { get; }

    /// <summary>
    /// Configures <see cref="McpClientOptions"/> used for MCP client creation.
    /// </summary>
    public AspireMcpClientBuilder ConfigureClientOptions(Action<McpClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _addClientOptionsAction(configure);
        return this;
    }

    /// <summary>
    /// Configures <see cref="HttpClientTransportOptions"/> used by the MCP transport.
    /// </summary>
    public AspireMcpClientBuilder ConfigureTransportOptions(Action<HttpClientTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _addTransportOptionsAction(configure);
        return this;
    }

    /// <summary>
    /// Configures the underlying named <see cref="HttpClient"/> used by this MCP registration.
    /// </summary>
    public AspireMcpClientBuilder ConfigureHttpClient(Action<IHttpClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(HostBuilder.Services.AddHttpClient(_httpClientName));
        return this;
    }

    /// <summary>
    /// Enables MCP OAuth authentication for this registration.
    /// </summary>
    public AspireMcpClientBuilder UseOAuth(Action<ClientOAuthOptions> configureOAuth)
    {
        ArgumentNullException.ThrowIfNull(configureOAuth);
        var options = new ClientOAuthOptions
        {
            RedirectUri = new Uri("http://localhost"),
        };
        configureOAuth(options);
        _setOAuthOptions(options);
        return this;
    }

    /// <summary>
    /// Configures a bearer token provider invoked for each MCP transport request.
    /// </summary>
    public AspireMcpClientBuilder UseBearerTokenProvider(Func<IServiceProvider, HttpRequestMessage, CancellationToken, ValueTask<string?>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _setBearerTokenProvider(tokenProvider);
        return this;
    }
}
