// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesInfrastructureTests(ITestOutputHelper output)
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

        Assert.True(container.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target));
        Assert.NotNull(target.DeploymentTarget);

        // The compute environment should be the Azure K8s environment
        Assert.Same(aks.Resource, target.ComputeEnvironment);

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
        Assert.True(enva.Resource.TryGetLastAnnotation<KubernetesEnvironmentAnnotation>(out var _));
        Assert.True(envb.Resource.TryGetLastAnnotation<KubernetesEnvironmentAnnotation>(out var _));

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

    [Fact]
    public async Task KubernetesPipelineStepsFlowThroughAksEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            workspace.Path,
            step: WellKnownPipelineSteps.Diagnostics);

        var reporter = new TestPipelineActivityReporter(output);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        builder.AddAzureKubernetesEnvironment("aks");
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080);

        await using var app = builder.Build();
        await app.RunAsync();

        var logs = reporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .ToList();

        Assert.Contains(logs, msg => msg.Contains("publish-aks"));
        Assert.Contains(logs, msg => msg.Contains("prepare-aks"));
        Assert.Contains(logs, msg => msg.Contains("helm-deploy-aks"));
        Assert.Contains(logs, msg => msg.Contains("aks-get-credentials-aks"));
        Assert.DoesNotContain(logs, msg => msg.Contains("aks-k8s"));
    }

    [Fact]
    public async Task AzureDeploymentContextUsesCurrentDeploymentState()
    {
        const string subscriptionId = "00000000-0000-0000-0000-000000000001";
        const string resourceGroup = "deployment-rg";
        var deploymentStateManager = new InMemoryDeploymentStateManager();
        deploymentStateManager.SetSection("Azure", new JsonObject
        {
            ["SubscriptionId"] = subscriptionId,
            ["ResourceGroup"] = resourceGroup
        });

        using var services = new ServiceCollection()
            .AddSingleton<IDeploymentStateManager>(deploymentStateManager)
            .BuildServiceProvider();

        var deploymentContext = await AzureKubernetesEnvironmentResource.GetAzureDeploymentContextAsync(
            services,
            TestContext.Current.CancellationToken);

        Assert.Equal(subscriptionId, deploymentContext.SubscriptionId);
        Assert.Equal(resourceGroup, deploymentContext.ResourceGroup);
    }

    [Fact]
    public async Task AzureDeploymentContextRequiresSubscription()
    {
        var deploymentStateManager = new InMemoryDeploymentStateManager();
        deploymentStateManager.SetSection("Azure", new JsonObject
        {
            ["ResourceGroup"] = "deployment-rg"
        });

        using var services = new ServiceCollection()
            .AddSingleton<IDeploymentStateManager>(deploymentStateManager)
            .BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureKubernetesEnvironmentResource.GetAzureDeploymentContextAsync(
                services,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "Could not resolve the Azure subscription selected for deployment. Ensure Azure provisioning has completed, or set the Azure:SubscriptionId configuration value.",
            exception.Message);
    }

    [Fact]
    public async Task GetResourceGroupUsesDeploymentStateWithoutQueryingAzure()
    {
        var invocations = new List<string>();

        var resourceGroup = await AzureKubernetesEnvironmentResource.GetResourceGroupAsync(
            "/usr/bin/az",
            "deployment-aks",
            "00000000-0000-0000-0000-000000000001",
            "deployment-rg",
            NullLogger.Instance,
            (path, arguments) =>
            {
                invocations.Add(arguments);
                return Task.FromResult(new AzureKubernetesEnvironmentResource.AzCommandResult(0, "unexpected-rg", ""));
            });

        Assert.Equal("deployment-rg", resourceGroup);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task GetResourceGroupQueryIsScopedToDeploymentSubscription()
    {
        const string subscriptionId = "00000000-0000-0000-0000-000000000001";
        var invocations = new List<string>();

        var resourceGroup = await AzureKubernetesEnvironmentResource.GetResourceGroupAsync(
            "/usr/bin/az",
            "deployment-aks",
            subscriptionId,
            savedResourceGroup: null,
            NullLogger.Instance,
            (path, arguments) =>
            {
                invocations.Add(arguments);
                return Task.FromResult(new AzureKubernetesEnvironmentResource.AzCommandResult(0, "queried-rg\n", ""));
            });

        Assert.Equal("queried-rg", resourceGroup);
        Assert.Equal(
            [$"resource list --resource-type Microsoft.ContainerService/managedClusters --name \"deployment-aks\" --query [0].resourceGroup -o tsv --subscription \"{subscriptionId}\""],
            invocations);
    }

    [Fact]
    public async Task FetchKubeConfigIsScopedToDeploymentSubscription()
    {
        const string subscriptionId = "00000000-0000-0000-0000-000000000001";
        var invocations = new List<string>();

        var kubeConfig = await AzureKubernetesEnvironmentResource.FetchKubeConfigAsync(
            "/usr/bin/az",
            subscriptionId,
            "deployment-rg",
            "deployment-aks",
            (path, arguments) =>
            {
                invocations.Add(arguments);
                return Task.FromResult(new AzureKubernetesEnvironmentResource.AzCommandResult(0, "kubeconfig-content", ""));
            });

        Assert.Equal("kubeconfig-content", kubeConfig);
        Assert.Equal(
            [$"aks get-credentials --resource-group \"deployment-rg\" --name \"deployment-aks\" --file - --subscription \"{subscriptionId}\""],
            invocations);
    }

    [Fact]
    public async Task FetchKubeConfigThrowsWhenAzureCliFails()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureKubernetesEnvironmentResource.FetchKubeConfigAsync(
                "/usr/bin/az",
                "00000000-0000-0000-0000-000000000001",
                "deployment-rg",
                "deployment-aks",
                (path, arguments) => Task.FromResult(
                    new AzureKubernetesEnvironmentResource.AzCommandResult(1, "", "subscription not found"))));

        Assert.Equal(
            "az aks get-credentials failed (exit code 1): subscription not found",
            exception.Message);
    }
}
