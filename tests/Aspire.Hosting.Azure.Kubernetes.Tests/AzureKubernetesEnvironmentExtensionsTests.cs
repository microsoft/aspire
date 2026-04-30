// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesEnvironmentExtensionsTests
{
    [Fact]
    public async Task AddAzureKubernetesEnvironment_BasicConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Equal("aks", aks.Resource.Name);
        Assert.Equal("{aks.outputs.id}", aks.Resource.Id.ValueExpression);
        Assert.Equal("{aks.outputs.clusterFqdn}", aks.Resource.ClusterFqdn.ValueExpression);
        Assert.Equal("{aks.outputs.oidcIssuerUrl}", aks.Resource.OidcIssuerUrl.ValueExpression);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddNodePool_ReturnsNodePoolResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);

        // Default system pool + added user pool
        Assert.Equal(2, aks.Resource.NodePools.Count);

        Assert.Equal("gpu", gpuPool.Resource.Name);
        Assert.Equal("gpu", gpuPool.Resource.Config.Name);
        Assert.Equal("Standard_NC6s_v3", gpuPool.Resource.Config.VmSize);
        Assert.Equal(0, gpuPool.Resource.Config.MinCount);
        Assert.Equal(5, gpuPool.Resource.Config.MaxCount);
        Assert.Equal(AksNodePoolMode.User, gpuPool.Resource.Config.Mode);
        Assert.Same(aks.Resource, gpuPool.Resource.AksParent);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_DefaultNodePool()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Single(aks.Resource.NodePools);
        var defaultPool = aks.Resource.NodePools[0];
        Assert.Equal("system", defaultPool.Name);
        Assert.Equal("Standard_D2s_v5", defaultPool.VmSize);
        Assert.Equal(1, defaultPool.MinCount);
        Assert.Equal(3, defaultPool.MaxCount);
        Assert.Equal(AksNodePoolMode.System, defaultPool.Mode);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_DefaultConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.True(aks.Resource.OidcIssuerEnabled);
        Assert.True(aks.Resource.WorkloadIdentityEnabled);
        Assert.Equal(AksSkuTier.Free, aks.Resource.SkuTier);
        Assert.Null(aks.Resource.KubernetesVersion);
        Assert.False(aks.Resource.IsPrivateCluster);
        Assert.False(aks.Resource.ContainerInsightsEnabled);
        Assert.Null(aks.Resource.LogAnalyticsWorkspace);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_HasInternalKubernetesEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.NotNull(aks.Resource.KubernetesEnvironment);
        Assert.Equal("aks", aks.Resource.KubernetesEnvironment.Name);
        Assert.True(aks.Resource.TryGetLastAnnotation<KubernetesEnvironmentAnnotation>(out var annotation));
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_AddsOnlyAksComputeEnvironmentToModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.DoesNotContain(model.Resources, r => r is KubernetesEnvironmentResource);

        var computeEnvironment = Assert.Single(model.Resources.OfType<IComputeEnvironmentResource>());
        Assert.Same(aks.Resource, computeEnvironment);
    }

    [Fact]
    public async Task AddAzureKubernetesEnvironment_AllowsKubernetesServiceCustomizationWithoutVisibleKubernetesEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        builder.AddAzureKubernetesEnvironment("aks");
        builder.AddContainer("api", "myimage")
            .PublishAsKubernetesService(_ => { });

        await using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_ThrowsOnNullBuilder()
    {
        IDistributedApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddAzureKubernetesEnvironment("aks"));
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_ThrowsOnEmptyName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        Assert.Throws<ArgumentException>(() =>
            builder.AddAzureKubernetesEnvironment(""));
    }

    [Fact]
    public void WithWorkloadIdentity_EnablesOidcAndWorkloadIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithWorkloadIdentity();

        Assert.True(aks.Resource.OidcIssuerEnabled);
        Assert.True(aks.Resource.WorkloadIdentityEnabled);
    }

    [Fact]
    public void WithAzureUserAssignedIdentity_WorksWithAks()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var identity = builder.AddAzureUserAssignedIdentity("myIdentity");

        var project = builder.AddContainer("myapi", "myimage")
            .WithAzureUserAssignedIdentity(identity);

        Assert.True(project.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentity));
        Assert.Same(identity.Resource, appIdentity.IdentityResource);
    }

    [Fact]
    public void AzureKubernetesEnvironment_ImplementsIAzureComputeEnvironmentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        Assert.IsAssignableFrom<IAzureComputeEnvironmentResource>(aks.Resource);
    }

    [Fact]
    public void AzureKubernetesEnvironment_ImplementsIAzureNspAssociationTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        Assert.IsAssignableFrom<IAzureNspAssociationTarget>(aks.Resource);
    }

    [Fact]
    public void AsExisting_WorksOnAksResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var nameParam = builder.AddParameter("aks-name");
        var rgParam = builder.AddParameter("aks-rg");

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .AsExisting(nameParam, rgParam);

        Assert.NotNull(aks);
    }

    [Fact]
    public void WithSubnet_OnNodePool_StoresPerPoolSubnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var defaultSubnet = vnet.AddSubnet("default-subnet", "10.0.0.0/22");
        var gpuSubnet = vnet.AddSubnet("gpu-subnet", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSubnet(defaultSubnet);

        var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5)
            .WithSubnet(gpuSubnet);

        // Environment-level subnet should be set via annotation
        Assert.True(aks.Resource.TryGetLastAnnotation<AksSubnetAnnotation>(out _));

        // Per-pool subnet should be stored in NodePoolSubnets dictionary
        Assert.Single(aks.Resource.NodePoolSubnets);
        Assert.True(aks.Resource.NodePoolSubnets.ContainsKey("gpu"));

        // Node pool should also have its own subnet annotation
        Assert.True(gpuPool.Resource.TryGetLastAnnotation<AksSubnetAnnotation>(out _));
    }

    [Fact]
    public void WithSubnet_OnNodePool_WithoutEnvironmentSubnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var gpuSubnet = vnet.AddSubnet("gpu-subnet", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // Only set subnet on the pool, not the environment
        var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5)
            .WithSubnet(gpuSubnet);

        // No environment-level subnet
        Assert.False(aks.Resource.TryGetLastAnnotation<AksSubnetAnnotation>(out _));

        // Per-pool subnet should still work
        Assert.Single(aks.Resource.NodePoolSubnets);
        Assert.True(aks.Resource.NodePoolSubnets.ContainsKey("gpu"));
    }

    [Fact]
    public void WithNodePool_AddsAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);

        var container = builder.AddContainer("myapi", "myimage")
            .WithNodePool(gpuPool);

        Assert.True(container.Resource.TryGetLastAnnotation<KubernetesNodePoolAnnotation>(out var affinity));
        Assert.Same(gpuPool.Resource, affinity.NodePool);
        Assert.Equal("gpu", affinity.NodePool.Name);
    }

    [Fact]
    public void AddNodePool_MultiplePoolsSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var pool1 = aks.AddNodePool("cpu", "Standard_D2s_v5", 1, 10);
        var pool2 = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);

        // Default system pool + 2 user pools
        Assert.Equal(3, aks.Resource.NodePools.Count);
        Assert.Equal("cpu", pool1.Resource.Name);
        Assert.Equal("gpu", pool2.Resource.Name);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_AutoCreatesDefaultRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.NotNull(aks.Resource.DefaultContainerRegistry);
        Assert.Equal("aks-acr", aks.Resource.DefaultContainerRegistry.Name);
    }

    [Fact]
    public void WithContainerRegistry_ReplacesDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var explicitAcr = builder.AddAzureContainerRegistry("my-acr");

        aks.WithContainerRegistry(explicitAcr);

        // Default registry should be removed
        Assert.Null(aks.Resource.DefaultContainerRegistry);

        // Explicit registry should be set via annotation
        Assert.True(aks.Resource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation));
        Assert.Same(explicitAcr.Resource, annotation.Registry);
    }

    [Fact]
    public async Task ContainerRegistry_FlowsToInnerKubernetesEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var container = builder.AddContainer("myapi", "myimage");

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.True(aks.Resource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var aksAnnotation));
        Assert.Same(aks.Resource.DefaultContainerRegistry, aksAnnotation.Registry);

        Assert.True(aks.Resource.KubernetesEnvironment
            .TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation));
        Assert.Same(aks.Resource.DefaultContainerRegistry, annotation.Registry);
    }

    [Fact]
    public void WithSystemNodePool_CustomVmSize()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool("Standard_B2s");

        Assert.Single(aks.Resource.NodePools, p => p.Mode is AksNodePoolMode.System);
        var systemPool = aks.Resource.NodePools.First(p => p.Mode is AksNodePoolMode.System);
        Assert.Equal("system", systemPool.Name);
        Assert.Equal("Standard_B2s", systemPool.VmSize);
        Assert.Equal(1, systemPool.MinCount);
        Assert.Equal(3, systemPool.MaxCount);
    }

    [Fact]
    public void WithSystemNodePool_CustomVmSizeAndScaling()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool("Standard_B4ms", minCount: 2, maxCount: 5);

        var systemPool = aks.Resource.NodePools.First(p => p.Mode is AksNodePoolMode.System);
        Assert.Equal("Standard_B4ms", systemPool.VmSize);
        Assert.Equal(2, systemPool.MinCount);
        Assert.Equal(5, systemPool.MaxCount);
    }

    [Fact]
    public void WithSystemNodePool_ReplacesDefaultSystemPool()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // Default system pool should exist
        Assert.Equal("Standard_D2s_v5", aks.Resource.NodePools[0].VmSize);

        // Replace it
        aks.WithSystemNodePool("Standard_B2s");

        // Should still be exactly one system pool
        Assert.Single(aks.Resource.NodePools, p => p.Mode is AksNodePoolMode.System);
        Assert.Equal("Standard_B2s", aks.Resource.NodePools.First(p => p.Mode is AksNodePoolMode.System).VmSize);
    }

    [Fact]
    public void WithSystemNodePool_CalledMultipleTimesUsesLastValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool("Standard_B2s")
            .WithSystemNodePool("Standard_D4s_v5", minCount: 3, maxCount: 10);

        Assert.Single(aks.Resource.NodePools, p => p.Mode is AksNodePoolMode.System);
        var systemPool = aks.Resource.NodePools.First(p => p.Mode is AksNodePoolMode.System);
        Assert.Equal("Standard_D4s_v5", systemPool.VmSize);
        Assert.Equal(3, systemPool.MinCount);
        Assert.Equal(10, systemPool.MaxCount);
    }

    [Fact]
    public void WithSystemNodePool_ChainsWithAddNodePool()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool("Standard_B2s");

        var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);

        // 1 system pool + 1 user pool
        Assert.Equal(2, aks.Resource.NodePools.Count);
        Assert.Equal("Standard_B2s", aks.Resource.NodePools.First(p => p.Mode is AksNodePoolMode.System).VmSize);
        Assert.Equal("gpu", gpuPool.Resource.Name);
    }

    [Fact]
    public void WithSystemNodePool_RejectsZeroMinCount()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        Assert.Throws<ArgumentOutOfRangeException>(() => aks.WithSystemNodePool("Standard_B2s", minCount: 0));
    }

    [Fact]
    public async Task WithSystemNodePool_BicepReflectsCustomVmSize()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool("Standard_B2s");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
