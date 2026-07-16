// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Mcp.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="McpClient"/> instances.
/// </summary>
public static class AspireMcpClientExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Mcp:Client";

    /// <summary>
    /// Registers an <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="connectionName">The service-discovery name of the MCP server.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{connectionName}/mcp</c> by default. When service discovery only provides
    /// HTTP endpoints, the client uses <c>http://{connectionName}/mcp</c> instead. Use <c>WithReference</c> in the
    /// AppHost to provide the server endpoint to this application.
    /// </remarks>
    /// <example>
    /// This example registers an unkeyed MCP client for an MCP server named <c>mcp-server</c>:
    /// <code>
    /// builder.AddMcpClient("mcp-server");
    /// </code>
    /// </example>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="connectionName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionName"/> is empty.</exception>
    /// <exception cref="FormatException">Thrown when the configured connection string is not an absolute HTTP or HTTPS URI.</exception>
    public static IHostApplicationBuilder AddMcpClient(this IHostApplicationBuilder builder, string connectionName)
        => AddMcpClientCore(builder, connectionName, serviceKey: null, configureClientOptions: null, configureTransportOptions: null, configureSettings: null);

    /// <summary>
    /// Registers an <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="connectionName">The service-discovery name of the MCP server.</param>
    /// <param name="configureSettings">An optional delegate to configure <see cref="McpClientSettings"/> after configuration binding.</param>
    /// <param name="configureClientOptions">An optional delegate to configure <see cref="McpClientOptions"/> before the client is created.</param>
    /// <param name="configureTransportOptions">An optional delegate to configure <see cref="HttpClientTransportOptions"/> before the transport is created.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{connectionName}/mcp</c> by default. When service discovery only provides
    /// HTTP endpoints, the client uses <c>http://{connectionName}/mcp</c> instead. Use <c>WithReference</c> in the
    /// AppHost to provide the server endpoint to this application.
    /// </remarks>
    /// <example>
    /// This example configures an unkeyed MCP client registration:
    /// <code>
    /// builder.AddMcpClient(
    ///     "mcp-server",
    ///     settings =>
    ///     {
    ///         // Configure Aspire settings.
    ///     },
    ///     clientOptions =>
    ///     {
    ///         // Configure client options.
    ///     },
    ///     transportOptions =>
    ///     {
    ///         // Configure transport options.
    ///     });
    /// </code>
    /// </example>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="connectionName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionName"/> is empty.</exception>
    /// <exception cref="FormatException">Thrown when the configured connection string is not an absolute HTTP or HTTPS URI and <paramref name="configureSettings"/> does not provide an endpoint.</exception>
    public static IHostApplicationBuilder AddMcpClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<McpClientSettings>? configureSettings = null,
        Action<McpClientOptions>? configureClientOptions = null,
        Action<HttpClientTransportOptions>? configureTransportOptions = null)
        => AddMcpClientCore(builder, connectionName, serviceKey: null, configureClientOptions, configureTransportOptions, configureSettings);

    /// <summary>
    /// Registers a keyed <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="name">The service-discovery name of the MCP server and the service key of the client.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{name}/mcp</c> by default. When service discovery only provides HTTP
    /// endpoints, the client uses <c>http://{name}/mcp</c> instead. Use <c>WithReference</c> in the AppHost to provide
    /// the server endpoint to this application.
    /// </remarks>
    /// <example>
    /// This example registers a keyed MCP client where the key and service-discovery name are both <c>mcp-server</c>:
    /// <code>
    /// builder.AddKeyedMcpClient("mcp-server");
    /// </code>
    /// </example>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty.</exception>
    /// <exception cref="FormatException">Thrown when the configured connection string is not an absolute HTTP or HTTPS URI.</exception>
    public static IHostApplicationBuilder AddKeyedMcpClient(this IHostApplicationBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddMcpClientCore(builder, name, name, configureClientOptions: null, configureTransportOptions: null, configureSettings: null);
    }

    /// <summary>
    /// Registers a keyed <see cref="McpClient"/> that connects to the specified MCP server through service discovery.
    /// </summary>
    /// <param name="builder">The application builder to add services to.</param>
    /// <param name="name">The service-discovery name of the MCP server and the service key of the client.</param>
    /// <param name="configureSettings">An optional delegate to configure <see cref="McpClientSettings"/> after configuration binding.</param>
    /// <param name="configureClientOptions">An optional delegate to configure <see cref="McpClientOptions"/> before the client is created.</param>
    /// <param name="configureTransportOptions">An optional delegate to configure <see cref="HttpClientTransportOptions"/> before the transport is created.</param>
    /// <remarks>
    /// The server is resolved at <c>https://{name}/mcp</c> by default. When service discovery only provides HTTP
    /// endpoints, the client uses <c>http://{name}/mcp</c> instead. Use <c>WithReference</c> in the AppHost to provide
    /// the server endpoint to this application.
    /// </remarks>
    /// <example>
    /// This example configures a keyed MCP client registration:
    /// <code>
    /// builder.AddKeyedMcpClient(
    ///     "mcp-server",
    ///     settings =>
    ///     {
    ///         // Configure Aspire settings.
    ///     },
    ///     clientOptions =>
    ///     {
    ///         // Configure client options.
    ///     },
    ///     transportOptions =>
    ///     {
    ///         // Configure transport options.
    ///     });
    /// </code>
    /// </example>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty.</exception>
    /// <exception cref="FormatException">Thrown when the configured connection string is not an absolute HTTP or HTTPS URI and <paramref name="configureSettings"/> does not provide an endpoint.</exception>
    public static IHostApplicationBuilder AddKeyedMcpClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<McpClientSettings>? configureSettings = null,
        Action<McpClientOptions>? configureClientOptions = null,
        Action<HttpClientTransportOptions>? configureTransportOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddMcpClientCore(builder, name, name, configureClientOptions, configureTransportOptions, configureSettings);
    }

    private static IHostApplicationBuilder AddMcpClientCore(
        IHostApplicationBuilder builder,
        string connectionName,
        object? serviceKey,
        Action<McpClientOptions>? configureClientOptions,
        Action<HttpClientTransportOptions>? configureTransportOptions,
        Action<McpClientSettings>? configureSettings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new McpClientSettings();
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        FormatException? connectionStringException = null;
        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            try
            {
                settings.ParseConnectionString(connectionString);
            }
            catch (FormatException ex)
            {
                // A malformed connection string must not be masked by an endpoint inherited from configuration.
                settings.Endpoint = null;
                connectionStringException = ex;
            }
        }

        configureSettings?.Invoke(settings);

        if (connectionStringException is not null && settings.Endpoint is null)
        {
            throw connectionStringException;
        }

        var endpoint = settings.Endpoint ?? CreateServiceDiscoveryEndpoint(builder.Configuration, connectionName);
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

        if (!settings.DisableHealthChecks)
        {
            var healthCheckName = serviceKey is null ? "Mcp.Client" : $"Mcp.Client_{connectionName}";

            builder.TryAddHealthCheck(new HealthCheckRegistration(
                healthCheckName,
                sp => new McpClientHealthCheck(sp, serviceKey),
                failureStatus: default,
                tags: default,
                timeout: default));
        }

        return builder;
    }

    private static Uri CreateServiceDiscoveryEndpoint(IConfiguration configuration, string connectionName)
    {
        if (connectionName.IndexOfAny(['/', '\\', '?', '#', '@', ':']) >= 0)
        {
            throw new ArgumentException($"'{connectionName}' is not a valid MCP service-discovery connection name.", nameof(connectionName));
        }

        var servicesSection = configuration.GetSection("services").GetSection(connectionName);
        var hasHttpsEndpoint = servicesSection.GetSection("https").GetChildren().Any();
        var hasHttpEndpoint = servicesSection.GetSection("http").GetChildren().Any();
        var scheme = !hasHttpsEndpoint && hasHttpEndpoint ? "http" : "https";
        return new Uri($"{scheme}://{connectionName}/mcp", UriKind.Absolute);
    }

    private sealed class McpClientRegistration : IDisposable, IAsyncDisposable
    {
        private readonly Func<Task<DisposableMcpClient>> _createClient;
        private readonly CancellationTokenSource _creationCancellation = new();
        private ReconnectableMcpClient? _client;
        private readonly object _lock = new();
        private int _disposed;

        public McpClientRegistration(
            Uri endpoint,
            Action<McpClientOptions>? configureClientOptions,
            Action<HttpClientTransportOptions>? configureTransportOptions,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            _createClient = () => CreateClientAsync(endpoint, configureClientOptions, configureTransportOptions, httpClientFactory, loggerFactory);
        }

        public McpClient GetClient()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) is not 0, this);
            var client = Volatile.Read(ref _client);
            if (client is null)
            {
                lock (_lock)
                {
                    client ??= _client ??= new ReconnectableMcpClient(_createClient, _creationCancellation.Cancel);
                }
            }
            client.Initialize();
            return client;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) is 0)
            {
                _creationCancellation.Cancel();
                Volatile.Read(ref _client)?.Dispose();
                _creationCancellation.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) is 0)
                {
                    _creationCancellation.Cancel();
                    var client = Volatile.Read(ref _client);
                    if (client is not null)
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                    _creationCancellation.Dispose();
                }
            }

        private async Task<DisposableMcpClient> CreateClientAsync(
            Uri endpoint,
            Action<McpClientOptions>? configureClientOptions,
            Action<HttpClientTransportOptions>? configureTransportOptions,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            HttpClient? httpClient = null;
            HttpClientTransport? transport = null;
            try
            {
                var transportOptions = new HttpClientTransportOptions { Endpoint = endpoint };
                configureTransportOptions?.Invoke(transportOptions);
                var clientOptions = new McpClientOptions();
                configureClientOptions?.Invoke(clientOptions);
                httpClient = httpClientFactory.CreateClient(string.Empty);
                transport = new HttpClientTransport(transportOptions, httpClient, loggerFactory, ownsHttpClient: true);
                var client = await McpClient.CreateAsync(transport, clientOptions, loggerFactory: loggerFactory, cancellationToken: _creationCancellation.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) is not 0)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw new ObjectDisposedException(nameof(McpClientRegistration));
                }
                return new DisposableMcpClient(client, transport);
            }
            catch
            {
                if (transport is not null)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    httpClient?.Dispose();
                }
                throw;
            }
        }
    }

    // Keep the injected McpClient stable while replacing its private session after a terminal failure.
#pragma warning disable MCPEXP002
    private sealed class ReconnectableMcpClient(
        Func<Task<DisposableMcpClient>> createClient,
        Action cancelClientCreation) : McpClient, IDisposable
#pragma warning restore MCPEXP002
    {
        private readonly object _lock = new();
        private readonly HashSet<NotificationRegistration> _notificationRegistrations = [];
        private readonly HashSet<Task> _cleanupTasks = [];
        private Task<DisposableMcpClient>? _client;
        private int _disposed;

        public override ServerCapabilities ServerCapabilities => GetClient().ServerCapabilities;
        public override Implementation ServerInfo => GetClient().ServerInfo;
        public override string ServerInstructions => GetClient().ServerInstructions!;
        public override Task<ClientCompletionDetails> Completion => GetClient().Completion;
        public override string SessionId => GetClient().SessionId!;
        public override string NegotiatedProtocolVersion => GetClient().NegotiatedProtocolVersion!;

        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
            => ExecuteAsync((client, token) => client.SendRequestAsync(request, token), cancellationToken);

        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
            => ExecuteAsync((client, token) => client.SendMessageAsync(message, token), cancellationToken);

        public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
        {
            var registration = new NotificationRegistration(this, method, handler);
            while (true)
            {
                var task = GetClientTask();
                var client = task.GetAwaiter().GetResult();
                lock (_lock)
                {
                    if (ReferenceEquals(_client, task))
                    {
                        _notificationRegistrations.Add(registration);
                        registration.Register(task, client);
                        return registration;
                    }
                }
            }
        }

        public void Initialize() => _ = GetClient();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) is 0)
            {
                cancelClientCreation();

                Task<DisposableMcpClient>? client;
                lock (_lock)
                {
                    client = _client;
                    _client = null;
                }

                if (client is not null)
                {
                    DisposeClientAsync(client).GetAwaiter().GetResult();
                }

                WaitForCleanupAsync().GetAwaiter().GetResult();
            }
        }

        public override ValueTask DisposeAsync() => new(DisposeAsyncCore());

        private async Task DisposeAsyncCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) is 0)
            {
                cancelClientCreation();

                Task<DisposableMcpClient>? client;
                lock (_lock)
                {
                    client = _client;
                    _client = null;
                }

                if (client is not null)
                {
                    await DisposeClientAsync(client).ConfigureAwait(false);
                }

                await WaitForCleanupAsync().ConfigureAwait(false);
            }
        }

        private McpClient GetClient() => GetClientTask().GetAwaiter().GetResult();

        private Task<DisposableMcpClient> GetClientTask()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) is not 0, this);
            var clientTask = Volatile.Read(ref _client);
            if (clientTask is null)
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) is not 0, this);
                    clientTask ??= _client ??= createClient();
                    if (ReferenceEquals(_client, clientTask))
                    {
                        _ = ObserveCompletionAsync(clientTask);
                    }
                }
            }

            return clientTask;
        }

        private async Task<T> ExecuteAsync<T>(Func<McpClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            var task = GetClientTask();
            try
            {
                var client = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                RegisterNotificationHandlers(task, client);
                return await operation(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTerminalFailure(task, ex))
            {
                Invalidate(task);
                throw;
            }
        }

        private async Task ExecuteAsync(Func<McpClient, CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            var task = GetClientTask();
            try
            {
                var client = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                RegisterNotificationHandlers(task, client);
                await operation(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTerminalFailure(task, ex))
            {
                Invalidate(task);
                throw;
            }
        }

        private async Task ObserveCompletionAsync(Task<DisposableMcpClient> task)
        {
            try
            {
                await (await task.ConfigureAwait(false)).Completion.ConfigureAwait(false);
            }
            catch
            {
            }

            Invalidate(task);
        }

        private void RegisterNotificationHandlers(Task<DisposableMcpClient> task, DisposableMcpClient client)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_client, task))
                {
                    foreach (var registration in _notificationRegistrations)
                    {
                        registration.Register(task, client);
                    }
                }
            }
        }

        private static bool IsTerminalFailure(Task<DisposableMcpClient> task, Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return false;
            }

            if (exception is HttpRequestException or IOException or System.Net.Sockets.SocketException)
            {
                return true;
            }

            if (!task.IsCompletedSuccessfully)
            {
                return false;
            }

            var completion = task.Result.Completion;
            return completion.IsCompletedSuccessfully && completion.Result.Exception is not null;
        }

        private void Invalidate(Task<DisposableMcpClient> task)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_client, task))
                {
                    return;
                }

                _client = null;
            }

            if (task.IsCompletedSuccessfully)
            {
                TrackCleanup(DisposeClientAsync(task));
            }
        }

        private void TrackCleanup(Task cleanupTask)
        {
            lock (_lock)
            {
                _cleanupTasks.Add(cleanupTask);
            }

            _ = cleanupTask.ContinueWith(
                _ => RemoveCleanup(cleanupTask),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void RemoveCleanup(Task cleanupTask)
        {
            lock (_lock)
            {
                _cleanupTasks.Remove(cleanupTask);
            }
        }

        private async Task WaitForCleanupAsync()
        {
            while (true)
            {
                Task[] tasks;
                lock (_lock)
                {
                    tasks = [.. _cleanupTasks];
                }

                if (tasks.Length is 0)
                {
                    return;
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        private static async Task DisposeClientAsync(Task<DisposableMcpClient> task)
        {
            try
            {
                await (await task.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private sealed class NotificationRegistration(
            ReconnectableMcpClient owner,
            string method,
            Func<JsonRpcNotification, CancellationToken, ValueTask> handler) : IAsyncDisposable
        {
            private Task<DisposableMcpClient>? _client;
            private IAsyncDisposable? _innerRegistration;
            private int _disposed;

            public void Register(Task<DisposableMcpClient> clientTask, DisposableMcpClient client)
            {
                if (Volatile.Read(ref _disposed) is not 0 || ReferenceEquals(_client, clientTask))
                {
                    return;
                }

                _client = clientTask;
                _innerRegistration = client.RegisterNotificationHandler(method, handler);
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) is 0)
                {
                    IAsyncDisposable? registration;
                    lock (owner._lock)
                    {
                        owner._notificationRegistrations.Remove(this);
                        registration = _innerRegistration;
                        _innerRegistration = null;
                    }

                    if (registration is not null)
                    {
                        await registration.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private sealed class McpClientHealthCheck(IServiceProvider serviceProvider, object? serviceKey) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var client = serviceKey is null
                    ? serviceProvider.GetRequiredService<McpClient>()
                    : serviceProvider.GetRequiredKeyedService<McpClient>(serviceKey);

                await client.PingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("MCP client health check failed.", ex);
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
