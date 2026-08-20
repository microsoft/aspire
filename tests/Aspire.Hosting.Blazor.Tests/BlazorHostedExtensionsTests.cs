// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREFILESYSTEM001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Blazor.Tests;

public class BlazorHostedExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ProxyService_EmitsYarpRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        Assert.Equal("cluster-weatherapi", env["ReverseProxy__Routes__route-weatherapi__ClusterId"]);
        Assert.Equal("/_api/weatherapi/{**catch-all}", env["ReverseProxy__Routes__route-weatherapi__Match__Path"]);
        Assert.Equal("/_api/weatherapi", env["ReverseProxy__Routes__route-weatherapi__Transforms__0__PathRemovePrefix"]);
        Assert.Equal("https+http://weatherapi", env["ReverseProxy__Clusters__cluster-weatherapi__Destinations__d1__Address"]);
    }

    [Fact]
    public async Task ProxyService_EmitsClientConfigResponse()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpEndpoint()
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        Assert.True(env.ContainsKey("Client__ConfigResponse"));
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("services__weatherapi__https__0", configJson);
        Assert.Contains("services__weatherapi__http__0", configJson);
        Assert.Contains("/_api/weatherapi", configJson);
        Assert.Equal("/_blazor/_configuration", env["Client__ConfigEndpointPath"]);
    }

    [Fact]
    public async Task ProxyTelemetry_EmitsOtlpRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        Assert.Equal("cluster-otlp-dashboard", env["ReverseProxy__Routes__route-otlp__ClusterId"]);
        Assert.Equal("/_otlp/{**catch-all}", env["ReverseProxy__Routes__route-otlp__Match__Path"]);
        Assert.Equal("/_otlp", env["ReverseProxy__Routes__route-otlp__Transforms__0__PathRemovePrefix"]);
    }

    [Fact]
    public async Task ProxyTelemetry_EmitsOtelServiceNameInConfig()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("OTEL_SERVICE_NAME", configJson);
        Assert.Contains("blazorapp", configJson);
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", configJson);
        Assert.Contains("/_otlp", configJson);
    }

    [Fact]
    public async Task ProxyService_And_ProxyTelemetry_Combined()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi)
            .ProxyBlazorTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Service routes
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-weatherapi__ClusterId"));

        // OTLP routes
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-otlp__ClusterId"));

        // Config response includes both service URLs and OTLP
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("services__weatherapi__https__0", configJson);
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", configJson);
        Assert.Contains("OTEL_SERVICE_NAME", configJson);
    }

    [Fact]
    public async Task ProxyService_MultipleServices()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");
        var catalogApi = builder.AddProject<TestProjectMetadata>("catalogapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi)
            .ProxyBlazorService(catalogApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Both services have routes
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-weatherapi__ClusterId"));
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-catalogapi__ClusterId"));

        // Config response includes both services
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("services__weatherapi__https__0", configJson);
        Assert.Contains("services__catalogapi__https__0", configJson);
    }

    [Fact]
    public void ProxyMethods_AddOneDebuggerResourceAndCommandPair()
    {
        using var fileSystemService = new TestFileSystemService();
        using var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("blazor-hosted");
        var (serverProjectPath, _) = CreateBlazorHostedProjects(tempDirectory);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");
        var catalogApi = builder.AddProject<TestProjectMetadata>("catalogapi");

        var host = builder.AddProject("blazorapp", serverProjectPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithBlazorDebuggerBrowser("chrome")
            .ProxyBlazorService(weatherApi)
            .ProxyBlazorService(catalogApi)
            .ProxyBlazorTelemetry()
            .ProxyBlazorTelemetry();

        var debuggerResource = Assert.Single(builder.Resources.OfType<BrowserDebuggerResource>());
        Assert.Equal("blazorapp-wasm-debugger", debuggerResource.Name);
        Assert.Equal("chrome", debuggerResource.Command);

        var parentRelationship = Assert.Single(
            debuggerResource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            annotation => annotation.Type == "Parent");
        Assert.Same(host.Resource, parentRelationship.Resource);

        var commands = host.Resource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .Select(annotation => annotation.Name)
            .Order()
            .ToList();
        Assert.Collection(
            commands,
            command => Assert.Equal("debug-in-browser", command),
            command => Assert.Equal("stop-browser-debug", command));
    }

    [Fact]
    public async Task ProxyService_WithWasmClient_UsesResolvedClientProjectAsWebRoot()
    {
        using var fileSystemService = new TestFileSystemService();
        using var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("blazor-hosted");
        var (serverProjectPath, clientProjectPath) = CreateBlazorHostedProjects(tempDirectory);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        ConfigureBrowserDebugging(builder);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");
        var host = builder.AddProject("blazorapp", serverProjectPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithEndpoint("https", endpoint => endpoint.AllocatedEndpoint = new(endpoint, "localhost", 7443), createIfNotExists: false)
            .ProxyBlazorService(weatherApi);

        using var app = builder.Build();
        await PublishBeforeStartEventAsync(builder, app);

        var debuggerResource = Assert.Single(builder.Resources.OfType<BrowserDebuggerResource>());
        var launchConfiguration = await CreateBrowserLaunchConfigurationAsync(debuggerResource);
        var debugCommand = Assert.Single(
            host.Resource.Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == "debug-in-browser");

        Assert.Equal(clientProjectPath, launchConfiguration.WebRoot);
        Assert.Equal(ResourceCommandState.Enabled, debugCommand.UpdateState(CreateRunningCommandStateContext()));
    }

    [Fact]
    public async Task ProxyService_WithoutWasmClient_HidesDebugCommand()
    {
        using var fileSystemService = new TestFileSystemService();
        using var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("blazor-hosted");
        var serverProjectPath = CreateBlazorHostedServerWithoutClient(tempDirectory);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        ConfigureBrowserDebugging(builder);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");
        var host = builder.AddProject("blazorapp", serverProjectPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi);

        using var app = builder.Build();
        await PublishBeforeStartEventAsync(builder, app);

        var debuggerResource = Assert.Single(builder.Resources.OfType<BrowserDebuggerResource>());
        var debugCommand = Assert.Single(
            host.Resource.Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == "debug-in-browser");

        Assert.True(debuggerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
        Assert.Equal(ResourceCommandState.Hidden, debugCommand.UpdateState(CreateRunningCommandStateContext()));

        using var services = new ServiceCollection().BuildServiceProvider();
        var result = await debugCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = services,
            ResourceName = host.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.Contains("no Blazor WebAssembly client project", result.Message);
    }

    [Fact]
    public async Task ProxyService_WhenClientDiscoveryFails_SurfacesFailure()
    {
        using var fileSystemService = new TestFileSystemService();
        using var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("blazor-hosted");
        var serverProjectPath = Path.Combine(tempDirectory.Path, "Server.csproj");
        File.WriteAllText(serverProjectPath, """<Project Sdk="Microsoft.NET.Sdk.Web" />""");
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");
        builder.AddProject("blazorapp", serverProjectPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => PublishBeforeStartEventAsync(builder, app));

        Assert.Contains("ResolveWebAssemblyProjectReferences", exception.Message);
    }

    [Fact]
    public async Task ProxyService_NoPathPrefix()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Hosted mode uses no path prefix — URLs are relative to root
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("/_api/weatherapi", configJson);
        Assert.DoesNotContain("/blazorapp/", configJson);
    }

    [Fact]
    public async Task ProxyService_WithoutProxyTelemetry_NoOtlpInConfig()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Without ProxyBlazorTelemetry, no OTLP routes or config
        Assert.False(env.ContainsKey("ReverseProxy__Routes__route-otlp__ClusterId"));

        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_ENDPOINT", configJson);
    }

    [Fact]
    public void ProxyService_WaitForDoesNotPreventReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint();

        // Host already has WaitFor(weatherApi) but not WithReference(weatherApi).
        // ProxyBlazorService should still add the reference so YARP gets service discovery env vars.
        var host = builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .WaitFor(weatherApi)
            .ProxyBlazorService(weatherApi);

        var endpointRef = host.Resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .SingleOrDefault(a => a.Resource.Name == "weatherapi");

        Assert.NotNull(endpointRef);
    }

    [Fact]
    public async Task ProxyService_DifferentApiPrefixPerService()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");
        var catalogApi = builder.AddProject<TestProjectMetadata>("catalogapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi, apiPrefix: "weather")
            .ProxyBlazorService(catalogApi, apiPrefix: "catalog");

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Each service gets its own prefix in YARP routes
        Assert.Equal("/weather/weatherapi/{**catch-all}", env["ReverseProxy__Routes__route-weatherapi__Match__Path"]);
        Assert.Equal("/weather/weatherapi", env["ReverseProxy__Routes__route-weatherapi__Transforms__0__PathRemovePrefix"]);
        Assert.Equal("/catalog/catalogapi/{**catch-all}", env["ReverseProxy__Routes__route-catalogapi__Match__Path"]);
        Assert.Equal("/catalog/catalogapi", env["ReverseProxy__Routes__route-catalogapi__Transforms__0__PathRemovePrefix"]);

        // Client config also reflects per-service prefixes
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("/weather/weatherapi", configJson);
        Assert.Contains("/catalog/catalogapi", configJson);
        Assert.DoesNotContain("/_api/", configJson);
    }

    [Fact]
    public void ProxyService_DoesNotDuplicateExistingReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint();

        // Host already references weatherapi via WithReference.
        var host = builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .WithReference(weatherApi)
            .ProxyBlazorService(weatherApi);

        // Should not duplicate the endpoint reference annotation
        var weatherRefs = host.Resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .Count(a => a.Resource.Name == "weatherapi");

        Assert.Equal(1, weatherRefs);
    }

    [Fact]
    public void ProxyService_MultipleServices_AllGetEndpointReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint();
        var catalogApi = builder.AddProject<TestProjectMetadata>("catalogapi")
            .WithHttpEndpoint();

        var host = builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorService(weatherApi)
            .ProxyBlazorService(catalogApi);

        var referencedNames = host.Resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .Select(a => a.Resource.Name)
            .ToList();

        Assert.Contains("weatherapi", referencedNames);
        Assert.Contains("catalogapi", referencedNames);
    }

    [Fact]
    public async Task ProxyTelemetry_LogsWarning_WhenOtlpEndpointNotResolvable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        // Intentionally NOT setting ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var (_, sink) = await GetEnvironmentVariablesWithLogs(blazorApp, builder);

        Assert.Contains(sink.Writes, msg =>
            msg.LogLevel == LogLevel.Warning &&
            msg.Message?.Contains("OTLP telemetry proxying was requested") == true &&
            msg.Message?.Contains("WASM client telemetry will not be forwarded") == true);
    }

    [Fact]
    public async Task ProxyTelemetry_DoesNotLogWarning_WhenOtlpEndpointIsConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyBlazorTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var (_, sink) = await GetEnvironmentVariablesWithLogs(blazorApp, builder);

        Assert.DoesNotContain(sink.Writes, msg =>
            msg.LogLevel == LogLevel.Warning &&
            msg.Message?.Contains("OTLP telemetry proxying was requested") == true);
    }

    [Fact]
    public async Task WithBlazorClientApp_LogsWarning_WhenOtlpEndpointNotResolvable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        gateway.WithBlazorClientApp(wasmApp, proxyTelemetry: true);

        var (_, sink) = await GetEnvironmentVariablesWithLogs(gateway.Resource, builder);

        Assert.Contains(sink.Writes, msg =>
            msg.LogLevel == LogLevel.Warning &&
            msg.Message?.Contains("OTLP telemetry proxying was requested") == true &&
            msg.Message?.Contains("WASM client telemetry will not be forwarded") == true);
    }

    [Fact]
    public async Task WithBlazorClientApp_DoesNotLogWarning_WhenOtlpEndpointIsConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        gateway.WithBlazorClientApp(wasmApp, proxyTelemetry: true);

        var (_, sink) = await GetEnvironmentVariablesWithLogs(gateway.Resource, builder);

        Assert.DoesNotContain(sink.Writes, msg =>
            msg.LogLevel == LogLevel.Warning &&
            msg.Message?.Contains("OTLP telemetry proxying was requested") == true);
    }

    private static async Task<Dictionary<string, object>> GetEnvironmentVariables(
        IResource resource, IDistributedApplicationBuilder builder)
    {
        var (env, _) = await GetEnvironmentVariablesWithLogs(resource, builder);
        return env;
    }

    private static async Task<(Dictionary<string, object> Env, TestSink Sink)> GetEnvironmentVariablesWithLogs(
        IResource resource, IDistributedApplicationBuilder builder)
    {
        var env = new Dictionary<string, object>();
        var sink = new TestSink();
        var logger = new TestLogger(string.Empty, sink, enabled: true);
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, env)
        {
            Logger = logger
        };
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context).ConfigureAwait(false);
        }
        return (env, sink);
    }

    private static string ResolveManifestExpression(object value)
    {
        if (value is IManifestExpressionProvider provider)
        {
            return provider.ValueExpression;
        }
        return (string)value;
    }

    private static async Task<BrowserLaunchConfiguration> CreateBrowserLaunchConfigurationAsync(IResource resource)
    {
        var launchConfiguration = await resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug);
        return Assert.IsType<BrowserLaunchConfiguration>(launchConfiguration);
    }

    private static async Task PublishBeforeStartEventAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
    }

    private static UpdateCommandStateContext CreateRunningCommandStateContext()
    {
        return new()
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "Project",
                Properties = [],
                State = KnownResourceStates.Running
            },
            Services = new ServiceCollection().BuildServiceProvider()
        };
    }

    private static void ConfigureBrowserDebugging(IDistributedApplicationBuilder builder)
    {
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:1234";
        builder.Configuration["DEBUG_SESSION_INFO"] =
            """{"protocols_supported":["2024-03-03"],"supported_launch_configurations":["browser"]}""";
    }

    private static (string ServerProjectPath, string ClientProjectPath) CreateBlazorHostedProjects(TempDirectory tempDirectory)
    {
        var serverDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "Server"));
        var clientDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "Client"));
        var serverProjectPath = Path.Combine(serverDirectory.FullName, "Server.csproj");
        var clientProjectPath = Path.Combine(clientDirectory.FullName, "Client.csproj");

        File.WriteAllText(serverProjectPath, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="../Client/Client.csproj" />
              </ItemGroup>
              <Target Name="ResolveWebAssemblyProjectReferences">
                <MSBuild Projects="@(ProjectReference)"
                         Targets="GetWebAssemblyProjectReference"
                         BuildInParallel="true"
                         SkipNonexistentTargets="true">
                  <Output TaskParameter="TargetOutputs" ItemName="WebAssemblyProjectReference" />
                </MSBuild>
              </Target>
            </Project>
            """);
        File.WriteAllText(clientProjectPath, """
            <Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
              <Target Name="GetWebAssemblyProjectReference"
                      Returns="@(_WebAssemblyProjectReference)">
                <ItemGroup>
                  <_WebAssemblyProjectReference Include="$(MSBuildProjectFullPath)" />
                </ItemGroup>
              </Target>
            </Project>
            """);

        return (serverProjectPath, clientProjectPath);
    }

    private static string CreateBlazorHostedServerWithoutClient(TempDirectory tempDirectory)
    {
        var serverProjectPath = Path.Combine(tempDirectory.Path, "Server.csproj");
        File.WriteAllText(serverProjectPath, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <Target Name="ResolveWebAssemblyProjectReferences" />
            </Project>
            """);
        return serverProjectPath;
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "TestProject/TestProject.csproj";

        public LaunchSettings LaunchSettings { get; } = new();
    }
}
