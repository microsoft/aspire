// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Provisioning.ContainerService;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesEnvironmentExtensionsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AddAzureKubernetesEnvironment_BasicConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Equal("aks", aks.Resource.Name);
        Assert.Equal("{aks.outputs.id}", aks.Resource.Id.ValueExpression);
        Assert.Equal("{aks.outputs.clusterFqdn}", aks.Resource.ClusterFqdn.ValueExpression);
        Assert.Equal("{aks.outputs.oidcIssuerUrl}", aks.Resource.OidcIssuerUrl.ValueExpression);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureKubernetesEnvironment_ConfigureInfrastructureCanSelectFreeTier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .ConfigureInfrastructure(infrastructure =>
            {
                var cluster = infrastructure.GetProvisionableResources()
                    .OfType<ContainerServiceManagedCluster>()
                    .Single();

                cluster.Sku.Tier = ManagedClusterSkuTier.Free;
            });

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddNodePool_ReturnsNodePoolResource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        Assert.Null(gpuPool.Resource.Config.OsSku);
        Assert.Same(aks.Resource, gpuPool.Resource.AksParent);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_DefaultNodePool()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Single(aks.Resource.NodePools);
        var defaultPool = aks.Resource.NodePools[0];
        Assert.Equal("system", defaultPool.Name);
        Assert.Equal("Standard_D2s_v5", defaultPool.VmSize);
        Assert.Equal(1, defaultPool.MinCount);
        Assert.Equal(3, defaultPool.MaxCount);
        Assert.Equal(AksNodePoolMode.System, defaultPool.Mode);
        Assert.Null(defaultPool.OsSku);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public void NodePoolCallbacks_ConfigureIndependentPools(DistributedApplicationOperation operation)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace, operation);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var result = aks.WithSystemNodePool(pool =>
        {
            pool.VmSize = "Standard_D4s_v5";
            pool.MinCount = 2;
            pool.MaxCount = 4;
            pool.OsSku = "AzureLinux";
        });
        var workers = aks.AddNodePool("workers", pool =>
        {
            pool.MinCount = 0;
            pool.MaxCount = 5;
            pool.OsSku = "Ubuntu";
        });
        var defaults = aks.AddNodePool("defaults", _ => { });

        Assert.Same(aks, result);
        Assert.Collection(aks.Resource.NodePools,
            pool => Assert.Equal(new AksNodePoolConfig("system", "Standard_D4s_v5", 2, 4, AksNodePoolMode.System, "AzureLinux"), pool),
            pool => Assert.Same(workers.Resource.Config, pool),
            pool => Assert.Same(defaults.Resource.Config, pool));
        Assert.Equal(new AksNodePoolConfig("workers", "Standard_D2s_v5", 0, 5, AksNodePoolMode.User, "Ubuntu"), workers.Resource.Config);
        Assert.Equal(new AksNodePoolConfig("defaults", "Standard_D2s_v5", 1, 3, AksNodePoolMode.User), defaults.Resource.Config);
    }

    [Fact]
    public async Task NodePoolCallbacks_ComposeConfigurationUntilBicepGeneration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        AksNodePoolConfig? systemConfig = null;
        AksNodePoolConfig? workerConfig = null;
        aks.WithSystemNodePool(pool =>
        {
            systemConfig = pool;
            pool.MinCount = -1;
            pool.OsSku = "AzureLinux";
        });
        var workers = aks.AddNodePool("workers", pool =>
        {
            workerConfig = pool;
            pool.MinCount = -1;
            pool.OsSku = "Ubuntu";
        });

        Assert.NotNull(systemConfig);
        Assert.NotNull(workerConfig);
        Assert.Same(systemConfig, aks.Resource.NodePools[0]);
        Assert.Same(workerConfig, workers.Resource.Config);
        Assert.NotSame(systemConfig, workerConfig);
        systemConfig.MinCount = 1;
        systemConfig.OsSku = "Ubuntu";
        workers.Resource.Config.OsSku = "AzureLinux";
        workers.Resource.Config.VmSize = "Standard_D4s_v5";
        workers.Resource.Config.MinCount = 2;
        workers.Resource.Config.MaxCount = 4;

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);

        systemConfig.OsSku = "AzureLinux";
        workerConfig.OsSku = "Ubuntu";
        workerConfig.MinCount = 0;
        var regenerated = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);

        await Verify(new
        {
            Initial = manifest.BicepText,
            Regenerated = regenerated.BicepText
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithSystemNodePool_SubsequentCallResetsOptions(bool useCallback)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool(pool => pool.OsSku = "AzureLinux");
        var workers = aks.AddNodePool("workers", pool => pool.OsSku = "Ubuntu");

        if (useCallback)
        {
            aks.WithSystemNodePool(_ => { });
        }
        else
        {
            aks.WithSystemNodePool();
        }

        Assert.Collection(aks.Resource.NodePools,
            pool => Assert.Equal(new AksNodePoolConfig("system", "Standard_D2s_v5", 1, 3, AksNodePoolMode.System), pool),
            pool => Assert.Same(workers.Resource.Config, pool));
    }

    [Theory]
    [InlineData(null, 1, 3, null)]
    [InlineData("", 1, 3, null)]
    [InlineData(" ", 1, 3, null)]
    [InlineData("Standard_D2s_v5", -1, 3, null)]
    [InlineData("Standard_D2s_v5", 1, -1, null)]
    [InlineData("Standard_D2s_v5", 4, 3, null)]
    [InlineData("Standard_D2s_v5", 1, 3, "")]
    [InlineData("Standard_D2s_v5", 1, 3, " ")]
    public async Task NodePoolConfiguration_ValidatesFinalValuesDuringBicepGeneration(string? vmSize, int minCount, int maxCount, string? osSku)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        void Configure(AksNodePoolConfig pool)
        {
            pool.VmSize = vmSize!;
            pool.MinCount = minCount;
            pool.MaxCount = maxCount;
            pool.OsSku = osSku;
        }

        aks.WithSystemNodePool(_ => { });
        var workers = aks.AddNodePool("workers", _ => { });
        Configure(aks.Resource.NodePools[0]);
        Configure(workers.Resource.Config);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => AzureManifestUtils.GetManifestWithBicep(aks.Resource));

        aks.WithSystemNodePool();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AzureManifestUtils.GetManifestWithBicep(aks.Resource));
    }

    [Fact]
    public void NodePoolCallbacks_RejectNullArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var original = Assert.Single(aks.Resource.NodePools);
        IResourceBuilder<AzureKubernetesEnvironmentResource> nullBuilder = null!;

        Assert.Throws<ArgumentNullException>(() => aks.WithSystemNodePool((Action<AksNodePoolConfig>)null!));
        Assert.Throws<ArgumentNullException>(() => aks.AddNodePool("workers", (Action<AksNodePoolConfig>)null!));
        Assert.Throws<ArgumentNullException>(() => nullBuilder.WithSystemNodePool(_ => { }));
        Assert.Throws<ArgumentNullException>(() => nullBuilder.AddNodePool("workers", _ => { }));
        Assert.Throws<ArgumentException>(() => aks.AddNodePool("", _ => { }));
        Assert.Same(original, Assert.Single(aks.Resource.NodePools));
    }

    [Fact]
    public async Task WithSystemNodePool_RejectsZeroCountsDuringBicepGeneration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        aks.WithSystemNodePool(pool => pool.MinCount = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AzureManifestUtils.GetManifestWithBicep(aks.Resource));

        aks.WithSystemNodePool(pool => pool.MaxCount = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AzureManifestUtils.GetManifestWithBicep(aks.Resource));
    }

    [Fact]
    public void NodePoolCallbacks_PropagateExceptionsWithoutMutatingModel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var original = Assert.Single(aks.Resource.NodePools);
        var error = new InvalidOperationException("Callback failed.");

        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => aks.WithSystemNodePool(_ => throw error)));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => aks.AddNodePool("workers", _ => throw error)));
        Assert.Same(original, Assert.Single(aks.Resource.NodePools));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("AzureLinux", "AzureLinux")]
    [InlineData("AzureLinux", "Ubuntu")]
    [InlineData(null, "FutureLinuxSku")]
    public async Task NodePoolCallbacks_BicepReflectsOsSku(string? systemOsSku, string? workerOsSku)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool(pool => pool.OsSku = systemOsSku);
        aks.AddNodePool("workers", pool => pool.OsSku = workerOsSku);
        aks.AddNodePool("defaults");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        var regenerated = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        Assert.Equal(manifest.BicepText, regenerated.BicepText);
        await Verify(manifest.BicepText, extension: "bicep")
            .UseParameters(systemOsSku, workerOsSku);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_DefaultConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.True(aks.Resource.OidcIssuerEnabled);
        Assert.True(aks.Resource.WorkloadIdentityEnabled);
        Assert.Null(aks.Resource.KubernetesVersion);
        Assert.False(aks.Resource.IsPrivateCluster);
        Assert.False(aks.Resource.ContainerInsightsEnabled);
        Assert.Null(aks.Resource.LogAnalyticsWorkspace);
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_HasInternalKubernetesEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.NotNull(aks.Resource.KubernetesEnvironment);
        Assert.Equal("aks", aks.Resource.KubernetesEnvironment.Name);
        Assert.True(aks.Resource.TryGetLastAnnotation<KubernetesEnvironmentAnnotation>(out var annotation));
    }

    [Fact]
    public void AddAzureKubernetesEnvironment_AddsOnlyAksComputeEnvironmentToModel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        Assert.Throws<ArgumentException>(() =>
            builder.AddAzureKubernetesEnvironment(""));
    }

    [Fact]
    public void WithWorkloadIdentity_EnablesOidcAndWorkloadIdentity()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithWorkloadIdentity();

        Assert.True(aks.Resource.OidcIssuerEnabled);
        Assert.True(aks.Resource.WorkloadIdentityEnabled);
    }

    [Fact]
    public void WithAzureUserAssignedIdentity_WorksWithAks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        Assert.IsAssignableFrom<IAzureComputeEnvironmentResource>(aks.Resource);
    }

    [Fact]
    public void AzureKubernetesEnvironment_ImplementsIAzureNspAssociationTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        Assert.IsAssignableFrom<IAzureNspAssociationTarget>(aks.Resource);
    }

    [Fact]
    public void AsExisting_WorksOnAksResource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var nameParam = builder.AddParameter("aks-name");
        var rgParam = builder.AddParameter("aks-rg");

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .AsExisting(nameParam, rgParam);

        Assert.NotNull(aks);
    }

    [Fact]
    public void WithSubnet_OnNodePool_StoresPerPoolSubnet()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.NotNull(aks.Resource.DefaultContainerRegistry);
        Assert.Equal("aks-acr", aks.Resource.DefaultContainerRegistry.Name);
    }

    [Fact]
    public void WithContainerRegistry_ReplacesDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithContainerRegistry_CrossResourceGroupPublishesScopedKubeletRole(bool parameterizedScope)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var registry = builder.AddAzureContainerRegistry("registry");
        if (parameterizedScope)
        {
            registry.PublishAsExistingInResourceGroup(
                builder.AddParameter("registry-name", "existingacr"),
                builder.AddParameter("registry-group", "registry-rg"),
                builder.AddParameter("registry-subscription", "00000000-0000-0000-0000-000000000001"));
        }
        else
        {
            registry.PublishAsExisting("existingacr", "registry-rg");
        }

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithContainerRegistry(registry);
        builder.AddContainer("api", "myimage").WithHttpEndpoint(targetPort: 8080);

        await using var app = builder.Build();
        await app.RunAsync();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var roles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "aks-kubelet-identity-roles-registry");
        Assert.Same(registry.Resource, roles.TargetAzureResource);
        Assert.Same(aks.Resource.KubeletIdentity, roles.IdentityResource);
        Assert.Equal(aks.Resource.KubeletIdentity!.PrincipalId.ValueExpression,
            Assert.IsType<BicepOutputReference>(roles.Parameters["principalId"]).ValueExpression);
        Assert.NotNull(roles.Scope);
        Assert.False(aks.Resource.Parameters.ContainsKey("acrName"));
        Assert.Equal("{aks.outputs.kubeletIdentityObjectId}", aks.Resource.KubeletIdentityObjectId.ValueExpression);

        var mainBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep"));
        var aksBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks", "aks.bicep"));
        var roleBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, roles.Name, $"{roles.Name}.bicep"));
        var registryBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "registry", "registry.bicep"));
        var controlPlaneBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-control-plane-identity", "aks-control-plane-identity.bicep"));
        var kubeletBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-kubelet-identity", "aks-kubelet-identity.bicep"));
        var operatorBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-control-plane-identity-roles-aks-kubelet-identity", "aks-control-plane-identity-roles-aks-kubelet-identity.bicep"));
        await Verify(mainBicep, "bicep")
            .UseParameters(parameterizedScope)
            .AppendContentAsFile(aksBicep, "bicep")
            .AppendContentAsFile(roleBicep, "bicep")
            .AppendContentAsFile(registryBicep, "bicep")
            .AppendContentAsFile(controlPlaneBicep, "bicep")
            .AppendContentAsFile(kubeletBicep, "bicep")
            .AppendContentAsFile(operatorBicep, "bicep");
    }

    [Fact]
    public async Task WithContainerRegistry_CrossResourceGroupUsesFinalRegistryAndPreparesOnce()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var first = builder.AddAzureContainerRegistry("first").PublishAsExisting("firstacr", "first-rg");
        var final = builder.AddAzureContainerRegistry("final");
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithContainerRegistry(first)
            .WithContainerRegistry(final);
        final.PublishAsExisting("finalacr", "final-rg");

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var provisioningRoles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "aks-kubelet-identity-roles-final");
        Assert.Equal("final-rg", provisioningRoles.Scope!.ResourceGroup);
        Assert.Same(final.Resource, provisioningRoles.TargetAzureResource);
        Assert.Same(aks.Resource.KubeletIdentity, provisioningRoles.IdentityResource);
        Assert.Collection(model.Resources.OfType<AzureUserAssignedIdentityResource>(),
            identity => Assert.Same(aks.Resource.ControlPlaneIdentity, identity),
            identity => Assert.Same(aks.Resource.KubeletIdentity, identity));
    }

    [Fact]
    public async Task WithContainerRegistry_SharedCrossGroupRegistryGrantsEachKubeletRole()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var registry = builder.AddAzureContainerRegistry("registry").PublishAsExisting("existingacr", "registry-rg");
        var first = builder.AddAzureKubernetesEnvironment("first").WithContainerRegistry(registry);
        var second = builder.AddAzureKubernetesEnvironment("second").WithContainerRegistry(registry);

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var roles = app.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<AzureRoleAssignmentResource>().Where(resource => resource.TargetAzureResource == registry.Resource);
        Assert.Collection(roles,
            role => Assert.Same(first.Resource.KubeletIdentity, role.IdentityResource),
            role => Assert.Same(second.Resource.KubeletIdentity, role.IdentityResource));
    }

    [Fact]
    public async Task WithContainerRegistry_UnscopedExistingRegistryUsesKubeletIdentity()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var registry = builder.AddAzureContainerRegistry("registry").PublishAsExisting("existingacr", resourceGroup: null);
        var aks = builder.AddAzureKubernetesEnvironment("aks").WithContainerRegistry(registry);

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roles = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<AzureRoleAssignmentResource>(), resource => resource.TargetAzureResource == registry.Resource);
        Assert.Same(aks.Resource.KubeletIdentity, roles.IdentityResource);
        Assert.Null(roles.Scope);
        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, "bicep");
    }

    [Fact]
    public async Task WithContainerRegistry_RunModeDoesNotProvisionClusterIdentities()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper, workspace, operation: DistributedApplicationOperation.Run);
        var registry = builder.AddAzureContainerRegistry("registry").RunAsExisting("existingacr", "registry-rg");
        var aks = builder.AddAzureKubernetesEnvironment("aks").WithContainerRegistry(registry);

        Assert.Null(aks.Resource.ControlPlaneIdentity);
        Assert.Null(aks.Resource.KubeletIdentity);
        Assert.Empty(builder.Resources.OfType<AzureUserAssignedIdentityResource>());
        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, "bicep");
    }

    [Fact]
    public async Task DefaultRegistry_GrantsPullOnlyToKubeletAndOperatorOnlyToControlPlane()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roles = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<AzureRoleAssignmentResource>();
        Assert.Collection(roles,
            role =>
            {
                Assert.Same(aks.Resource.ControlPlaneIdentity, role.IdentityResource);
                Assert.Same(aks.Resource.KubeletIdentity, role.TargetAzureResource);
                Assert.Contains(role, aks.Resource.References);
            },
            role =>
            {
                Assert.Same(aks.Resource.KubeletIdentity, role.IdentityResource);
                Assert.Same(aks.Resource.DefaultContainerRegistry, role.TargetAzureResource);
                Assert.Contains(role, aks.Resource.References);
            });
        Assert.Collection(aks.Resource.KubeletIdentity!.Annotations.OfType<RoleAssignmentAnnotation>(),
            annotation => Assert.Collection(annotation.Roles,
                role => Assert.Equal(new RoleDefinition("7f951dda-4ed3-4680-a7ca-43fe172d538d", "AcrPull"), role)));
        Assert.Collection(aks.Resource.ControlPlaneIdentity!.Annotations.OfType<RoleAssignmentAnnotation>(),
            annotation => Assert.Collection(annotation.Roles,
                role => Assert.Equal(new RoleDefinition("f1a07417-d97a-45cb-824c-7a7467783830", "ManagedIdentityOperator"), role)));
    }

    [Fact]
    public async Task ExcludedEnvironment_DoesNotPrepareIdentities()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        builder.AddAzureKubernetesEnvironment("aks").WithAnnotation(ManifestPublishingCallbackAnnotation.Ignore);

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Empty(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        Assert.Empty(model.Resources.OfType<AzureRoleAssignmentResource>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClusterScope_IsInheritedByIdentitiesAndOperatorRole(bool configuredScope)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var scope = configuredScope
            ? new AzureBicepResourceScope(
                builder.AddParameter("cluster-group", "cluster-rg").Resource,
                builder.AddParameter("cluster-subscription", "00000000-0000-0000-0000-000000000001").Resource)
            : new AzureBicepResourceScope("cluster-rg", "00000000-0000-0000-0000-000000000001");
        if (configuredScope)
        {
            aks.ConfigureInfrastructure(infrastructure => infrastructure.AspireResource.Scope = scope);
        }
        else
        {
            aks.Resource.Scope = scope;
        }

        await using var app = builder.Build();
        await app.RunAsync();

        Assert.Same(scope, aks.Resource.ControlPlaneIdentity!.Scope);
        Assert.Same(scope, aks.Resource.KubeletIdentity!.Scope);
        var operatorRole = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<AzureRoleAssignmentResource>(),
            role => role.TargetAzureResource == aks.Resource.KubeletIdentity);
        Assert.Same(scope, operatorRole.Scope);
        var registryRole = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<AzureRoleAssignmentResource>(),
            role => role.TargetAzureResource == aks.Resource.DefaultContainerRegistry);
        Assert.Null(registryRole.Scope);

        var mainBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep"));
        var aksBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks", "aks.bicep"));
        var operatorBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, operatorRole.Name, $"{operatorRole.Name}.bicep"));
        var registryBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-acr", "aks-acr.bicep"));
        var controlPlaneBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-control-plane-identity", "aks-control-plane-identity.bicep"));
        var kubeletBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-kubelet-identity", "aks-kubelet-identity.bicep"));
        var registryRoleBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, registryRole.Name, $"{registryRole.Name}.bicep"));
        await Verify(mainBicep, "bicep")
            .UseParameters(configuredScope)
            .AppendContentAsFile(aksBicep, "bicep")
            .AppendContentAsFile(operatorBicep, "bicep")
            .AppendContentAsFile(registryBicep, "bicep")
            .AppendContentAsFile(controlPlaneBicep, "bicep")
            .AppendContentAsFile(kubeletBicep, "bicep")
            .AppendContentAsFile(registryRoleBicep, "bicep");
    }

    [Fact]
    public async Task IdentityNameCollisionFailsBeforeRegisteringEitherIdentity()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        builder.AddParameter("aks-kubelet-identity");
        builder.AddAzureKubernetesEnvironment("aks");

        await using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("aks-kubelet-identity", exception.Message);
        Assert.Empty(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<AzureUserAssignedIdentityResource>());
    }

    [Fact]
    public async Task WithContainerRegistry_ExplicitScopeOverridesExistingAnnotationScope()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var registry = builder.AddAzureContainerRegistry("registry").PublishAsExisting("existingacr", "annotation-rg");
        registry.Resource.Scope = new AzureBicepResourceScope("explicit-rg", "00000000-0000-0000-0000-000000000001");
        builder.AddAzureKubernetesEnvironment("aks").WithContainerRegistry(registry);

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roles = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<AzureRoleAssignmentResource>(), resource => resource.TargetAzureResource == registry.Resource);
        Assert.Same(registry.Resource.Scope, roles.Scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryAndSubnetCallbacks_ApplyFinalScopeToRoleModules(bool parameterizedScope)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var registry = builder.AddAzureContainerRegistry("registry").PublishAsExisting("existingacr", "annotation-registry-rg");
        var network = builder.AddAzureVirtualNetwork("network", "10.0.0.0/16")
            .PublishAsExisting("existingvnet", "annotation-network-rg");
        var registryScope = parameterizedScope
            ? new AzureBicepResourceScope(
                builder.AddParameter("registry-group", "registry-rg").Resource,
                builder.AddParameter("registry-subscription", "00000000-0000-0000-0000-000000000001").Resource)
            : new AzureBicepResourceScope("registry-rg", "00000000-0000-0000-0000-000000000001");
        var networkScope = parameterizedScope
            ? new AzureBicepResourceScope(
                builder.AddParameter("network-group", "network-rg").Resource,
                builder.AddParameter("network-subscription", "00000000-0000-0000-0000-000000000002").Resource)
            : new AzureBicepResourceScope("network-rg", "00000000-0000-0000-0000-000000000002");
        registry.ConfigureInfrastructure(infrastructure => infrastructure.AspireResource.Scope = registryScope);
        network.ConfigureInfrastructure(infrastructure => infrastructure.AspireResource.Scope = networkScope);
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithContainerRegistry(registry)
            .WithSubnet(network.AddSubnet("subnet", "10.0.0.0/22"));

        await using var app = builder.Build();
        await app.RunAsync();

        var roles = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<AzureRoleAssignmentResource>();
        var registryRole = Assert.Single(roles, role => role.TargetAzureResource == registry.Resource);
        var networkRole = Assert.Single(roles, role => role.TargetAzureResource == network.Resource);
        Assert.Same(registryScope, registry.Resource.Scope);
        Assert.Same(registryScope, registryRole.Scope);
        Assert.Same(aks.Resource.KubeletIdentity, registryRole.IdentityResource);
        Assert.Same(networkScope, network.Resource.Scope);
        Assert.Same(networkScope, networkRole.Scope);
        Assert.Same(aks.Resource.ControlPlaneIdentity, networkRole.IdentityResource);
        Assert.Contains(registryRole, aks.Resource.References);
        Assert.Contains(networkRole, aks.Resource.References);

        var mainBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep"));
        var registryBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "registry", "registry.bicep"));
        var registryRoleBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, registryRole.Name, $"{registryRole.Name}.bicep"));
        var networkBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "network", "network.bicep"));
        var networkRoleBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, networkRole.Name, $"{networkRole.Name}.bicep"));
        var aksBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks", "aks.bicep"));
        var controlPlaneBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-control-plane-identity", "aks-control-plane-identity.bicep"));
        var kubeletBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aks-kubelet-identity", "aks-kubelet-identity.bicep"));
        var operatorRoleName = "aks-control-plane-identity-roles-aks-kubelet-identity";
        var operatorBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, operatorRoleName, $"{operatorRoleName}.bicep"));
        await Verify(mainBicep, "bicep")
            .UseParameters(parameterizedScope)
            .AppendContentAsFile(registryBicep, "bicep")
            .AppendContentAsFile(registryRoleBicep, "bicep")
            .AppendContentAsFile(networkBicep, "bicep")
            .AppendContentAsFile(networkRoleBicep, "bicep")
            .AppendContentAsFile(aksBicep, "bicep")
            .AppendContentAsFile(controlPlaneBicep, "bicep")
            .AppendContentAsFile(kubeletBicep, "bicep")
            .AppendContentAsFile(operatorBicep, "bicep");
    }

    [Fact]
    public async Task WithSubnet_GrantsControlPlaneNetworkAccessForFinalEnvironmentAndPoolSubnets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);
        var firstNetwork = builder.AddAzureVirtualNetwork("first-network", "10.0.0.0/16");
        var finalNetwork = builder.AddAzureVirtualNetwork("final-network", "10.1.0.0/16");
        var poolNetwork = builder.AddAzureVirtualNetwork("pool-network", "10.2.0.0/16")
            .PublishAsExisting("pool-vnet", "network-rg");
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSubnet(firstNetwork.AddSubnet("first-subnet", "10.0.0.0/22"))
            .WithSubnet(finalNetwork.AddSubnet("final-subnet", "10.1.0.0/22"));
        aks.AddNodePool("workload", "Standard_D2s_v5")
            .WithSubnet(poolNetwork.AddSubnet("pool-subnet", "10.2.0.0/22"));
        aks.AddNodePool("second", "Standard_D2s_v5")
            .WithSubnet(poolNetwork.AddSubnet("second-subnet", "10.2.4.0/22"));

        await using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roles = app.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<AzureRoleAssignmentResource>().Where(role => role.TargetAzureResource is AzureVirtualNetworkResource);
        Assert.Collection(roles,
            role =>
            {
                Assert.Same(aks.Resource.ControlPlaneIdentity, role.IdentityResource);
                Assert.Same(poolNetwork.Resource, role.TargetAzureResource);
                Assert.Equal("network-rg", role.Scope!.ResourceGroup);
                Assert.Contains(role, aks.Resource.References);
            },
            role =>
            {
                Assert.Same(aks.Resource.ControlPlaneIdentity, role.IdentityResource);
                Assert.Same(finalNetwork.Resource, role.TargetAzureResource);
                Assert.Null(role.Scope);
                Assert.Contains(role, aks.Resource.References);
            });
        foreach (var annotation in aks.Resource.ControlPlaneIdentity!.Annotations.OfType<RoleAssignmentAnnotation>()
            .Where(annotation => annotation.Target is AzureVirtualNetworkResource))
        {
            Assert.Collection(annotation.Roles,
                role => Assert.Equal(new RoleDefinition("4d97b98b-1d4f-4787-a291-c67834d212e7", "NetworkContributor"), role));
        }
    }

    [Fact]
    public void WithSystemNodePool_CustomVmSize()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        Assert.Throws<ArgumentOutOfRangeException>(() => aks.WithSystemNodePool("Standard_B2s", minCount: 0));
    }

    [Fact]
    public async Task WithSystemNodePool_BicepReflectsCustomVmSize()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(
            outputHelper,
            workspace,
            operation: DistributedApplicationOperation.Run);

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSystemNodePool("Standard_B2s");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RequiresPreviewIngressApi_DependsOnApplicationLoadBalancer(bool gatewayApiEnabled, bool applicationLoadBalancerEnabled)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        aks.Resource.GatewayApiEnabled = gatewayApiEnabled;
        aks.Resource.ApplicationLoadBalancerEnabled = applicationLoadBalancerEnabled;

        Assert.Equal(applicationLoadBalancerEnabled, aks.Resource.RequiresPreviewIngressApi);
    }

    [Fact]
    public async Task GatewayApiEnabled_BicepUsesStableApi()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        aks.Resource.GatewayApiEnabled = true;

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        Assert.Contains("resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' = {", manifest.BicepText);
        Assert.Contains("installation: 'Standard'", manifest.BicepText);
    }

    [Fact]
    public async Task AddLoadBalancer_BicepEnablesIngressProfileAndUsesPreviewApi()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var aksSubnet = vnet.AddSubnet("aksnodes", "10.0.0.0/22");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks").WithSubnet(aksSubnet);
        aks.AddLoadBalancer("lb", albSubnet);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddLoadBalancer_AppliesSubnetDelegation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        aks.AddLoadBalancer("lb", albSubnet);

        Assert.True(aks.Resource.GatewayApiEnabled);
        Assert.True(aks.Resource.ApplicationLoadBalancerEnabled);
        Assert.True(aks.Resource.RequiresPreviewIngressApi);

        // AGC requires the subnet be delegated to Microsoft.ServiceNetworking/trafficControllers.
        Assert.True(albSubnet.Resource.TryGetLastAnnotation<AzureSubnetServiceDelegationAnnotation>(out var delegation));
        Assert.Equal("Microsoft.ServiceNetworking/trafficControllers", delegation!.ServiceName);
    }

    [Fact]
    public void AddLoadBalancer_RegistersPerLBPipelineStep()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb = aks.AddLoadBalancer("lb", albSubnet);

        Assert.Same(aks.Resource, lb.Resource.Parent);
        Assert.Equal("alb-lb", lb.Resource.AlbName);
        Assert.True(lb.Resource.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var stepAnnotations));
        Assert.Single(stepAnnotations);
    }

    [Fact]
    public void AddLoadBalancer_MultipleLBs_AllStepsRegistered()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var alb1 = vnet.AddSubnet("alb1", "10.0.4.0/24");
        var alb2 = vnet.AddSubnet("alb2", "10.0.5.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb1 = aks.AddLoadBalancer("lb1", alb1);
        var lb2 = aks.AddLoadBalancer("lb2", alb2);

        Assert.NotSame(lb1.Resource, lb2.Resource);
        Assert.Equal("alb-lb1", lb1.Resource.AlbName);
        Assert.Equal("alb-lb2", lb2.Resource.AlbName);

        // Each LB owns its own pipeline step annotation.
        Assert.True(lb1.Resource.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var lb1Steps));
        Assert.True(lb2.Resource.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var lb2Steps));
        Assert.Single(lb1Steps);
        Assert.Single(lb2Steps);

        // Both LBs share the subnet/delegation requirements but the annotation
        // is applied idempotently per subnet (one delegation per subnet).
        Assert.True(alb1.Resource.TryGetLastAnnotation<AzureSubnetServiceDelegationAnnotation>(out _));
        Assert.True(alb2.Resource.TryGetLastAnnotation<AzureSubnetServiceDelegationAnnotation>(out _));
    }

    [Fact]
    public void AddLoadBalancer_OnUserDelegatedSubnet_ReplacesDelegationAndRecordsDisplaced()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");

        // The user delegates their ALB subnet to a different service before handing it to AGC.
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24")
            .WithServiceDelegation("Microsoft.Netapp/volumes");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb = aks.AddLoadBalancer("lb", albSubnet);

        // AGC requires trafficControllers, so the user's delegation is replaced rather than appended
        // and the subnet retains exactly one effective delegation.
        var delegation = Assert.Single(albSubnet.Resource.Annotations.OfType<AzureSubnetServiceDelegationAnnotation>());
        Assert.Equal("Microsoft.ServiceNetworking/trafficControllers", delegation.ServiceName);

        // The displaced delegation is surfaced so the LB pipeline step can warn the user at deploy time.
        Assert.Equal("Microsoft.Netapp/volumes", lb.Resource.DisplacedDelegationServiceName);
    }

    [Fact]
    public void AddLoadBalancer_SharedSubnet_KeepsSingleDelegationWithNoDisplacement()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb1 = aks.AddLoadBalancer("lb1", albSubnet);
        var lb2 = aks.AddLoadBalancer("lb2", albSubnet);

        // Sharing a subnet across load balancers is idempotent: exactly one delegation remains.
        var delegation = Assert.Single(albSubnet.Resource.Annotations.OfType<AzureSubnetServiceDelegationAnnotation>());
        Assert.Equal("Microsoft.ServiceNetworking/trafficControllers", delegation.ServiceName);

        // Neither LB displaced a user delegation; the only prior delegation was AGC's own.
        Assert.Null(lb1.Resource.DisplacedDelegationServiceName);
        Assert.Null(lb2.Resource.DisplacedDelegationServiceName);
    }

    [Fact]
    public void AddLoadBalancer_EquivalentDelegationWithDifferentCasing_DoesNotRecordDisplacement()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24")
            .WithServiceDelegation("microsoft.servicenetworking/trafficcontrollers");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb = aks.AddLoadBalancer("lb", albSubnet);

        var delegation = Assert.Single(albSubnet.Resource.Annotations.OfType<AzureSubnetServiceDelegationAnnotation>());
        Assert.Equal("Microsoft.ServiceNetworking/trafficControllers", delegation.ServiceName);
        Assert.Null(lb.Resource.DisplacedDelegationServiceName);
    }

    [Fact]
    public void AddLoadBalancer_OnSubnetWithMultipleDirectDelegations_CollapsesToSingle()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = AzureKubernetesTestBuilder.Create(outputHelper, workspace);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");

        // AzureSubnetServiceDelegationAnnotation is public, so a subnet can carry several directly-appended
        // delegations. AddLoadBalancer must read the last one (last-write-wins) without throwing on the
        // duplicates and then collapse them to AGC's single required delegation.
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24")
            .WithAnnotation(new AzureSubnetServiceDelegationAnnotation("First", "Microsoft.Netapp/volumes"))
            .WithAnnotation(new AzureSubnetServiceDelegationAnnotation("Second", "Microsoft.Sql/managedInstances"));

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb = aks.AddLoadBalancer("lb", albSubnet);

        var delegation = Assert.Single(albSubnet.Resource.Annotations.OfType<AzureSubnetServiceDelegationAnnotation>());
        Assert.Equal("Microsoft.ServiceNetworking/trafficControllers", delegation.ServiceName);

        // The last of the user's delegations is surfaced as displaced so the LB pipeline step can warn.
        Assert.Equal("Microsoft.Sql/managedInstances", lb.Resource.DisplacedDelegationServiceName);
    }

}
