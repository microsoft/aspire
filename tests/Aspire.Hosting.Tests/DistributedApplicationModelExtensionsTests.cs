// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class DistributedApplicationModelExtensionsTests
{
    [Fact]
    public void GetComputeResources_Returns_Containers_Emulators_And_Projects_Excludes_Ignored()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container1 = builder.AddContainer("container1", "image");
        var container2 = builder.AddContainer("container2", "image");
        var project = builder.AddProject<Projects.ServiceA>("ServiceA");
        var emulator = builder.AddResource(new CustomResource() { Annotations = { new EmulatorResourceAnnotation() } });
        var ignored = builder.AddContainer("container3", "image")
            .ExcludeFromManifest();

        var notACompute = builder.AddExecutable("notACompute", "path/to/executable", ".");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var result = appModel.GetComputeResources().ToList();

        Assert.Collection(result,
            item => Assert.Equal(container1.Resource, item),
            item => Assert.Equal(container2.Resource, item),
            item => Assert.Equal(project.Resource, item),
            item => Assert.Equal(emulator.Resource, item));
    }

    [Fact]
    public void GetBuildAndPushResources_Returns_Projects_And_Resources_With_Dockerfiles_Excludes_BuildOnly_And_Ignored()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Projects always require build and push
        var project = builder.AddProject<Projects.ServiceA>("ServiceA");
        
        // Regular containers don't require build/push (they use existing images)
        var regularContainer = builder.AddContainer("regularContainer", "image");
        
        // Containers with DockerfileBuildAnnotation require build and push
        var containerWithDockerfile = builder.AddContainer("containerWithDockerfile", "image");
        containerWithDockerfile.Resource.Annotations.Add(new DockerfileBuildAnnotation("/context", "/Dockerfile", null));
        
        // Build-only containers (no entrypoint) should be excluded
        var buildOnlyContainer = builder.AddContainer("buildOnlyContainer", "image");
        buildOnlyContainer.Resource.Annotations.Add(new DockerfileBuildAnnotation("/context", "/Dockerfile", null) { HasEntrypoint = false });
        
        // Excluded resources should not be returned
        var ignored = builder.AddProject<Projects.ServiceB>("ServiceB")
            .ExcludeFromManifest();

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var result = appModel.GetBuildAndPushResources().ToList();

        Assert.Collection(result,
            item => Assert.Equal(project.Resource, item),
            item => Assert.Equal(containerWithDockerfile.Resource, item));
    }

    private sealed class CustomResource : IResource
    {
        public string Name { get; set; } = "CustomResource";

        public ResourceAnnotationCollection Annotations { get; } = [];
    }
}
