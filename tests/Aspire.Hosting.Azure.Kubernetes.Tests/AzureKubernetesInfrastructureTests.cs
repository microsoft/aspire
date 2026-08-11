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
    public async Task DestroyPipelineFetchesCredentialsBeforeClusterCleanupAndDeletesAzureLast()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            workspace.Path,
            step: WellKnownPipelineSteps.Diagnostics);

        var reporter = new TestPipelineActivityReporter(output);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        aks.AddHelmChart("podinfo", "oci://ghcr.io/stefanprodan/charts/podinfo", "6.7.1")
            .WithDestroy();
        aks.AddCertManager("cert-manager")
            .AddIssuer("letsencrypt");
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080);

        await using var app = builder.Build();
        await app.RunAsync();

        var diagnosticLines = reporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .SelectMany(message => message.Split('\n'))
            .Select(line => line.Trim())
            .ToList();

        Assert.Equal(
            "Direct dependencies: destroy-prereq",
            GetDirectDependencies(diagnosticLines, "aks-get-credentials-for-destroy-aks"));
        Assert.Equal(
            "Direct dependencies: aks-get-credentials-for-destroy-aks, destroy-prereq",
            GetDirectDependencies(diagnosticLines, "destroy-helm-aks"));
        Assert.Equal(
            "Direct dependencies: aks-get-credentials-for-destroy-aks, check-helm-prereqs-aks, cm-issuer-delete-letsencrypt, destroy-prereq",
            GetDirectDependencies(diagnosticLines, "helm-uninstall-cert-manager-chart"));
        Assert.Equal(
            "Direct dependencies: aks-get-credentials-for-destroy-aks, check-helm-prereqs-aks, destroy-prereq",
            GetDirectDependencies(diagnosticLines, "helm-uninstall-podinfo"));
        Assert.Equal(
            "Direct dependencies: aks-get-credentials-for-destroy-aks, destroy-prereq",
            GetDirectDependencies(diagnosticLines, "cm-issuer-delete-letsencrypt"));
        Assert.Equal(
            "Direct dependencies: cm-issuer-delete-letsencrypt, destroy-helm-aks, destroy-prereq, helm-uninstall-cert-manager-chart, helm-uninstall-podinfo",
            GetDirectDependencies(diagnosticLines, "destroy-azure-azure-environment"));
    }

    [Fact]
    public async Task DestroyPipelineUsesMatchingCredentialsForEachAksEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            workspace.Path,
            step: WellKnownPipelineSteps.Diagnostics);

        var reporter = new TestPipelineActivityReporter(output);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        var east = builder.AddAzureKubernetesEnvironment("east");
        var west = builder.AddAzureKubernetesEnvironment("west");
        builder.AddContainer("east-api", "myimage")
            .WithComputeEnvironment(east);
        builder.AddContainer("west-api", "myimage")
            .WithComputeEnvironment(west);

        await using var app = builder.Build();
        await app.RunAsync();

        var diagnosticLines = reporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .SelectMany(message => message.Split('\n'))
            .Select(line => line.Trim())
            .ToList();

        Assert.Equal(
            "Direct dependencies: aks-get-credentials-for-destroy-east, destroy-prereq",
            GetDirectDependencies(diagnosticLines, "destroy-helm-east"));
        Assert.Equal(
            "Direct dependencies: aks-get-credentials-for-destroy-west, destroy-prereq",
            GetDirectDependencies(diagnosticLines, "destroy-helm-west"));
        Assert.Equal(
            "Direct dependencies: destroy-helm-east, destroy-helm-west, destroy-prereq",
            GetDirectDependencies(diagnosticLines, "destroy-azure-azure-environment"));
    }

    [Fact]
    public async Task DestroyCredentialAcquisitionUsesPersistedAzureTarget()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var stateManager = new InMemoryDeploymentStateManager();
        stateManager.SetSection("Azure", new JsonObject
        {
            ["ResourceGroup"] = "persisted-resource-group",
            ["SubscriptionId"] = "00000000-1111-2222-3333-444444444444"
        });
        string? azArguments = null;
        var azCommandCount = 0;

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            workspace.Path);
        builder.Services.AddSingleton<IDeploymentStateManager>(stateManager);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        aks.Resource.Outputs["name"] = "aks";
        aks.Resource.AzCommandRunner = arguments =>
        {
            azCommandCount++;
            azArguments = arguments;
            return Task.FromResult((0, "apiVersion: v1", string.Empty));
        };

        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");

        await GetAksCredentialsForDestroyAsync(aks.Resource, new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        Assert.Equal(1, azCommandCount);
        Assert.Equal(
            "aks get-credentials --resource-group \"persisted-resource-group\" --name \"aks\" --file - " +
            "--subscription \"00000000-1111-2222-3333-444444444444\"",
            azArguments);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetAksCredentialsForDestroyAsync")]
    private static extern Task GetAksCredentialsForDestroyAsync(
        AzureKubernetesEnvironmentResource resource,
        PipelineStepContext context);

    private static string GetDirectDependencies(List<string> diagnosticLines, string stepName)
    {
        var targetLine = diagnosticLines.IndexOf($"If targeting '{stepName}':");
        Assert.InRange(targetLine, 0, diagnosticLines.Count - 2);
        return diagnosticLines[targetLine + 1];
    }
}
