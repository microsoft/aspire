// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Dashboard;

public class DashboardResourceTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(KnownConfigNames.ShowDashboardResources)]
    [InlineData(KnownConfigNames.Legacy.ShowDashboardResources)]
    public async Task DashboardIsAutomaticallyAddedAsHiddenResource(string showDashboardResourcesKey)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        // Ensure any ambient configuration doesn't impact this test.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [showDashboardResourcesKey] = null
        });

        var dashboardPath = Path.GetFullPath("dashboard");

        builder.Services.Configure<DcpOptions>(o =>
        {
            o.DashboardPath = dashboardPath;
        });

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources.OfType<ExecutableResource>());
        var initialSnapshot = Assert.Single(dashboard.Annotations.OfType<ResourceSnapshotAnnotation>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(dashboard).DefaultTimeout();

        Assert.NotNull(dashboard);
        Assert.Equal("aspire-dashboard", dashboard.Name);
        // dotnet exec --runtimeconfig <temp runtimeconfig.json> <dashboardPath>.dll
        Assert.Equal("dotnet", dashboard.Command);
        Assert.Equal(args[3], $"{dashboardPath}.dll");
        Assert.True(initialSnapshot.InitialSnapshot.IsHidden);
    }

    [Fact]
    public async Task DashboardIsAddedFirst()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.AddContainer("my-container", "my-image");

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Collection(model.Resources,
            r => Assert.Equal("aspire-dashboard", r.Name),
            r => Assert.Equal("my-container", r.Name)
        );
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl)]
    public async Task DashboardDoesNotAddResource_ConfiguresExistingDashboard(string dashboardOtlpGrpcEndpointUrlKey)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.Services.AddSingleton<IDashboardEndpointProvider, MockDashboardEndpointProvider>();

        builder.Configuration.Sources.Clear();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://localhost",
            [dashboardOtlpGrpcEndpointUrlKey] = "http://localhost",
            [KnownConfigNames.DashboardMcpEndpointUrl] = "http://localhost"
        });

        var container = builder.AddContainer(KnownResourceNames.AspireDashboard, "my-image");

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources);

        SetDashboardAllocatedEndpoints(dashboard, otlpGrpcPort: 5001, otlpHttpPort: 5002, httpPort: 5003, mcpPort: 5004);

        Assert.Same(container.Resource, dashboard);

        var config = (await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dashboard, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout())
            .OrderBy(c => c.Key)
            .ToList();

        Assert.Collection(config,
            e =>
            {
                Assert.Equal(KnownConfigNames.DashboardMcpEndpointUrl, e.Key);
                Assert.Equal("http://localhost:5004", e.Value);
            },
            e =>
            {
                Assert.Equal(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, e.Key);
                Assert.Equal("http://localhost:5001", e.Value);
            },
            e =>
            {
                Assert.Equal(KnownConfigNames.ResourceServiceEndpointUrl, e.Key);
                Assert.Equal("http://localhost:5000", e.Value);
            },
            e =>
            {
                Assert.Equal("ASPNETCORE_ENVIRONMENT", e.Key);
                Assert.Equal("Production", e.Value);
            },
            e =>
            {
                Assert.Equal(KnownConfigNames.AspNetCoreUrls, e.Key);
                Assert.Equal("http://localhost:5003", e.Value);
            },
            e =>
            {
                Assert.Equal("DASHBOARD__FRONTEND__AUTHMODE", e.Key);
                Assert.Equal("Unsecured", e.Value);
            },
            e =>
            {
                Assert.Equal("DASHBOARD__FRONTEND__PUBLICURL", e.Key);
                Assert.Equal("http://localhost:5003", e.Value);
            },
            e =>
            {
                Assert.Equal("DASHBOARD__MCP__AUTHMODE", e.Key);
                Assert.Equal("Unsecured", e.Value);
            },
            e =>
            {
                Assert.Equal("DASHBOARD__MCP__PUBLICURL", e.Key);
                Assert.Equal("http://localhost:5004", e.Value);
            },
            e =>
            {
                Assert.Equal("DASHBOARD__MCP__USECLIMCP", e.Key);
                Assert.Equal("true", e.Value);
            },
            e =>
            {
                Assert.Equal("DASHBOARD__OTLP__AUTHMODE", e.Key);
                Assert.Equal("Unsecured", e.Value);
            },
            e =>
            {
                Assert.Equal("DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE", e.Key);
                Assert.Equal("Unsecured", e.Value);
            },
            e =>
            {
                Assert.Equal("LOGGING__CONSOLE__FORMATTERNAME", e.Key);
                Assert.Equal("json", e.Value);
            }
        );
    }

    [Theory]
    [InlineData(5004, "http://localhost")] // MCP port
    [InlineData(5003, null)] // HTTP port
    public async Task DashboardDoesNotAddResource_ConfiguresMcpEndpoint(int expectedPort, string? mcpEndpointUrl)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.Services.AddSingleton<IDashboardEndpointProvider, MockDashboardEndpointProvider>();

        builder.Configuration.Sources.Clear();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://localhost",
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            [KnownConfigNames.DashboardMcpEndpointUrl] = mcpEndpointUrl
        });

        var container = builder.AddContainer(KnownResourceNames.AspireDashboard, "my-image");

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources);

        SetDashboardAllocatedEndpoints(dashboard, otlpGrpcPort: 5001, otlpHttpPort: 5002, httpPort: 5003, mcpPort: 5004);

        Assert.Same(container.Resource, dashboard);

        var config = (await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dashboard, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout())
            .OrderBy(c => c.Key)
            .ToList();

        Assert.Equal($"http://localhost:{expectedPort}", config.Single(e => e.Key == DashboardConfigNames.DashboardMcpPublicUrlName.EnvVarName).Value);
        Assert.Equal($"http://localhost:{expectedPort}", config.Single(e => e.Key == DashboardConfigNames.DashboardMcpUrlName.EnvVarName).Value);
    }

    [Fact]
    public async Task DashboardWithDllPathLaunchesDotnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        var dashboardPath = Path.GetFullPath("dashboard.dll");

        builder.Services.Configure<DcpOptions>(o =>
        {
            o.DashboardPath = dashboardPath;
        });

        var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources.OfType<ExecutableResource>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(dashboard).DefaultTimeout();

        Assert.NotNull(dashboard);
        Assert.Equal("aspire-dashboard", dashboard.Name);
        Assert.Equal("dotnet", dashboard.Command);
        Assert.Equal("exec", args[0]);
        Assert.Equal("--runtimeconfig", args[1]);
        Assert.EndsWith(".json", args[2]); // Generated temp runtimeconfig.json path
        Assert.Equal(dashboardPath, args[3]);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl)]
    public async Task DashboardAuthConfigured_EnvVarsPresent(string dashboardOtlpGrpcEndpointUrlKey)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.Services.AddSingleton<IDashboardEndpointProvider, MockDashboardEndpointProvider>();

        builder.Configuration.Sources.Clear();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.AspNetCoreUrls] = "http://localhost",
            [dashboardOtlpGrpcEndpointUrlKey] = "http://localhost",
            ["AppHost:BrowserToken"] = "TestBrowserToken!",
            ["AppHost:OtlpApiKey"] = "TestOtlpApiKey!"
        });

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources);

        SetDashboardAllocatedEndpoints(dashboard, otlpGrpcPort: 5001, otlpHttpPort: 5002, httpPort: 5000, mcpPort: 5003);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dashboard, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("BrowserToken", config.Single(e => e.Key == DashboardConfigNames.DashboardFrontendAuthModeName.EnvVarName).Value);
        Assert.Equal("TestBrowserToken!", config.Single(e => e.Key == DashboardConfigNames.DashboardFrontendBrowserTokenName.EnvVarName).Value);

        Assert.Equal("ApiKey", config.Single(e => e.Key == DashboardConfigNames.DashboardOtlpAuthModeName.EnvVarName).Value);
        Assert.Equal("TestOtlpApiKey!", config.Single(e => e.Key == DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.EnvVarName).Value);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl)]
    public async Task DashboardAuthRemoved_EnvVarsUnsecured(string dashboardOtlpGrpcEndpointUrlKey)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.Services.AddSingleton<IDashboardEndpointProvider, MockDashboardEndpointProvider>();

        builder.Configuration.Sources.Clear();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.AspNetCoreUrls] = "http://localhost",
            [dashboardOtlpGrpcEndpointUrlKey] = "http://localhost"
        });

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources);

        SetDashboardAllocatedEndpoints(dashboard, otlpGrpcPort: 5001, otlpHttpPort: 5002, httpPort: 5000, mcpPort: 5003);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dashboard, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("Unsecured", config.Single(e => e.Key == DashboardConfigNames.DashboardFrontendAuthModeName.EnvVarName).Value);
        Assert.Equal("Unsecured", config.Single(e => e.Key == DashboardConfigNames.DashboardOtlpAuthModeName.EnvVarName).Value);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)]
    [InlineData(KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl)]
    public async Task DashboardResourceServiceUriIsSet(string dashboardOtlpGrpcEndpointUrlKey)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.Services.AddSingleton<IDashboardEndpointProvider, MockDashboardEndpointProvider>();

        builder.Configuration.Sources.Clear();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.AspNetCoreUrls] = "http://localhost",
            [dashboardOtlpGrpcEndpointUrlKey] = "http://localhost"
        });

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources);

        SetDashboardAllocatedEndpoints(dashboard, otlpGrpcPort: 5001, otlpHttpPort: 5002, httpPort: 5000, mcpPort: 5003);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dashboard, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://localhost:5000", config.Single(e => e.Key == DashboardConfigNames.ResourceServiceUrlName.EnvVarName).Value);
    }

    [Theory]
    [InlineData("*", KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.DashboardCorsAllowedOrigins)]
    [InlineData(null, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardCorsAllowedOrigins)]
    public async Task DashboardResource_OtlpHttpEndpoint_CorsEnvVarSet(string? explicitCorsAllowedOrigins, string otlpHttpEndpointUrlKey, string corsAllowedOriginsKey)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.AddContainer("my-container", "my-image").WithHttpEndpoint(port: 8080, targetPort: 58080);

        builder.Services.AddSingleton<IDashboardEndpointProvider, MockDashboardEndpointProvider>();

        builder.Configuration.Sources.Clear();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.AspNetCoreUrls] = "http://localhost",
            [otlpHttpEndpointUrlKey] = "http://localhost",
            [corsAllowedOriginsKey] = explicitCorsAllowedOrigins
        });

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Hack in an AllocatedEndpoint. This is what is used to build the list of CORS endpoints.
        var container = Assert.Single(model.Resources, r => r.Name == "my-container");
        var endpointAnnotation = Assert.Single(container.Annotations.OfType<EndpointAnnotation>());
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8081);

        var dashboard = Assert.Single(model.Resources, r => r.Name == "aspire-dashboard");

        SetDashboardAllocatedEndpoints(dashboard, otlpGrpcPort: 5001, otlpHttpPort: 5002, httpPort: 5003, mcpPort: 5004);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dashboard, DistributedApplicationOperation.Run, app.Services).DefaultTimeout();

        var expectedAllowedOrigins = !string.IsNullOrEmpty(explicitCorsAllowedOrigins) ? explicitCorsAllowedOrigins : "http://localhost:8081,http://localhost:58080";
        Assert.Equal(expectedAllowedOrigins, config.Single(e => e.Key == DashboardConfigNames.DashboardOtlpCorsAllowedOriginsKeyName.EnvVarName).Value);
        Assert.Equal("*", config.Single(e => e.Key == DashboardConfigNames.DashboardOtlpCorsAllowedHeadersKeyName.EnvVarName).Value);
        Assert.DoesNotContain(config, e => e.Key == corsAllowedOriginsKey);
    }

    [Theory]
    [InlineData("*", KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.DashboardCorsAllowedOrigins)]
    [InlineData(null, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardCorsAllowedOrigins)]
    public async Task DashboardResource_OtlpGrpcEndpoint_CorsEnvVarNotSet(string? explicitCorsAllowedOrigins, string otlpGrpcEndpointUrlKey, string corsAllowedOriginsKey)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        builder.AddContainer("my-container", "my-image").WithHttpEndpoint(port: 8080, targetPort: 58080);

        builder.Services.AddSingleton<IDashboardEndpointProvider, MockDashboardEndpointProvider>();

        builder.Configuration.Sources.Clear();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.AspNetCoreUrls] = "http://localhost",
            [otlpGrpcEndpointUrlKey] = "http://localhost",
            [corsAllowedOriginsKey] = explicitCorsAllowedOrigins
        });

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources, r => r.Name == "aspire-dashboard");

        SetDashboardAllocatedEndpoints(dashboard, otlpGrpcPort: 5001, otlpHttpPort: 5002, httpPort: 5003, mcpPort: 5004);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dashboard, DistributedApplicationOperation.Run, app.Services).DefaultTimeout();

        Assert.DoesNotContain(config, e => e.Key == DashboardConfigNames.DashboardOtlpCorsAllowedOriginsKeyName.EnvVarName);
        Assert.DoesNotContain(config, e => e.Key == DashboardConfigNames.DashboardOtlpCorsAllowedHeadersKeyName.EnvVarName);
    }

    [Fact]
    public async Task DashboardIsNotAddedInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options =>
            {
                options.DisableDashboard = false;
                options.Args = ["--publisher", "manifest"];
            },
            testOutputHelper: testOutputHelper);

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Empty(model.Resources);
    }

    [Fact]
    public async Task DashboardIsNotAddedIfDisabled()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true,
            testOutputHelper: testOutputHelper);

        var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Empty(model.Resources);
    }

    [Fact]
    public void ContainerIsValidWithDashboardIsDisabled()
    {
        // Set the host environment to "Development" so that the container validates services.
        using var builder = TestDistributedApplicationBuilder.Create(
            options =>
            {
                options.DisableDashboard = true;
                options.Args = ["--environment", "Development"];
            },
            testOutputHelper: testOutputHelper);

        // Container validation logic runs when the service provider is built.
        using var app = builder.Build();
    }

    [Theory]
    [InlineData(LogLevel.Critical)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    public async Task DashboardLifecycleEventsWatchesLogs(LogLevel logLevel)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            testOutputHelper: testOutputHelper);

        var loggerProvider = new TestLoggerProvider();

        builder.Services.AddLogging(b =>
        {
            b.AddProvider(loggerProvider);
            b.AddFilter("Aspire.Hosting.Dashboard", logLevel);
        });

        var dashboardPath = Path.GetFullPath("dashboard");

        builder.Services.Configure<DcpOptions>(o =>
        {
            o.DashboardPath = dashboardPath;
        });

        var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        var watchForLogSubs = Task.Run(async () =>
        {
            await foreach (var sub in resourceLoggerService.WatchAnySubscribersAsync())
            {
                if (sub.AnySubscribers)
                {
                    break;
                }
            }
        });

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();

        var dashboard = Assert.Single(model.Resources.OfType<ExecutableResource>());

        Assert.NotNull(dashboard);
        Assert.Equal("aspire-dashboard", dashboard.Name);

        // Push a notification through to the dashboard resource.
        await resourceNotificationService.PublishUpdateAsync(dashboard, "aspire-dashboard-0", s => s with { State = "Running" }).DefaultTimeout();

        // Wait for logs to be subscribed to
        await watchForLogSubs.DefaultTimeout();

        // Push some logs through to the dashboard resource.
        var logger = resourceLoggerService.GetLogger("aspire-dashboard-0");

        // The logging watcher expects a JSON payload
        var dashboardLogMessage = new DashboardLogMessage
        {
            Category = "Test",
            LogLevel = logLevel,
            Message = "Test dashboard message"
        };

        logger.Log(logLevel, 0, JsonSerializer.Serialize(dashboardLogMessage), null, (s, _) => s);

        // Get the logger with the category we expect Aspire.Hosting.Dashboard.Test
        var testLogger = loggerProvider.CreateLogger("Aspire.Hosting.Dashboard.Test") as TestLogger;

        Assert.NotNull(testLogger);

        // Get the first log message that was logged
        var log = await testLogger.FirstLogTask.DefaultTimeout();

        Assert.Equal("Test dashboard message", log.Message);
        Assert.Equal(logLevel, log.LogLevel);

        await app.DisposeAsync().AsTask().DefaultTimeout();
    }

    [Fact]
    public async Task DashboardIsExcludedFromManifestInPublishModeEvenIfAddedExplicitly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddProject<DashboardProject>(KnownResourceNames.AspireDashboard);

        var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dashboard = Assert.Single(model.Resources.OfType<ProjectResource>());

        Assert.NotNull(dashboard);
        var annotation = Assert.Single(dashboard.Annotations.OfType<ManifestPublishingCallbackAnnotation>());

        var manifest = await ManifestUtils.GetManifestOrNull(dashboard).DefaultTimeout();

        Assert.Equal("aspire-dashboard", dashboard.Name);
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore, annotation);
        Assert.Null(manifest);
    }

    static void SetDashboardAllocatedEndpoints(IResource dashboard, int otlpGrpcPort, int otlpHttpPort, int httpPort, int mcpPort)
    {
        foreach (var endpoint in dashboard.Annotations.OfType<EndpointAnnotation>())
        {
            if (endpoint.Name == DashboardEventHandlers.OtlpGrpcEndpointName)
            {
                endpoint.AllocatedEndpoint = new(endpoint, "localhost", otlpGrpcPort, targetPortExpression: otlpGrpcPort.ToString());
            }
            else if (endpoint.Name == DashboardEventHandlers.OtlpHttpEndpointName)
            {
                endpoint.AllocatedEndpoint = new(endpoint, "localhost", otlpHttpPort, targetPortExpression: otlpHttpPort.ToString());
            }
            else if (endpoint.Name == DashboardEventHandlers.McpEndpointName)
            {
                endpoint.AllocatedEndpoint = new(endpoint, "localhost", mcpPort, targetPortExpression: mcpPort.ToString());
            }
            else if (endpoint.Name == "http")
            {
                endpoint.AllocatedEndpoint = new(endpoint, "localhost", httpPort, targetPortExpression: httpPort.ToString());
            }
        }
    }

    private sealed class DashboardProject : IProjectMetadata
    {
        public string ProjectPath => "dashboard.csproj";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class TestLogger : ILogger
    {
        private readonly TaskCompletionSource<LogMessage> _tcs = new();

        public Task<LogMessage> FirstLogTask => _tcs.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = new LogMessage
            {
                LogLevel = logLevel,
                Message = formatter(state, exception)
            };

            _tcs.TrySetResult(message);
        }

        public sealed class LogMessage
        {
            public LogLevel LogLevel { get; set; }
            public string Message { get; set; } = string.Empty;
        }
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, TestLogger> _loggers = new();

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, _ => new TestLogger());
        }

        public void Dispose() { }
    }

    private sealed class MockDashboardEndpointProvider : IDashboardEndpointProvider
    {
        public Task<string> GetResourceServiceUriAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("http://localhost:5000");
        }
    }
}
