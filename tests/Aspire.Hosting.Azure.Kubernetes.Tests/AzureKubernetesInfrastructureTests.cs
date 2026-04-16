// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesInfrastructureTests
{
    [Fact]
    public async Task NoUserPool_CreatesDefaultWorkloadPool()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // No AddNodePool call — only the default system pool exists
        Assert.Single(aks.Resource.NodePools);
        Assert.Equal(AksNodePoolMode.System, aks.Resource.NodePools[0].Mode);

        var container = builder.AddContainer("myapi", "myimage");

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        // Infrastructure should have added a default "workload" user pool
        Assert.Equal(2, aks.Resource.NodePools.Count);
        var workloadPool = aks.Resource.NodePools.First(p => p.Mode is AksNodePoolMode.User);
        Assert.Equal("workload", workloadPool.Name);

        // Compute resource should have been auto-assigned to the workload pool
        Assert.True(container.Resource.TryGetLastAnnotation<KubernetesNodePoolAnnotation>(out var affinity));
        Assert.Equal("workload", affinity.NodePool.Name);
    }

    [Fact]
    public async Task ExplicitUserPool_NoDefaultCreated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);

        var container = builder.AddContainer("myapi", "myimage");

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        // Should NOT create a default pool since one already exists
        Assert.Equal(2, aks.Resource.NodePools.Count); // system + gpu
        Assert.DoesNotContain(aks.Resource.NodePools, p => p.Name == "workload");

        // Unaffinitized compute resource should get assigned to the first user pool
        Assert.True(container.Resource.TryGetLastAnnotation<KubernetesNodePoolAnnotation>(out var affinity));
        Assert.Equal("gpu", affinity.NodePool.Name);
    }

    [Fact]
    public async Task ExplicitAffinity_NotOverridden()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);
        var cpuPool = aks.AddNodePool("cpu", "Standard_D4s_v5", 1, 10);

        var container = builder.AddContainer("myapi", "myimage")
            .WithNodePool(cpuPool);

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        // Explicit affinity should be preserved, not overridden
        Assert.True(container.Resource.TryGetLastAnnotation<KubernetesNodePoolAnnotation>(out var affinity));
        Assert.Equal("cpu", affinity.NodePool.Name);
    }

    [Fact]
    public async Task ComputeResource_GetsDeploymentTargetFromKubernetesInfrastructure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var container = builder.AddContainer("myapi", "myimage");

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        // DeploymentTargetAnnotation comes from KubernetesInfrastructure (via the inner
        // KubernetesEnvironmentResource), not from AzureKubernetesInfrastructure.
        Assert.True(container.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target));
        Assert.NotNull(target.DeploymentTarget);

        // The compute environment should be the inner K8s environment
        Assert.Same(aks.Resource.KubernetesEnvironment, target.ComputeEnvironment);

        // CRITICAL: ContainerRegistry must be set on the DeploymentTargetAnnotation
        // so that push steps can resolve the registry endpoint
        Assert.NotNull(target.ContainerRegistry);
        Assert.IsType<AzureContainerRegistryResource>(target.ContainerRegistry);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    [Fact]
    public async Task MultiEnv_ResourcesMatchCorrectEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var registry = builder.AddAzureContainerRegistry("registry");
        var enva = builder.AddAzureKubernetesEnvironment("enva")
            .WithContainerRegistry(registry);
        var envb = builder.AddAzureKubernetesEnvironment("envb")
            .WithContainerRegistry(registry);

        var cache = builder.AddContainer("cache", "redis")
            .WithComputeEnvironment(enva);
        var api = builder.AddContainer("api", "myapi")
            .WithComputeEnvironment(enva);
        var other = builder.AddContainer("other", "myother")
            .WithComputeEnvironment(envb);

        // OwningComputeEnvironment should be set
        Assert.Same(enva.Resource, enva.Resource.KubernetesEnvironment.OwningComputeEnvironment);
        Assert.Same(envb.Resource, envb.Resource.KubernetesEnvironment.OwningComputeEnvironment);

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        // cache and api should get DeploymentTargetAnnotation targeting enva
        Assert.True(cache.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var cacheTarget),
            "cache should have DeploymentTargetAnnotation");
        Assert.Same(enva.Resource, cacheTarget.ComputeEnvironment);

        Assert.True(api.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var apiTarget),
            "api should have DeploymentTargetAnnotation");
        Assert.Same(enva.Resource, apiTarget.ComputeEnvironment);

        // other should get DeploymentTargetAnnotation targeting envb
        Assert.True(other.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var otherTarget),
            "other should have DeploymentTargetAnnotation");
        Assert.Same(envb.Resource, otherTarget.ComputeEnvironment);
    }
}
