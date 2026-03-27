// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Blazor.Tests;

public class AddBlazorWasmAppTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddBlazorWasmApp_CreatesResource_WithCorrectProjectPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var wasmApp = builder.AddBlazorWasmApp("store", "MyApp/MyApp.csproj");

        var resource = Assert.Single(builder.Resources.OfType<BlazorWasmAppResource>());
        Assert.Equal("store", resource.Name);
        Assert.EndsWith("MyApp.csproj", resource.ProjectPath);
    }

    [Fact]
    public void AddBlazorWasmApp_SetsInitialState_ToWaiting()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var wasmApp = builder.AddBlazorWasmApp("store", "MyApp/MyApp.csproj");

        var resource = Assert.Single(builder.Resources.OfType<BlazorWasmAppResource>());
        var snapshot = resource.Annotations.OfType<ResourceSnapshotAnnotation>().FirstOrDefault();
        Assert.NotNull(snapshot);
        var state = snapshot.InitialSnapshot;
        Assert.Equal("BlazorWasmApp", state.ResourceType);
        Assert.Equal(KnownResourceStates.Waiting, state.State);
    }

    [Fact]
    public void AddBlazorWasmApp_ExcludedFromManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var wasmApp = builder.AddBlazorWasmApp("store", "MyApp/MyApp.csproj");

        var resource = Assert.Single(builder.Resources.OfType<BlazorWasmAppResource>());
        Assert.True(resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var annotation));
        Assert.Equal(ManifestPublishingCallbackAnnotation.Ignore, annotation);
    }

    [Fact]
    public void AddBlazorWasmApp_ProjectDirectory_ReturnsParentOfProjectPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var wasmApp = builder.AddBlazorWasmApp("store", "MyApp/MyApp.csproj");

        var resource = Assert.Single(builder.Resources.OfType<BlazorWasmAppResource>());
        Assert.Equal(Path.GetDirectoryName(resource.ProjectPath), resource.ProjectDirectory);
    }

    [Fact]
    public void AddBlazorWasmApp_ImplementsIResourceWithEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var wasmApp = builder.AddBlazorWasmApp("store", "MyApp/MyApp.csproj");

        var resource = Assert.Single(builder.Resources.OfType<BlazorWasmAppResource>());
        Assert.IsAssignableFrom<IResourceWithEnvironment>(resource);
    }

    [Fact]
    public void AddBlazorWasmApp_MultiplApps_CreatesDistinctResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        builder.AddBlazorWasmApp("admin", "Admin/Admin.csproj");

        var resources = builder.Resources.OfType<BlazorWasmAppResource>().ToList();
        Assert.Equal(2, resources.Count);
        Assert.Contains(resources, r => r.Name == "store");
        Assert.Contains(resources, r => r.Name == "admin");
    }
}
