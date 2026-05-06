// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

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

        gateway.WithBlazorApp(wasmApp, "store");

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
            .WithBlazorApp(storeApp, "store")
            .WithBlazorApp(adminApp, "admin");

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal(2, annotation.Apps.Count);
        Assert.Equal("store", annotation.Apps[0].PathPrefix);
        Assert.Equal("admin", annotation.Apps[1].PathPrefix);
    }

    [Fact]
    public void WithBlazorApp_ServiceNames_AreStored()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        gateway.WithBlazorApp(wasmApp, "store", ["weatherapi", "catalogapi"]);

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal(new[] { "weatherapi", "catalogapi" }, annotation.Apps[0].ServiceNames);
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
            .WithBlazorApp(storeApp, "store")
            .WithBlazorApp(adminApp, "admin");

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

        gateway.WithClient(wasmApp);

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

        gateway.WithClient(wasmApp);

        // Count references to weatherapi on the gateway
        var weatherRefs = gateway.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Count(r => r.Resource.Name == "weatherapi");

        // Should have exactly 1 (not duplicated)
        Assert.Equal(1, weatherRefs);
    }

    [Fact]
    public void WithClient_UsesResourceName_AsPathPrefix()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("storefront", "Store/Store.csproj");

        gateway.WithClient(wasmApp);

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("storefront", annotation.Apps[0].PathPrefix);
    }

    [Fact]
    public void WithClient_CanDisableTelemetryProxy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        gateway.WithClient(wasmApp, proxyTelemetry: false);

        var annotation = gateway.Resource.Annotations.OfType<GatewayAppsAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.False(annotation.Apps[0].ProxyTelemetry);
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "TestProject/TestProject.csproj";

        public LaunchSettings LaunchSettings { get; } = new();
    }
}
