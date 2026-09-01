// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Loader;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class ResourceProjectionTests
{
    [Fact]
    public async Task SelectedProjectionIsAuthoritativeAndOwnerRemainsSoleModelMember()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new ContainerImageAnnotation
            {
                Image = "legacy-owner-image",
                Tag = "latest"
            })
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container =>
                {
                    container.WithImage("projected-image:v2");
                    container.WithVolume("projection-data", "/var/data");
                    container.WithEnvironment("PROJECTION_SETTING", "projection");
                })
            .WithEnvironment("OWNER_SETTING", "owner");

        Assert.Collection(builder.Resources, resource => Assert.Same(executable.Resource, resource));

        var projection = Assert.IsType<ContainerResourceProjection<ExecutableResource>>(
            executable.Resource.GetEffectiveResource(builder.ExecutionContext));

        Assert.Same(executable.Resource, projection.Owner);
        Assert.NotSame(executable.Resource, projection);
        Assert.True(projection.TryGetContainerImageName(out var projectionImage));
        Assert.Equal("projected-image:v2", projectionImage);
        Assert.True(executable.Resource.TryGetContainerImageName(out var ownerImage));
        Assert.Equal("legacy-owner-image:latest", ownerImage);

        var mount = Assert.Single(projection.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("projection-data", mount.Source);
        Assert.Empty(executable.Resource.Annotations.OfType<ContainerMountAnnotation>());

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projection,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance);

        Assert.Equal("owner", environment["OWNER_SETTING"]);
        Assert.Equal("projection", environment["PROJECTION_SETTING"]);
    }

    [Fact]
    public void NoSelectedProjectionPreservesLegacyAnnotationDrivenResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new ContainerImageAnnotation
            {
                Image = "legacy-image",
                Tag = "latest"
            });

        var effectiveResource = executable.Resource.GetEffectiveResource(builder.ExecutionContext);

        Assert.Same(executable.Resource, effectiveResource);
        Assert.True(effectiveResource.IsContainer());
    }

    [Fact]
    public void ProjectionSelectionIsOperationScoped()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithImage("projected-image"));

        var runContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        Assert.Same(executable.Resource, executable.Resource.GetEffectiveResource(runContext));
        Assert.IsType<ContainerResourceProjection<ExecutableResource>>(
            executable.Resource.GetEffectiveResource(builder.ExecutionContext));
    }

    [Fact]
    public void AspireHosting13_5ResourceImplementationLoadsAndMutatesAnnotations()
    {
        Assert.Equal(
            typeof(Collection<IResourceAnnotation>),
            typeof(ResourceAnnotationCollection).BaseType);
        Assert.Equal(
            typeof(ResourceAnnotationCollection),
            typeof(IResource).GetProperty(nameof(IResource.Annotations))!.PropertyType);

        var assemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "BinaryCompatibilityAssets",
            "Aspire.Hosting.13.5.Integration.dll");

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var resourceType = assembly.GetType("Aspire.Hosting.BinaryCompatibility.LegacyResource", throwOnError: true)!;
        var resource = Assert.IsAssignableFrom<IResource>(Activator.CreateInstance(resourceType));

        var mutationCount = resourceType.GetMethod(
            "MutateAnnotations",
            BindingFlags.Instance | BindingFlags.Public)!.Invoke(resource, null);

        Assert.Equal(1, mutationCount);
        Assert.Single(resource.Annotations);
    }
}
