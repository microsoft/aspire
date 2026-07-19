// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.ServiceDiscovery;
using ModelContextProtocol.Client;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Json.Schema;
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
        builder.Configuration["services:mcp:https:0"] = "https://mcp";
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        var clientOptionsConfigured = false;
        var transportOptionsConfigured = false;

        builder.AddMcpClient(
            "mcp",
            null,
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
            null,
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
    public void AddMcpClientInvokesSettingsDelegate()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddMcpClient("mcp", configureSettings: settings => settings.DisableHealthChecks = true);

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Empty(options.Registrations);
    }

    [Fact]
    public void AddMcpClientRejectsNonAbsoluteConnectionString()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:mcp"] = "not-a-uri";

        var exception = Assert.Throws<FormatException>(() => builder.AddMcpClient("mcp"));

        Assert.Equal("The MCP client connection string must be an absolute HTTP or HTTPS URI.", exception.Message);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("ftp://mcp")]
    [InlineData("http:/mcp")]
    [InlineData("https:///mcp")]
    public void AddMcpClientRejectsMalformedConnectionString(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:mcp"] = connectionString;

        Assert.Throws<FormatException>(() => builder.AddMcpClient("mcp"));
    }

    [Theory]
    [InlineData("ftp://mcp/mcp")]
    [InlineData("/mcp")]
    public void AddMcpClientRejectsInvalidConfiguredEndpoint(string endpoint)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["Aspire:Mcp:Client:Endpoint"] = endpoint;

        Assert.Throws<FormatException>(() => builder.AddMcpClient("mcp"));
    }

    [Fact]
    public void ConfigureSettingsCanOverrideMalformedConnectionString()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:mcp"] = "not-a-uri";
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp", configureSettings: settings => settings.Endpoint = new Uri("https://override/mcp"));

        using var host = builder.Build();
        _ = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://override/mcp");
    }

    [Fact]
    public void MalformedConnectionStringDoesNotUseInheritedEndpoint()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aspire:Mcp:Client:Endpoint"] = "https://inherited/mcp",
            ["ConnectionStrings:mcp"] = "not-a-uri"
        });

        var exception = Assert.Throws<FormatException>(() => builder.AddMcpClient("mcp"));

        Assert.Equal("The MCP client connection string must be an absolute HTTP or HTTPS URI.", exception.Message);
    }

    [Fact]
    public void ConfigurationUsesBaseThenNamedThenConnectionStringThenSettings()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aspire:Mcp:Client:Endpoint"] = "https://base/mcp",
            ["Aspire:Mcp:Client:mcp:Endpoint"] = "https://named/mcp",
            ["ConnectionStrings:mcp"] = "https://connection/mcp"
        });
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp", configureSettings: settings => settings.Endpoint = new Uri("https://settings/mcp"));

        using var host = builder.Build();
        _ = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://settings/mcp");
    }

    [Fact]
    public void ConfigurationSchemaValidatesExpectedConfiguration()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json"), new BuildOptions
        {
            Dialect = Dialect.Draft07,
            SchemaRegistry = new SchemaRegistry()
        });
        using var config = JsonDocument.Parse("""{"Aspire":{"Mcp":{"Client":{"Endpoint":"https://mcp/mcp","DisableHealthChecks":false}}}}""");

        Assert.True(schema.Evaluate(config.RootElement).IsValid);
    }

    [Fact]
    public void ConfigurationSchemaIncludesMcpLoggingCategories()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json")));
        var logLevelProperties = schema.RootElement
            .GetProperty("definitions")
            .GetProperty("logLevel")
            .GetProperty("properties");

        Assert.True(logLevelProperties.TryGetProperty("ModelContextProtocol.Authentication.ClientOAuthProvider", out _));
        Assert.True(logLevelProperties.TryGetProperty("ModelContextProtocol.Client.AutoDetectingClientSessionTransport", out _));
        Assert.True(logLevelProperties.TryGetProperty("ModelContextProtocol.Client.HttpClientTransport", out _));
        Assert.True(logLevelProperties.TryGetProperty("ModelContextProtocol.Client.McpClient", out _));
    }

    [Fact]
    public void ConfigurationSchemaRejectsInvalidConfiguration()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json"), new BuildOptions
        {
            Dialect = Dialect.Draft07,
            SchemaRegistry = new SchemaRegistry()
        });
        using var config = JsonDocument.Parse("""{"Aspire":{"Mcp":{"Client":{"DisableHealthChecks":"false"}}}}""");

        Assert.False(schema.Evaluate(config.RootElement).IsValid);
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
    public void AddMcpClientSupportsConfiguringOnlyTransportOptions()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        var transportOptionsConfigured = false;

        builder.AddMcpClient(
            "mcp",
            configureTransportOptions: options =>
            {
                transportOptionsConfigured = true;
                options.Endpoint = new Uri("https://transport-only/mcp", UriKind.Absolute);
            });

        using var host = builder.Build();
        _ = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.True(transportOptionsConfigured);
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://transport-only/mcp");
    }

    [Fact]
    public void AddMcpClientDefaultsTransportNameToConnectionName()
    {
        var handler = new RequestRecordingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:mcp"] = "https://user:password@example.com/mcp?token=secret";
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        string? transportName = null;

        builder.AddMcpClient("mcp", configureTransportOptions: options => transportName = options.Name);

        using var host = builder.Build();
        _ = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Equal("mcp", transportName);
    }

    [Theory]
    [InlineData("ftp://mcp/mcp")]
    public void AddMcpClientRejectsInvalidTransportEndpoint(string endpoint)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMcpClient(
            "mcp",
            configureTransportOptions: options => options.Endpoint = new Uri(endpoint, UriKind.RelativeOrAbsolute));

        using var host = builder.Build();

        void ResolveClient() => _ = host.Services.GetRequiredService<McpClient>();

        Assert.Throws<ArgumentException>(ResolveClient);
    }

    [Fact]
    public void AddKeyedMcpClientSupportsConfiguringOnlyClientOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var clientOptionsConfigured = false;

        builder.AddKeyedMcpClient(
            "mcp",
            configureClientOptions: _ =>
            {
                clientOptionsConfigured = true;
            });

        using var host = builder.Build();
        _ = Record.Exception(() => _ = host.Services.GetRequiredKeyedService<McpClient>("mcp"));

        Assert.True(clientOptionsConfigured);
    }

    [Fact]
    public void McpClientResolvesHttpOnlyServiceDiscoveryEndpoint()
    {
        var handler = new SuccessfulInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["services:mcp:http:0"] = "http://resolved-mcp",
        });
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddServiceDiscovery();
            http.ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var exception = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Null(exception);
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "http://resolved-mcp/mcp");
    }

    public static TheoryData<EndPoint, string> PlatformServiceDiscoveryEndpoints { get; } = new()
    {
        { new DnsEndPoint("platform-mcp", 5001), "https://platform-mcp:5001/mcp" },
        { new IPEndPoint(IPAddress.Loopback, 5001), "https://127.0.0.1:5001/mcp" },
    };

    [Theory]
    [MemberData(nameof(PlatformServiceDiscoveryEndpoints))]
    public void McpClientResolvesPlatformServiceDiscoveryEndpoint(EndPoint endPoint, string expectedEndpoint)
    {
        var handler = new SuccessfulInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddSingleton<IServiceEndpointProviderFactory>(new StaticServiceEndpointProviderFactory(endPoint));
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var exception = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Null(exception);
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == expectedEndpoint);
    }

    [Fact]
    public async Task McpClientSelectsNextServiceDiscoveryEndpointAfterReconnect()
    {
        var handler = new FailPingOnceHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["services:mcp:https:0"] = "https://replica-one",
            ["services:mcp:https:1"] = "https://replica-two",
        });
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var healthChecks = host.Services.GetRequiredService<HealthCheckService>();
        var firstResult = await healthChecks.CheckHealthAsync();
        var secondResult = await healthChecks.CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, firstResult.Status);
        Assert.Equal(HealthStatus.Healthy, secondResult.Status);
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://replica-one/mcp");
        Assert.Contains(handler.RequestUris, uri => uri.ToString() == "https://replica-two/mcp");
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
    public async Task AddMcpClientHealthCheckPingsServer()
    {
        var handler = new SuccessfulInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var result = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains(handler.RequestMethods, method => method == "ping");
    }

    [Fact]
    public async Task AddMcpClientHealthCheckPassesCancellationTokenToPing()
    {
        var handler = new BlockingPingHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        using var cancellationTokenSource = new CancellationTokenSource();
        var checkTask = host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationTokenSource.Token);
        await handler.PingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellationTokenSource.Cancel();
        var result = await checkTask;

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.True(handler.PingCanceled);
    }

    [Fact]
    public async Task AddMcpClientHealthCheckCancelsInitializationWait()
    {
        var handler = new BlockingInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        using var cancellationTokenSource = new CancellationTokenSource();
        var checkTask = host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationTokenSource.Token);
        await handler.InitializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellationTokenSource.Cancel();
        var result = await checkTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.False(handler.InitializeCanceled);
    }

    [Fact]
    public void AddMcpClientDisposesHttpClientTransportOnHostDisposal()
    {
        var handler = new SuccessfulInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMcpClient("mcp");
        var factory = new TrackingHttpClientFactory(handler);
        builder.Services.AddSingleton<IHttpClientFactory>(factory);

        var host = builder.Build();
        var resolveException = Record.Exception(() => _ = host.Services.GetRequiredService<McpClient>());

        Assert.Null(resolveException);
        Assert.Null(Record.Exception(host.Dispose));
        Assert.True(handler.Disposed);
        Assert.Equal(string.Empty, factory.Name);
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

    [Fact]
    public async Task AddMcpClientRetriesInitializationAfterTransientFailure()
    {
        var handler = new FailThenSucceedInitializationHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var healthChecks = host.Services.GetRequiredService<HealthCheckService>();
        var firstResult = await healthChecks.CheckHealthAsync();
        var secondResult = await healthChecks.CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, firstResult.Status);
        Assert.Equal(HealthStatus.Healthy, secondResult.Status);
        Assert.Equal(2, handler.InitializeAttempts);
    }

    [Fact]
    public async Task AddMcpClientReconnectsAfterOperationFailureWithoutReplaying()
    {
        var handler = new FailPingOnceHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var healthChecks = host.Services.GetRequiredService<HealthCheckService>();
        var firstResult = await healthChecks.CheckHealthAsync();
        var secondResult = await healthChecks.CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, firstResult.Status);
        Assert.Equal(HealthStatus.Healthy, secondResult.Status);
        Assert.Equal(2, handler.InitializeAttempts);
    }

    [Fact]
    public async Task AddMcpClientPreservesNotificationHandlersAcrossReconnect()
    {
        var handler = new NotificationRecoveryHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<McpClient>();
        var notificationReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        await using var registration = client.RegisterNotificationHandler(
            "test/notification",
            (_, _) =>
            {
                if (Interlocked.Increment(ref notificationCount) > 1)
                {
                    unexpectedNotification.TrySetResult();
                }

                notificationReceived.TrySetResult();
                return ValueTask.CompletedTask;
            });

        await handler.InitialStream.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var failure = await Record.ExceptionAsync(async () => await client.PingAsync());
        Assert.NotNull(failure);

        var serverInfo = client.ServerInfo;
        var replacementStream = await handler.ReplacementStream.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("test", serverInfo.Name);

        await NotificationRecoveryHandler.SendNotificationAsync(replacementStream);
        await notificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref notificationCount));

        await registration.DisposeAsync();
        await NotificationRecoveryHandler.SendNotificationAsync(replacementStream);
        var unexpectedResult = await Task.WhenAny(unexpectedNotification.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(unexpectedNotification.Task, unexpectedResult);
        Assert.Equal(1, Volatile.Read(ref notificationCount));
    }

    [Fact]
    public async Task AddMcpClientDoesNotRetainInvalidNotificationHandlers()
    {
        var handler = new NotificationRecoveryHandler();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        builder.AddMcpClient("mcp");

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<McpClient>();

        Assert.Throws<ArgumentException>(() => client.RegisterNotificationHandler(" ", (_, _) => ValueTask.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => client.RegisterNotificationHandler("test/notification", null!));

        await handler.InitialStream.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var failure = await Record.ExceptionAsync(async () => await client.PingAsync());
        Assert.NotNull(failure);

        var exception = Record.Exception(() => _ = client.ServerInfo);

        Assert.Null(exception);
        await handler.ReplacementStream.Task.WaitAsync(TimeSpan.FromSeconds(10));
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

    [Fact]
    public void AddMcpClientRejectsConnectionNameWithReservedUriCharacters()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddMcpClient("mcp/service"));

        Assert.Equal("connectionName", exception.ParamName);
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
        public ConcurrentQueue<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private class SuccessfulInitializationHandler : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> RequestUris { get; } = [];

        public ConcurrentQueue<string> RequestMethods { get; } = [];

        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Enqueue(request.RequestUri!);

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
                RequestMethods.Enqueue(methodElement.GetString()!);
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

                if (string.Equals(methodElement.GetString(), "ping", StringComparison.Ordinal))
                {
                    var id = requestJson.RootElement.GetProperty("id").GetInt32();
                    var pingResponse = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { } });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(pingResponse, Encoding.UTF8, "application/json"),
                    };
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

    private sealed class StaticServiceEndpointProviderFactory(params EndPoint[] endPoints) : IServiceEndpointProviderFactory
    {
        public bool TryCreateProvider(ServiceEndpointQuery query, [NotNullWhen(true)] out IServiceEndpointProvider? provider)
        {
            provider = new StaticServiceEndpointProvider(endPoints);

            return true;
        }
    }

    private sealed class StaticServiceEndpointProvider(EndPoint[] endPoints) : IServiceEndpointProvider
    {
        public ValueTask PopulateAsync(IServiceEndpointBuilder endpoints, CancellationToken cancellationToken)
        {
            foreach (var endPoint in endPoints)
            {
                endpoints.Endpoints.Add(ServiceEndpoint.Create(endPoint, new FeatureCollection()));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingPingHandler : SuccessfulInitializationHandler
    {
        public TaskCompletionSource PingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PingCanceled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var requestBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                using var requestJson = JsonDocument.Parse(requestBody);
                if (requestJson.RootElement.TryGetProperty("method", out var methodElement) &&
                    string.Equals(methodElement.GetString(), "ping", StringComparison.Ordinal))
                {
                    PingStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        PingCanceled = true;
                        throw;
                    }
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private sealed class FailPingOnceHandler : SuccessfulInitializationHandler
    {
        private int _pingFailures;
        public int InitializeAttempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("method", out var method))
                {
                    if (string.Equals(method.GetString(), "initialize", StringComparison.Ordinal))
                    {
                        InitializeAttempts++;
                    }
                    else if (string.Equals(method.GetString(), "ping", StringComparison.Ordinal) &&
                        Interlocked.Exchange(ref _pingFailures, 1) is 0)
                    {
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class NotificationRecoveryHandler : SuccessfulInitializationHandler
    {
        private int _pingFailures;
        private int _streamCount;

        public TaskCompletionSource<PipeWriter> InitialStream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PipeWriter> ReplacementStream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var pipe = new Pipe();
                var streamNumber = Interlocked.Increment(ref _streamCount);
                if (streamNumber is 1)
                {
                    InitialStream.TrySetResult(pipe.Writer);
                }
                else
                {
                    ReplacementStream.TrySetResult(pipe.Writer);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(pipe.Reader.AsStream()),
                };
                response.Content.Headers.ContentType = new("text/event-stream");
                return response;
            }

            if (request.Method == HttpMethod.Post)
            {
                var requestBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                using var requestJson = JsonDocument.Parse(requestBody);
                if (requestJson.RootElement.TryGetProperty("method", out var methodElement) &&
                    string.Equals(methodElement.GetString(), "ping", StringComparison.Ordinal) &&
                    Interlocked.Exchange(ref _pingFailures, 1) is 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public static async Task SendNotificationAsync(PipeWriter writer)
        {
            const string message = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"test/notification\",\"params\":{}}\n\n";
            await writer.WriteAsync(Encoding.UTF8.GetBytes(message));
        }
    }

    private sealed class FailThenSucceedInitializationHandler : HttpMessageHandler
    {
        private int _initializeAttempts;

        public int InitializeAttempts => Volatile.Read(ref _initializeAttempts);

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
                    if (Interlocked.Increment(ref _initializeAttempts) == 1)
                    {
                        await Task.Yield();
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }

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

                if (string.Equals(methodElement.GetString(), "ping", StringComparison.Ordinal))
                {
                    var id = requestJson.RootElement.GetProperty("id").GetInt32();
                    var pingResponse = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { } });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(pingResponse, Encoding.UTF8, "application/json"),
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class TrackingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? Name { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Name = name;
            return new(handler);
        }
    }
}
