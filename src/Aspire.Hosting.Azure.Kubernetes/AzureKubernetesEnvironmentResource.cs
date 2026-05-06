// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Represents an Azure Kubernetes Service (AKS) environment resource that provisions
/// an AKS cluster and serves as a compute environment for Kubernetes workloads.
/// </summary>
public partial class AzureKubernetesEnvironmentResource :
    AzureProvisioningResource,
    IAzureComputeEnvironmentResource,
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

            return Task.FromResult<IEnumerable<PipelineStep>>([prepareStep, getCredentialsStep]);
        }));
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
}
