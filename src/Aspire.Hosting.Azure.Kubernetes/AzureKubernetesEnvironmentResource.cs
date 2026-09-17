// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREAZURE001

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Represents an Azure Kubernetes Service (AKS) environment resource that provisions
/// an AKS cluster and serves as a compute environment for Kubernetes workloads.
/// </summary>
public partial class AzureKubernetesEnvironmentResource :
    AzureProvisioningResource,
    IAzureComputeEnvironmentResource,
    IComputeEnvironmentWithVolumeMounts,
    IAzureNspAssociationTarget
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureKubernetesEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure infrastructure.</param>
    public AzureKubernetesEnvironmentResource(
        string name,
        Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure)
    {
        // Add pipeline step annotation to register per-environment AKS steps:
        //   - prepare-aks-{name}: applies node-pool/workload-identity annotations to compute
        //     resources targeted at this AKS env. Runs before BeforeStart so the inner
        //     KubernetesEnvironmentResource's prepare-deployment-targets-{k8s-name} step
        //     can observe the annotations.
        //   - aks-get-credentials-{name}: fetches AKS credentials into an isolated
        //     kubeconfig file after AKS is provisioned, before Helm prepare runs.
        //   - aks-get-credentials-for-destroy-{name}: fetches credentials from saved
        //     deployment state before cluster-scoped destroy steps run.
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var k8sEnv = KubernetesEnvironment;

            var prepareStep = new PipelineStep
            {
                Name = $"prepare-aks-{Name}",
                Description = $"Prepares Azure Kubernetes Service environment {Name}.",
                Action = ctx => PrepareAksEnvironmentAsync(ctx),
                DependsOnSteps = [WellKnownPipelineSteps.ValidateComputeEnvironments],
                RequiredBySteps =
                [
                    WellKnownPipelineSteps.BeforeStart,
                    // Ensure this runs before the inner K8s env materializes service resources,
                    // because that step reads node-pool and workload-identity annotations
                    // applied by PrepareAksEnvironmentAsync.
                    $"prepare-deployment-targets-{k8sEnv.Name}"
                ]
            };

            var getCredentialsStep = new PipelineStep
            {
                Name = $"aks-get-credentials-{Name}",
                Description = $"Fetches AKS credentials for {Name}",
                Action = ctx => GetAksCredentialsAsync(ctx),
                // Run after ALL Azure infrastructure is provisioned (including the AKS cluster).
                DependsOnSteps = [AzureEnvironmentResource.ProvisionInfrastructureStepName],
                // Must complete before the Helm prepare step on the inner K8s env.
                RequiredBySteps = [$"prepare-{k8sEnv.Name}"]
            };

            var getDestroyCredentialsStep = new PipelineStep
            {
                Name = $"aks-get-credentials-for-destroy-{Name}",
                Description = $"Fetches AKS credentials for destroying {Name}",
                Action = ctx => GetAksCredentialsForDestroyAsync(ctx),
                // Keep this separate from the deploy credential step: depending on Azure
                // provisioning here would pull provisioning into the destroy graph.
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq]
            };

            return Task.FromResult<IEnumerable<PipelineStep>>([prepareStep, getCredentialsStep, getDestroyCredentialsStep]);
        }));

        Annotations.Add(new PipelineConfigurationAnnotation(async context =>
        {
            var k8sEnv = KubernetesEnvironment;
            var getDestroyCredentialsStep = context.GetSteps(this)
                .Single(step => step.Name == $"aks-get-credentials-for-destroy-{Name}");
            var kubernetesDestroySteps = context
                .GetSteps(HelmDeploymentEngine.GetKubernetesDestroyTag(k8sEnv.Name))
                .ToList();

            var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
            var deploymentStateSection = await deploymentStateManager
                .AcquireSectionAsync($"Azure:Deployments:{Name}")
                .ConfigureAwait(false);

            var azureEnvironment = context.Model.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"Azure environment resource required by AKS environment '{Name}' was not found.");
            var destroyAzureStep = context.GetSteps(azureEnvironment)
                .SingleOrDefault(step => step.Name == $"destroy-azure-{azureEnvironment.Name}")
                ?? throw new InvalidOperationException(
                    $"Azure destroy step for environment '{azureEnvironment.Name}' was not found.");

            // A never-deployed AKS environment has no isolated kubeconfig to acquire. Likewise, a
            // partially deployed environment can persist the cluster ID before any Helm release saves
            // destroy state. In either case, aggregate Azure cleanup must skip cluster-scoped destroy
            // steps rather than block on reacquiring credentials when there is nothing known to clean
            // up. Explicitly targeting one of those Kubernetes cleanup steps still runs through the
            // credential prerequisite and fails rather than allowing the command to fall back to the
            // caller's ambient Kubernetes context.
            var targetStep = context.Services.GetRequiredService<IOptions<PipelineOptions>>().Value.Step;
            var hasPersistedAksIdentity = HasPersistedAksIdentity(deploymentStateSection.Data);
            var hasPersistedKubernetesCleanupState = hasPersistedAksIdentity &&
                await HasPersistedKubernetesCleanupStateAsync(
                    deploymentStateManager,
                    context.Model,
                    k8sEnv).ConfigureAwait(false);

            if (!hasPersistedAksIdentity || !hasPersistedKubernetesCleanupState)
            {
                if (string.Equals(targetStep, WellKnownPipelineSteps.Destroy, StringComparison.Ordinal))
                {
                    foreach (var kubernetesDestroyStep in kubernetesDestroySteps)
                    {
                        kubernetesDestroyStep.RequiredBySteps.RemoveAll(
                            static stepName => string.Equals(stepName, WellKnownPipelineSteps.Destroy, StringComparison.Ordinal));
                    }

                    return;
                }

                if (string.Equals(targetStep, destroyAzureStep.Name, StringComparison.Ordinal))
                {
                    return;
                }
            }

            foreach (var kubernetesDestroyStep in kubernetesDestroySteps)
            {
                kubernetesDestroyStep.DependsOn(getDestroyCredentialsStep);

                // The direct Helm uninstall step is an explicit, no-confirmation alternative to the
                // aggregate destroy step. It needs isolated credentials when targeted directly, but
                // Azure cleanup must not schedule both alternatives and uninstall the release twice.
                if (!string.Equals(
                    kubernetesDestroyStep.Name,
                    HelmDeploymentEngine.GetHelmUninstallStepName(k8sEnv.Name),
                    StringComparison.Ordinal))
                {
                    destroyAzureStep.DependsOn(kubernetesDestroyStep);
                }
            }
        }));
    }

    private static async Task<bool> HasPersistedKubernetesCleanupStateAsync(
        IDeploymentStateManager deploymentStateManager,
        DistributedApplicationModel model,
        KubernetesEnvironmentResource environment)
    {
        var environmentState = await deploymentStateManager
            .AcquireSectionAsync($"Helm:{environment.Name}")
            .ConfigureAwait(false);
        if (HasPersistedHelmReleaseState(environmentState.Data))
        {
            return true;
        }

        foreach (var chart in model.Resources.OfType<KubernetesHelmChartResource>())
        {
            if (!chart.DestroyOnUninstall ||
                !string.Equals(chart.Parent.Name, environment.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var chartState = await deploymentStateManager
                .AcquireSectionAsync($"HelmChart:{environment.Name}:{chart.Name}")
                .ConfigureAwait(false);
            if (HasPersistedHelmReleaseState(chartState.Data))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPersistedHelmReleaseState(JsonObject deploymentState)
    {
        try
        {
            return !string.IsNullOrEmpty(deploymentState["ReleaseName"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the underlying Kubernetes environment resource used for Helm-based deployment.
    /// </summary>
    internal KubernetesEnvironmentResource KubernetesEnvironment { get; set; } = default!;

    /// <summary>
    /// Gets the resource ID of the AKS cluster.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the fully qualified domain name of the AKS cluster.
    /// </summary>
    public BicepOutputReference ClusterFqdn => new("clusterFqdn", this);

    /// <summary>
    /// Gets the OIDC issuer URL for the AKS cluster, used for workload identity federation.
    /// </summary>
    public BicepOutputReference OidcIssuerUrl => new("oidcIssuerUrl", this);

    /// <summary>
    /// Gets the object ID of the kubelet managed identity.
    /// </summary>
    public BicepOutputReference KubeletIdentityObjectId => new("kubeletIdentityObjectId", this);

    /// <summary>
    /// Gets the name of the node resource group.
    /// </summary>
    public BicepOutputReference NodeResourceGroup => new("nodeResourceGroup", this);

    /// <summary>
    /// Gets the name output reference for the AKS cluster.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets or sets the Kubernetes version for the AKS cluster.
    /// </summary>
    internal string? KubernetesVersion { get; set; }

    /// <summary>
    /// Gets or sets whether OIDC issuer is enabled on the cluster.
    /// </summary>
    internal bool OidcIssuerEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether workload identity is enabled on the cluster.
    /// </summary>
    internal bool WorkloadIdentityEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the Log Analytics workspace resource for monitoring.
    /// </summary>
    internal AzureLogAnalyticsWorkspaceResource? LogAnalyticsWorkspace { get; set; }

    /// <summary>
    /// Gets or sets whether Container Insights is enabled.
    /// </summary>
    internal bool ContainerInsightsEnabled { get; set; }

    /// <summary>
    /// Gets the node pool configurations.
    /// </summary>
    internal List<AksNodePoolConfig> NodePools { get; } =
    [
        new AksNodePoolConfig("system", "Standard_D2s_v5", 1, 3, AksNodePoolMode.System)
    ];

    /// <summary>
    /// Gets the per-node-pool subnet overrides. Key is the pool name.
    /// </summary>
    internal Dictionary<string, BicepOutputReference> NodePoolSubnets { get; } = [];

    /// <summary>
    /// Gets the workload identity mappings. Key is the resource name, value is the identity resource.
    /// Used to generate federated identity credentials in Bicep.
    /// </summary>
    internal Dictionary<string, IAppIdentityResource> WorkloadIdentities { get; } = [];

    /// <summary>
    /// Gets or sets the network profile for the AKS cluster.
    /// </summary>
    internal AksNetworkProfile? NetworkProfile { get; set; }

    /// <summary>
    /// Gets or sets whether the cluster should be private.
    /// </summary>
    internal bool IsPrivateCluster { get; set; }

    /// <summary>
    /// Gets or sets the default container registry auto-created for this AKS environment.
    /// </summary>
    internal AzureContainerRegistryResource? DefaultContainerRegistry { get; set; }

    /// <summary>
    /// Gets the load balancer resources registered against this AKS environment via
    /// <see cref="AzureKubernetesEnvironmentExtensions.AddLoadBalancer"/>. Used by
    /// the Bicep emission to synthesize per-LB role assignments granting the
    /// AKS-auto-created AGC controller identity permission to join each LB subnet.
    /// </summary>
    internal List<AzureKubernetesLoadBalancerResource> LoadBalancers { get; } = [];

    /// <summary>
    /// Gets or sets whether the AKS managed Gateway API installation is enabled on the
    /// cluster. Toggled internally by <see cref="AzureKubernetesEnvironmentExtensions.AddLoadBalancer"/>;
    /// not exposed as a public extension because it's only useful in combination with the
    /// AGC ALB controller add-on (<see cref="ApplicationLoadBalancerEnabled"/>) today.
    /// </summary>
    internal bool GatewayApiEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the Azure Application Gateway for Containers (AGC) ALB
    /// controller add-on is enabled on the cluster. Toggled internally by
    /// <see cref="AzureKubernetesEnvironmentExtensions.AddLoadBalancer"/>.
    /// </summary>
    internal bool ApplicationLoadBalancerEnabled { get; set; }

    /// <summary>
    /// Whether the cluster needs to be emitted using a preview Bicep API version because
    /// it depends on <c>ingressProfile.gatewayAPI</c> or <c>ingressProfile.applicationLoadBalancer</c>,
    /// neither of which is in any stable AKS API version yet (latest stable
    /// <c>2026-01-01</c> doesn't have them; <c>gatewayAPI</c> first appears in
    /// <c>2025-08-02-preview</c>, <c>applicationLoadBalancer</c> in
    /// <c>2025-09-02-preview</c>).
    /// </summary>
    internal bool RequiresPreviewIngressApi => GatewayApiEnabled || ApplicationLoadBalancerEnabled;
}
