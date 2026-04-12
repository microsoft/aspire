// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Utils;

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
    public async Task AddAzureKubernetesEnvironment_WithVersion()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithVersion("1.30");

        Assert.Equal("1.30", aks.Resource.KubernetesVersion);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_WithSkuTier()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSkuTier(AksSkuTier.Standard);

        Assert.Equal(AksSkuTier.Standard, aks.Resource.SkuTier);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_WithNodePool()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithNodePool("gpu", "Standard_NC6s_v3", 0, 5);

        // Default system pool + added user pool
        Assert.Equal(2, aks.Resource.NodePools.Count);

        var userPool = aks.Resource.NodePools[1];
        Assert.Equal("gpu", userPool.Name);
        Assert.Equal("Standard_NC6s_v3", userPool.VmSize);
        Assert.Equal(0, userPool.MinCount);
        Assert.Equal(5, userPool.MaxCount);
        Assert.Equal(AksNodePoolMode.User, userPool.Mode);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_AsPrivateCluster()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .AsPrivateCluster();

        Assert.True(aks.Resource.IsPrivateCluster);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_WithContainerInsights()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithContainerInsights();

        Assert.True(aks.Resource.ContainerInsightsEnabled);
        Assert.Null(aks.Resource.LogAnalyticsWorkspace);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_WithContainerInsightsAndWorkspace()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var logAnalytics = builder.AddAzureLogAnalyticsWorkspace("law");
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithContainerInsights(logAnalytics);

        Assert.True(aks.Resource.ContainerInsightsEnabled);
        Assert.Same(logAnalytics.Resource, aks.Resource.LogAnalyticsWorkspace);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_WithAzureLogAnalyticsWorkspace()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var logAnalytics = builder.AddAzureLogAnalyticsWorkspace("law");
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithAzureLogAnalyticsWorkspace(logAnalytics);

        Assert.Same(logAnalytics.Resource, aks.Resource.LogAnalyticsWorkspace);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_DefaultNodePool()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Single(aks.Resource.NodePools);
        var defaultPool = aks.Resource.NodePools[0];
        Assert.Equal("system", defaultPool.Name);
        Assert.Equal("Standard_D4s_v5", defaultPool.VmSize);
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
        Assert.Equal("aks-k8s", aks.Resource.KubernetesEnvironment.Name);
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
    public void WithVersion_ThrowsOnEmptyVersion()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Throws<ArgumentException>(() =>
            aks.WithVersion(""));
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
    public void WithAzureWorkloadIdentity_AddsAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var identity = builder.AddAzureUserAssignedIdentity("myIdentity");

        var project = builder.AddContainer("myapi", "myimage")
            .WithAzureWorkloadIdentity(identity);

        Assert.True(project.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentity));
        Assert.Same(identity.Resource, appIdentity.IdentityResource);

        Assert.True(project.Resource.TryGetLastAnnotation<AksWorkloadIdentityAnnotation>(out var aksIdentity));
        Assert.Same(identity.Resource, aksIdentity.IdentityResource);
    }

    [Fact]
    public void WithAzureWorkloadIdentity_AutoCreatesIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var project = builder.AddContainer("myapi", "myimage")
            .WithAzureWorkloadIdentity();

        Assert.True(project.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out _));
        Assert.True(project.Resource.TryGetLastAnnotation<AksWorkloadIdentityAnnotation>(out var aksIdentity));
        Assert.NotNull(aksIdentity.IdentityResource);
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
}
