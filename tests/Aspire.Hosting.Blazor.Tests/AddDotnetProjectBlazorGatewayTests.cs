// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001 // AddDotnetProject and the DotnetProjectResource-backed gateway are experimental
#pragma warning disable ASPIREPROJECTS001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Blazor.Tests;

public class AddDotnetProjectBlazorGatewayTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddDotnetProjectBlazorGateway_WithBlazorClientApp_AddsGatewayAppsAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddDotnetProjectBlazorGateway("gateway");
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        gateway.WithBlazorClientApp(wasmApp);

        Assert.True(gateway.Resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata));
        Assert.True(File.Exists(projectMetadata.ProjectPath));
        Assert.Equal("Gateway.cs", Path.GetFileName(projectMetadata.ProjectPath));
        Assert.Equal("Scripts", Path.GetFileName(Path.GetDirectoryName(projectMetadata.ProjectPath)));
        Assert.Equal(
            ["http", "https"],
            gateway.Resource.Annotations.OfType<EndpointAnnotation>().Select(e => e.Name));

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

        var gateway = builder.AddDotnetProjectBlazorGateway("gateway");

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
    public void AddDotnetProjectBlazorGateway_InPublishMode_ConfiguresSdkPublishing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddDotnetProjectBlazorGateway("gateway");

        Assert.True(gateway.Resource.SupportsDotnetProgramPublishing());
        Assert.IsAssignableFrom<IContainerFilesDestinationResource>(gateway.Resource);
    }
}
