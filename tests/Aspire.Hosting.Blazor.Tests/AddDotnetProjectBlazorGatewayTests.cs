// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001 // AddDotnetProject and the DotnetProjectResource-backed gateway are experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Blazor.Tests;

public class AddDotnetProjectBlazorGatewayTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithBlazorApp_DotnetProjectGateway_AddsGatewayAppsAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // The gateway helpers are generic over the gateway resource type, so a DotnetProjectResource
        // gateway (created via AddDotnetProject) flows through the same path as an AddBlazorGateway
        // (ProjectResource) gateway. Build the gateway directly to avoid depending on the Gateway.cs
        // script content that AddDotnetProjectBlazorGateway resolves at run time. ExcludeLaunchProfile
        // avoids resolving launchSettings for the placeholder project path.
        var gateway = builder.AddDotnetProject("gateway", "gateway.csproj", o => o.ExcludeLaunchProfile = true)
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
    public void WithBlazorClientApp_DotnetProjectGateway_ForwardsServiceReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddDotnetProject("weatherapi", "weatherapi.csproj", o => o.ExcludeLaunchProfile = true)
            .WithHttpEndpoint();

        var gateway = builder.AddDotnetProject("gateway", "gateway.csproj", o => o.ExcludeLaunchProfile = true)
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi);

        gateway.WithBlazorClientApp(wasmApp);

        // The reference declared on the WASM app must be forwarded to the gateway so YARP can
        // resolve service endpoints via Aspire's service discovery.
        var gatewayRefs = gateway.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Select(r => r.Resource.Name)
            .ToList();

        Assert.Contains("weatherapi", gatewayRefs);
    }

    [Fact]
    public void AddDotnetProjectBlazorGateway_InPublishMode_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Publishing a DotnetProjectResource-backed gateway is not supported yet because the resource
        // is not an IContainerFilesDestinationResource, so the WASM static-asset merge would be skipped.
        Assert.Throws<NotSupportedException>(() => builder.AddDotnetProjectBlazorGateway("gateway"));
    }
}
