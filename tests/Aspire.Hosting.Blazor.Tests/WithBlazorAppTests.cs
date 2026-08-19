// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Blazor.Tests;

public class WithBlazorAppTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithBlazorApp_AddsGatewayAppsAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        gateway.WithBlazorApp(wasmApp, "store", []);

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Single(annotation.Apps);
        Assert.Equal("store", annotation.Apps[0].PathPrefix);
    }

    [Fact]
    public void WithBlazorApp_MultipleApps_AllRegistered()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var storeApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        var adminApp = builder.AddBlazorWasmApp("admin", "Admin/Admin.csproj");

        gateway
            .WithBlazorApp(storeApp, "store", [])
            .WithBlazorApp(adminApp, "admin", []);

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal(2, annotation.Apps.Count);
        Assert.Equal("store", annotation.Apps[0].PathPrefix);
        Assert.Equal("admin", annotation.Apps[1].PathPrefix);
    }

    [Fact]
    public void WithBlazorApp_InitializesAnnotation_OnlyOnce()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var storeApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        var adminApp = builder.AddBlazorWasmApp("admin", "Admin/Admin.csproj");

        gateway
            .WithBlazorApp(storeApp, "store", [])
            .WithBlazorApp(adminApp, "admin", []);

        // Should only have one GatewayAppsAnnotation
        var annotations = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().ToList();
        Assert.Single(annotations);
        Assert.True(annotations[0].IsInitialized);
    }

    [Fact]
    public void WithClient_ForwardsServiceReferences_ToGateway()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint();

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi);

        gateway.WithBlazorClientApp(wasmApp);

        // The gateway should now have a reference to weatherapi
        var gatewayRefs = gateway.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Select(r => r.Resource.Name)
            .ToList();

        Assert.Contains("weatherapi", gatewayRefs);
    }

    [Fact]
    public void WithClient_DoesNotDuplicateExistingReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint();

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint()
            .WithReference(weatherApi); // Already referencing weatherapi

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi);

        gateway.WithBlazorClientApp(wasmApp);

        // Count references to weatherapi on the gateway
        var weatherRefs = gateway.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Count(r => r.Resource.Name == "weatherapi");

        // Should have exactly 1 (not duplicated)
        Assert.Equal(1, weatherRefs);
    }

    [Fact]
    public void WithClient_WaitForDoesNotPreventReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint();

        // Gateway already has WaitFor(weatherApi) but not WithReference(weatherApi).
        // WithBlazorClientApp should still forward the reference so YARP gets service discovery env vars.
        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint()
            .WaitFor(weatherApi);

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi);

        gateway.WithBlazorClientApp(wasmApp);

        var refs = gateway.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Where(r => r.Resource.Name == "weatherapi" && r.Type == "Reference")
            .ToList();

        Assert.Single(refs);
    }

    [Fact]
    public void WithClient_UsesResourceName_AsPathPrefix()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("storefront", "Store/Store.csproj");

        gateway.WithBlazorClientApp(wasmApp);

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("storefront", annotation.Apps[0].PathPrefix);
    }

    [Fact]
    public void WithClient_AddsDebuggerResourceAndCommands()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithBlazorDebuggerBrowser("chrome");

        gateway.WithBlazorClientApp(wasmApp);

        var debuggerResource = Assert.Single(builder.Resources.OfType<BrowserDebuggerResource>());
        Assert.Equal("gateway-store-debugger", debuggerResource.Name);
        Assert.Equal("chrome", debuggerResource.Command);
        Assert.True(debuggerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
        Assert.True(debuggerResource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var manifestAnnotation));
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore, manifestAnnotation);

        var parentRelationship = Assert.Single(
            debuggerResource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            annotation => annotation.Type == "Parent");
        Assert.Same(gateway.Resource, parentRelationship.Resource);

        Assert.True(debuggerResource.TryGetLastAnnotation<ResourceSnapshotAnnotation>(out var snapshotAnnotation));
        Assert.Equal("BrowserDebugger", snapshotAnnotation.InitialSnapshot.ResourceType);
        Assert.True(snapshotAnnotation.InitialSnapshot.IsHidden);

        var commandNames = wasmApp.Resource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .Select(annotation => annotation.Name)
            .ToList();
        Assert.Contains("debug-in-browser", commandNames);
        Assert.Contains("stop-browser-debug", commandNames);
        Assert.DoesNotContain(gateway.Resource.Annotations, annotation => annotation is ResourceCommandAnnotation);
    }

    [Fact]
    public void WithClient_InPublishMode_DoesNotAddDebuggerResourceOrCommands()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        gateway.WithBlazorClientApp(wasmApp);

        Assert.Empty(builder.Resources.OfType<BrowserDebuggerResource>());
        Assert.DoesNotContain(wasmApp.Resource.Annotations, annotation => annotation is ResourceCommandAnnotation);
        Assert.Single(gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>());
    }

    [Theory]
    [InlineData("Running", ResourceCommandState.Enabled)]
    [InlineData("Starting", ResourceCommandState.Disabled)]
    [InlineData("Exited", ResourceCommandState.Disabled)]
    public void WithClient_DebuggerCommandsHaveExpectedInitialState(string resourceState, ResourceCommandState expectedDebugState)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        ConfigureBrowserDebugging(builder, browserCapabilitySupported: true);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        gateway.WithBlazorClientApp(wasmApp);

        var commands = wasmApp.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToDictionary(annotation => annotation.Name);
        var context = new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "BlazorWasmApp",
                Properties = [],
                State = resourceState
            },
            Services = new ServiceCollection().BuildServiceProvider()
        };

        Assert.Equal(expectedDebugState, commands["debug-in-browser"].UpdateState(context));
        Assert.Equal(ResourceCommandState.Hidden, commands["stop-browser-debug"].UpdateState(context));
    }

    [Theory]
    [InlineData(false, null, ResourceCommandState.Disabled)]
    [InlineData(true, null, ResourceCommandState.Disabled)]
    [InlineData(true, false, ResourceCommandState.Disabled)]
    [InlineData(true, true, ResourceCommandState.Enabled)]
    public void WithClient_DebugInBrowserCommandRequiresBrowserDebuggingSupport(
        bool debugSessionActive,
        bool? browserCapabilitySupported,
        ResourceCommandState expectedState)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["DEBUG_SESSION_PORT"] = debugSessionActive ? "localhost:1234" : null;

        if (browserCapabilitySupported is not null)
        {
            ConfigureBrowserDebugging(builder, browserCapabilitySupported.Value);
        }

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        gateway.WithBlazorClientApp(wasmApp);

        var command = Assert.Single(
            wasmApp.Resource.Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == "debug-in-browser");
        var context = new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "BlazorWasmApp",
                Properties = [],
                State = KnownResourceStates.Running
            },
            Services = new ServiceCollection().BuildServiceProvider()
        };

        Assert.Equal(expectedState, command.UpdateState(context));
    }

    [Fact]
    public async Task WithClient_DebugInBrowserCommandFailsWithoutBrowserDebuggingSupport()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        gateway.WithBlazorClientApp(wasmApp);

        var command = Assert.Single(
            wasmApp.Resource.Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == "debug-in-browser");
        using var services = new ServiceCollection().BuildServiceProvider();

        var result = await command.ExecuteCommand(new ExecuteCommandContext
        {
            Services = services,
            ResourceName = wasmApp.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.Equal(
            "Browser debugging requires an active IDE debug session that supports the 'browser' launch configuration.",
            result.Message);
    }

    [Fact]
    public async Task WithClient_CreatesBrowserLaunchConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint()
            .WithEndpoint("https", endpoint => endpoint.AllocatedEndpoint = new(endpoint, "localhost", 7443), createIfNotExists: false);

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithBlazorDebuggerBrowser("chrome");

        gateway.WithBlazorClientApp(wasmApp);

        var debuggerResource = Assert.Single(builder.Resources.OfType<BrowserDebuggerResource>());
        var launchConfiguration = await CreateBrowserLaunchConfigurationAsync(debuggerResource);

        Assert.Equal(ExecutableLaunchMode.Debug, launchConfiguration.Mode);
        Assert.Equal("https://localhost:7443/store/", launchConfiguration.Url);
        Assert.Equal(wasmApp.Resource.ProjectPath, launchConfiguration.WebRoot);
        Assert.Equal("chrome", launchConfiguration.Browser);
    }

    [Fact]
    public void WithClient_CanDisableTelemetryProxy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        gateway.WithBlazorClientApp(wasmApp, proxyTelemetry: false);

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.False(annotation.Apps[0].ProxyBlazorTelemetry);
    }

    [Fact]
    public void WithClient_ForwardsAllEndpoints_WhenUseAllEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        // WithReference(weatherApi) sets UseAllEndpoints = true on the WASM app's annotation.
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi);

        gateway.WithBlazorClientApp(wasmApp);

        // The gateway should have an EndpointReferenceAnnotation for weatherapi with UseAllEndpoints = true.
        var endpointRef = gateway.Resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .SingleOrDefault(a => a.Resource.Name == "weatherapi");

        Assert.NotNull(endpointRef);
        Assert.True(endpointRef.UseAllEndpoints);
    }

    [Fact]
    public void WithClient_ForwardsSpecificEndpoints_WhenNamedEndpointReferenced()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint(name: "public")
            .WithHttpsEndpoint(name: "internal");

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        // Reference only the "public" endpoint on the WASM app.
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi.GetEndpoint("public"));

        gateway.WithBlazorClientApp(wasmApp);

        // The gateway should have only the "public" endpoint forwarded (not UseAllEndpoints)
        // because a single named endpoint uses service discovery's named endpoint format.
        var endpointRef = gateway.Resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .SingleOrDefault(a => a.Resource.Name == "weatherapi");

        Assert.NotNull(endpointRef);
        Assert.False(endpointRef.UseAllEndpoints);
        Assert.Collection(endpointRef.EndpointNames,
            name => Assert.Equal("public", name));

        // The registration should map weatherapi → "public" endpoint name so the YARP
        // destination uses https+http://_public.weatherapi instead of scheme-based resolution.
        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().Single();
        var registration = Assert.Single(annotation.Apps);
        var service = Assert.Single(registration.Services);
        Assert.Equal("weatherapi", service.Name);
        Assert.Collection(service.EndpointNames,
            name => Assert.Equal("public", name));
    }

    [Fact]
    public void WithClient_ForwardsMultipleNamedEndpoints_Correctly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint(name: "public")
            .WithHttpsEndpoint(name: "admin")
            .WithHttpEndpoint(name: "health");

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        // Reference two specific endpoints on the WASM app.
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi.GetEndpoint("public"))
            .WithReference(weatherApi.GetEndpoint("admin"));

        gateway.WithBlazorClientApp(wasmApp);

        // The gateway should have only the specific named endpoints forwarded.
        var endpointRef = gateway.Resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .SingleOrDefault(a => a.Resource.Name == "weatherapi");

        Assert.NotNull(endpointRef);
        Assert.False(endpointRef.UseAllEndpoints);
        Assert.Collection(endpointRef.EndpointNames.Order(),
            name => Assert.Equal("admin", name),
            name => Assert.Equal("public", name));

        // The YARP destination uses the first endpoint name for named resolution.
        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().Single();
        var registration = Assert.Single(annotation.Apps);
        var service = Assert.Single(registration.Services);
        Assert.Equal("weatherapi", service.Name);
        Assert.Collection(service.EndpointNames.Order(),
            name => Assert.Equal("admin", name),
            name => Assert.Equal("public", name));
    }

    private static async Task<BrowserLaunchConfiguration> CreateBrowserLaunchConfigurationAsync(IResource resource)
    {
        var launchConfiguration = await resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug);
        return Assert.IsType<BrowserLaunchConfiguration>(launchConfiguration);
    }

    private static void ConfigureBrowserDebugging(IDistributedApplicationBuilder builder, bool browserCapabilitySupported)
    {
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:1234";

        // DEBUG_SESSION_INFO is emitted by the IDE in this shape:
        // {"protocols_supported":["2024-03-03"],"supported_launch_configurations":["browser"]}
        builder.Configuration["DEBUG_SESSION_INFO"] = browserCapabilitySupported
            ? """{"protocols_supported":["2024-03-03"],"supported_launch_configurations":["browser"]}"""
            : """{"protocols_supported":["2024-03-03"],"supported_launch_configurations":["project"]}""";
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "TestProject/TestProject.csproj";

        public LaunchSettings LaunchSettings { get; } = new();
    }
}
