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
    public async Task DeploymentScopeUsesCurrentDeploymentState()
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

        // Nothing is pinned on the resource, so the scope falls back to global deployment state.
        var deploymentScope = await AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
            scopedSubscription: null,
            scopedResourceGroup: null,
            services,
            TestContext.Current.CancellationToken);

        Assert.Equal(subscriptionId, deploymentScope.SubscriptionId);
        Assert.Equal(resourceGroup, deploymentScope.ResourceGroup);
    }

    [Fact]
    public async Task DeploymentScopeRequiresSubscription()
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
            () => AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
                scopedSubscription: null,
                scopedResourceGroup: null,
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
            [$"resource list --resource-type Microsoft.ContainerService/managedClusters --name \"deployment-aks\" --query [].resourceGroup -o tsv --subscription \"{subscriptionId}\""],
            invocations);
    }

    [Fact]
    public async Task GetResourceGroupThrowsWhenClusterNameIsAmbiguous()
    {
        const string subscriptionId = "00000000-0000-0000-0000-000000000001";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureKubernetesEnvironmentResource.GetResourceGroupAsync(
                "/usr/bin/az",
                "deployment-aks",
                subscriptionId,
                savedResourceGroup: null,
                NullLogger.Instance,
                (path, arguments) => Task.FromResult(
                    new AzureKubernetesEnvironmentResource.AzCommandResult(0, "first-rg\nsecond-rg\n", ""))));

        Assert.Equal(
            $"Found 2 AKS clusters named 'deployment-aks' in subscription '{subscriptionId}' " +
            "(resource groups: first-rg, second-rg). Specify which one to use by calling " +
            "AsExistingInResourceGroup on the resource.",
            exception.Message);
    }

    [Fact]
    public async Task GetResourceGroupThrowsWhenClusterIsNotFound()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureKubernetesEnvironmentResource.GetResourceGroupAsync(
                "/usr/bin/az",
                "deployment-aks",
                "00000000-0000-0000-0000-000000000001",
                savedResourceGroup: null,
                NullLogger.Instance,
                (path, arguments) => Task.FromResult(
                    new AzureKubernetesEnvironmentResource.AzCommandResult(0, "\n", ""))));

        Assert.Equal(
            "Could not resolve resource group for AKS cluster 'deployment-aks'. " +
            "Ensure Azure provisioning has completed.",
            exception.Message);
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

    [Fact]
    public async Task GetCredentialsStepScopesEveryAzureCliCallToDeploymentSubscription()
    {
        const string subscriptionId = "00000000-0000-0000-0000-000000000001";
        const string clusterName = "provisioned-aks";

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var deploymentStateManager = new InMemoryDeploymentStateManager();
        deploymentStateManager.SetSection("Azure", new JsonObject
        {
            ["SubscriptionId"] = subscriptionId
        });
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // The provisioned cluster name comes from a Bicep output, and no resource group is
        // saved in deployment state, so the step must fall back to the az resource list query.
        aks.Resource.Outputs["name"] = clusterName;

        var invocations = new List<string>();
        aks.Resource.AzCliPathResolverForTesting = () => "/usr/bin/az";
        aks.Resource.AzCommandRunnerForTesting = (path, arguments, logger) =>
        {
            invocations.Add(arguments);

            // The resource-group query runs first and returns the group the cluster lives in;
            // the get-credentials call that follows returns kubeconfig content.
            return Task.FromResult(arguments.StartsWith("resource list", StringComparison.Ordinal)
                ? new AzureKubernetesEnvironmentResource.AzCommandResult(0, "queried-rg\n", "")
                : new AzureKubernetesEnvironmentResource.AzCommandResult(0, "kubeconfig-content", ""));
        };

        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        // The resource carries several PipelineStepAnnotations (the base provisioning resource
        // contributes its own), so collect every step and select the one under test by name.
        var steps = new List<PipelineStep>();
        foreach (var annotation in aks.Resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = aks.Resource
            }));
        }

        var getCredentialsStep = Assert.Single(steps, step => step.Name == "aks-get-credentials-aks");

        // BicepOutputReference.GetValueAsync awaits this source before reading Outputs, so the
        // step would block forever without it. Provisioning normally completes it, and nothing
        // provisions here. It must be signalled after step creation because the base resource's
        // PipelineStepAnnotation assigns a fresh, incomplete source each time steps are built.
        Assert.NotNull(aks.Resource.ProvisioningTaskCompletionSource);
        aks.Resource.ProvisioningTaskCompletionSource.TrySetResult();

        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        await getCredentialsStep.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        // Assert on the exact command lines the step issued. This is what makes the test
        // guard the call site: reverting either call to an unscoped invocation fails here.
        Assert.Equal(
            [
                $"resource list --resource-type Microsoft.ContainerService/managedClusters --name \"{clusterName}\" --query [].resourceGroup -o tsv --subscription \"{subscriptionId}\"",
                $"aks get-credentials --resource-group \"queried-rg\" --name \"{clusterName}\" --file - --subscription \"{subscriptionId}\""
            ],
            invocations);

        // The copy-paste hint shown to users must carry the subscription too, otherwise it
        // reproduces the original bug by hand on whatever subscription az defaults to.
        var connectHint = Assert.Single(
            pipelineContext.Summary.Items,
            item => item.Key == "🔑 Connect to cluster");
        Assert.Equal(
            $"`az aks get-credentials --resource-group 'queried-rg' --name '{clusterName}' --subscription {subscriptionId}`",
            connectHint.Value);

        Assert.Equal(
            "kubeconfig-content",
            await File.ReadAllTextAsync(aks.Resource.KubernetesEnvironment.KubeConfigPath!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetCredentialsStepUsesExistingClusterScopeInsteadOfDeploymentState()
    {
        const string appSubscriptionId = "00000000-0000-0000-0000-000000000001";
        const string clusterSubscriptionId = "00000000-0000-0000-0000-000000000002";
        const string clusterResourceGroup = "shared-platform-rg";
        const string clusterName = "shared-aks";

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        // The app deploys into its own subscription and resource group...
        var deploymentStateManager = new InMemoryDeploymentStateManager();
        deploymentStateManager.SetSection("Azure", new JsonObject
        {
            ["SubscriptionId"] = appSubscriptionId,
            ["ResourceGroup"] = "app-rg"
        });
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);

        // ...but the cluster it targets already exists somewhere else entirely.
        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .AsExistingInResourceGroup(clusterName, clusterResourceGroup, clusterSubscriptionId);

        aks.Resource.Outputs["name"] = clusterName;

        var invocations = new List<string>();
        aks.Resource.AzCliPathResolverForTesting = () => "/usr/bin/az";
        aks.Resource.AzCommandRunnerForTesting = (path, arguments, logger) =>
        {
            invocations.Add(arguments);
            return Task.FromResult(new AzureKubernetesEnvironmentResource.AzCommandResult(0, "kubeconfig-content", ""));
        };

        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var steps = new List<PipelineStep>();
        foreach (var annotation in aks.Resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = aks.Resource
            }));
        }

        var getCredentialsStep = Assert.Single(steps, step => step.Name == "aks-get-credentials-aks");

        Assert.NotNull(aks.Resource.ProvisioningTaskCompletionSource);
        aks.Resource.ProvisioningTaskCompletionSource.TrySetResult();

        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        await getCredentialsStep.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        // Only get-credentials should run: the resource group is pinned by the annotation, so no
        // discovery query is needed. Both scope values must come from the annotation, not the
        // app's own deployment state.
        Assert.Equal(
            [$"aks get-credentials --resource-group \"{clusterResourceGroup}\" --name \"{clusterName}\" --file - --subscription \"{clusterSubscriptionId}\""],
            invocations);

        var connectHint = Assert.Single(
            pipelineContext.Summary.Items,
            item => item.Key == "🔑 Connect to cluster");
        Assert.Equal(
            $"`az aks get-credentials --resource-group '{clusterResourceGroup}' --name '{clusterName}' --subscription {clusterSubscriptionId}`",
            connectHint.Value);
    }

    [Fact]
    public async Task DeploymentScopeFallsBackToDeploymentStateWhenResourcePinsNothing()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        var scope = await AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
            scopedSubscription: null,
            scopedResourceGroup: null,
            services,
            TestContext.Current.CancellationToken);

        Assert.Equal(("sub-global", "rg-global"), scope);
    }

    [Fact]
    public async Task DeploymentScopeKeepsDeploymentResourceGroupWhenResourcePinsSameSubscription()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        var scope = await AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
            scopedSubscription: "sub-global",
            scopedResourceGroup: null,
            services,
            TestContext.Current.CancellationToken);

        Assert.Equal(("sub-global", "rg-global"), scope);
    }

    [Fact]
    public async Task DeploymentScopeDropsDeploymentResourceGroupWhenResourcePinsAnotherSubscription()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        var scope = await AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
            scopedSubscription: "sub-other",
            scopedResourceGroup: null,
            services,
            TestContext.Current.CancellationToken);

        // "rg-global" names a group inside "sub-global" only. Carrying it across the subscription
        // boundary could miss entirely or hit an unrelated group with the same name, so the step
        // must rediscover it instead.
        Assert.Equal(("sub-other", (string?)null), scope);
    }

    [Fact]
    public async Task DeploymentScopeUsesDeploymentSubscriptionWhenResourcePinsOnlyResourceGroup()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        var scope = await AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
            scopedSubscription: null,
            scopedResourceGroup: "rg-pinned",
            services,
            TestContext.Current.CancellationToken);

        Assert.Equal(("sub-global", "rg-pinned"), scope);
    }

    [Fact]
    public async Task DeploymentScopeIgnoresDeploymentStateWhenResourcePinsBothValues()
    {
        // No Azure section at all: a fully pinned resource must not depend on deployment state.
        var services = new ServiceCollection()
            .AddSingleton<IDeploymentStateManager>(new InMemoryDeploymentStateManager())
            .BuildServiceProvider();

        var scope = await AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
            scopedSubscription: "sub-pinned",
            scopedResourceGroup: "rg-pinned",
            services,
            TestContext.Current.CancellationToken);

        Assert.Equal(("sub-pinned", "rg-pinned"), scope);
    }

    [Fact]
    public async Task DeploymentScopeResolvesParameterBackedScopeValues()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        var subscriptionParameter = new ParameterResource("sub", _ => "sub-from-parameter");
        var resourceGroupParameter = new ParameterResource("rg", _ => "rg-from-parameter");

        var scope = await AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
            subscriptionParameter,
            resourceGroupParameter,
            services,
            TestContext.Current.CancellationToken);

        Assert.Equal(("sub-from-parameter", "rg-from-parameter"), scope);
    }

    [Fact]
    public async Task DeploymentScopeThrowsWhenScopeProviderResolvesNull()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        // Provisioning rejects a null scope value outright. Silently substituting the app's own
        // subscription here would diverge from that and could adopt a same-named cluster elsewhere.
        var unavailableSubscription = new ParameterResource("sub", _ => null!);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
                unavailableSubscription,
                scopedResourceGroup: null,
                services,
                TestContext.Current.CancellationToken));

        Assert.Equal("The Azure resource scope value cannot be null or empty.", exception.Message);
    }

    [Fact]
    public async Task DeploymentScopeThrowsWhenScopeProviderResolvesEmpty()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        // An empty value is dropped by the string.IsNullOrEmpty checks downstream, silently
        // reintroducing the global-scope fallback, so it has to be rejected just like null.
        var emptyResourceGroup = new ParameterResource("rg", _ => "");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
                scopedSubscription: null,
                emptyResourceGroup,
                services,
                TestContext.Current.CancellationToken));

        Assert.Equal("The Azure resource scope value cannot be null or empty.", exception.Message);
    }

    [Fact]
    public async Task GetCredentialsStepPrefersExplicitScopeOverExistingResourceAnnotation()
    {
        const string scopeSubscriptionId = "00000000-0000-0000-0000-000000000003";
        const string scopeResourceGroup = "scope-assigned-rg";
        const string clusterName = "scoped-aks";

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var deploymentStateManager = new InMemoryDeploymentStateManager();
        deploymentStateManager.SetSection("Azure", new JsonObject
        {
            ["SubscriptionId"] = "00000000-0000-0000-0000-000000000001",
            ["ResourceGroup"] = "app-rg"
        });
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .AsExistingInResourceGroup(clusterName, "annotation-rg", "00000000-0000-0000-0000-000000000002");

        // ConfigureInfrastructure can assign Scope directly, and the provisioner gives it precedence
        // over the annotation, so the credential fetch has to follow the same precedence.
        aks.Resource.Scope = new AzureBicepResourceScope(scopeResourceGroup, scopeSubscriptionId);
        aks.Resource.Outputs["name"] = clusterName;

        var invocations = new List<string>();
        aks.Resource.AzCliPathResolverForTesting = () => "/usr/bin/az";
        aks.Resource.AzCommandRunnerForTesting = (path, arguments, logger) =>
        {
            invocations.Add(arguments);
            return Task.FromResult(new AzureKubernetesEnvironmentResource.AzCommandResult(0, "kubeconfig-content", ""));
        };

        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var steps = new List<PipelineStep>();
        foreach (var annotation in aks.Resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = aks.Resource
            }));
        }

        var getCredentialsStep = Assert.Single(steps, step => step.Name == "aks-get-credentials-aks");

        Assert.NotNull(aks.Resource.ProvisioningTaskCompletionSource);
        aks.Resource.ProvisioningTaskCompletionSource.TrySetResult();

        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        await getCredentialsStep.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        Assert.Equal(
            [$"aks get-credentials --resource-group \"{scopeResourceGroup}\" --name \"{clusterName}\" --file - --subscription \"{scopeSubscriptionId}\""],
            invocations);
    }

    [Fact]
    public async Task GetCredentialsStepFallsBackToDeploymentStateForSubscriptionScopedResources()
    {
        const string subscriptionId = "00000000-0000-0000-0000-000000000001";
        const string clusterName = "subscription-scoped-aks";

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var deploymentStateManager = new InMemoryDeploymentStateManager();
        deploymentStateManager.SetSection("Azure", new JsonObject
        {
            ["SubscriptionId"] = subscriptionId,
            ["ResourceGroup"] = "app-rg"
        });
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // A subscription-scoped Scope pins no resource group, and reading AzureBicepResourceScope.ResourceGroup
        // in that state throws, so the step must fall back rather than fail.
        aks.Resource.Scope = AzureBicepResourceScope.CreateForSubscription(subscriptionId);
        aks.Resource.Outputs["name"] = clusterName;

        var invocations = new List<string>();
        aks.Resource.AzCliPathResolverForTesting = () => "/usr/bin/az";
        aks.Resource.AzCommandRunnerForTesting = (path, arguments, logger) =>
        {
            invocations.Add(arguments);
            return Task.FromResult(new AzureKubernetesEnvironmentResource.AzCommandResult(0, "kubeconfig-content", ""));
        };

        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var steps = new List<PipelineStep>();
        foreach (var annotation in aks.Resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = aks.Resource
            }));
        }

        var getCredentialsStep = Assert.Single(steps, step => step.Name == "aks-get-credentials-aks");

        Assert.NotNull(aks.Resource.ProvisioningTaskCompletionSource);
        aks.Resource.ProvisioningTaskCompletionSource.TrySetResult();

        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        await getCredentialsStep.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        // The scope subscription matches deployment state, so the saved resource group still applies.
        Assert.Equal(
            [$"aks get-credentials --resource-group \"app-rg\" --name \"{clusterName}\" --file - --subscription \"{subscriptionId}\""],
            invocations);
    }

    [Fact]
    public async Task DeploymentScopeThrowsWhenScopeValueIsEmptyString()
    {
        using var services = CreateServicesWithAzureState("sub-global", "rg-global");

        // Nothing upstream rejects an empty scope string: AsExistingInResourceGroup and the
        // AzureBicepResourceScope constructors only guard against null. Without this check the
        // value would be treated as unpinned and silently fall back to the global scope.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureKubernetesEnvironmentResource.ResolveDeploymentScopeAsync(
                scopedSubscription: "",
                scopedResourceGroup: null,
                services,
                TestContext.Current.CancellationToken));

        Assert.Equal("The Azure resource scope value cannot be null or empty.", exception.Message);
    }

    private static ServiceProvider CreateServicesWithAzureState(string subscriptionId, string? resourceGroup)
    {
        var deploymentStateManager = new InMemoryDeploymentStateManager();
        var azureState = new JsonObject { ["SubscriptionId"] = subscriptionId };

        if (resourceGroup is not null)
        {
            azureState["ResourceGroup"] = resourceGroup;
        }

        deploymentStateManager.SetSection("Azure", azureState);

        return new ServiceCollection()
            .AddSingleton<IDeploymentStateManager>(deploymentStateManager)
            .BuildServiceProvider();
    }
}
