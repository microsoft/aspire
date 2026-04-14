// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Pipeline types are experimental
#pragma warning disable ASPIREAZURE001 // AzureEnvironmentResource is experimental
#pragma warning disable ASPIREAZURE003 // Subnet/network types are experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure Kubernetes Service (AKS) environments to the application model.
/// </summary>
public static class AzureKubernetesEnvironmentExtensions
{
    /// <summary>
    /// Adds an Azure Kubernetes Service (AKS) environment to the distributed application.
    /// This provisions an AKS cluster and configures it as a Kubernetes compute environment.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the AKS environment resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/>.</returns>
    /// <remarks>
    /// This method internally creates a Kubernetes environment for Helm-based deployment
    /// and provisions an AKS cluster via Azure Bicep. It combines the functionality of
    /// <c>AddKubernetesEnvironment</c> with Azure-specific provisioning.
    /// </remarks>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks")
    ///     .WithVersion("1.30");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> AddAzureKubernetesEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Set up Azure provisioning infrastructure
        builder.AddAzureProvisioning();
        builder.Services.Configure<AzureProvisioningOptions>(
            o => o.SupportsTargetedRoleAssignments = true);

        // Register the AKS-specific infrastructure eventing subscriber
        builder.Services.TryAddEventingSubscriber<AzureKubernetesInfrastructure>();

        // Create the inner KubernetesEnvironmentResource via the public API.
        // This registers KubernetesInfrastructure, creates the resource with
        // Helm chart name/dashboard, adds it to the model, and sets up the
        // default Helm deployment engine.
        var k8sEnvBuilder = builder.AddKubernetesEnvironment($"{name}-k8s");

        // Scope the Helm chart name to this AKS environment to avoid
        // conflicts when multiple environments deploy to the same cluster
        // or when re-deploying with different environment names.
        k8sEnvBuilder.Resource.HelmChartName = $"{builder.Environment.ApplicationName}-{name}".ToLowerInvariant().Replace(' ', '-');

        // Create the unified AKS environment resource
        var resource = new AzureKubernetesEnvironmentResource(name, ConfigureAksInfrastructure);
        resource.KubernetesEnvironment = k8sEnvBuilder.Resource;

        // Set the parent so KubernetesInfrastructure matches resources that use
        // WithComputeEnvironment(aksEnv) — the inner K8s env checks both itself
        // and its parent when filtering compute resources.
        k8sEnvBuilder.Resource.ParentComputeEnvironment = resource;

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        // Auto-create a default Azure Container Registry for image push/pull.
        // Wire it to the inner K8s environment immediately so KubernetesInfrastructure
        // can discover it during BeforeStartEvent (both subscribers run during the same
        // event, so we can't rely on annotation ordering during the event).
        var defaultRegistry = builder.AddAzureContainerRegistry($"{name}-acr");
        resource.DefaultContainerRegistry = defaultRegistry.Resource;
        k8sEnvBuilder.WithAnnotation(new ContainerRegistryReferenceAnnotation(defaultRegistry.Resource));

        // Wire ACR name as a parameter on the AKS resource so the Bicep module
        // can create an AcrPull role assignment for the kubelet identity.
        // The publishing context will wire this as a parameter in main.bicep.
        resource.Parameters["acrName"] = defaultRegistry.Resource.NameOutputReference;

        // Ensure push steps wait for ALL Azure provisioning to complete. Push steps
        // call registry.Endpoint.GetValueAsync() which awaits the BicepOutputReference
        // for loginServer — if the ACR hasn't been provisioned yet, this blocks.
        //
        // NOTE: The standard push step dependency wiring (pushSteps.DependsOn(buildSteps) 
        // and pushSteps.DependsOn(push-prereq)) from ProjectResource's PipelineConfigurationAnnotation
        // may not resolve correctly when using Kubernetes compute environments, because
        // context.GetSteps(resource, tag) may return empty if the resource reference doesn't
        // match. We explicitly wire the dependencies here as a workaround.
        k8sEnvBuilder.WithAnnotation(new PipelineConfigurationAnnotation(context =>
        {
            var pushSteps = context.Steps
                .Where(s => s.Tags.Contains(WellKnownPipelineTags.PushContainerImage))
                .ToList();

            foreach (var pushStep in pushSteps)
            {
                // Ensure push waits for Azure provisioning (ACR endpoint resolution)
                pushStep.DependsOn(AzureEnvironmentResource.ProvisionInfrastructureStepName);

                // Ensure push waits for push-prereq (ACR login)
                pushStep.DependsOn(WellKnownPipelineSteps.PushPrereq);

                // Ensure push waits for its corresponding build step
                var resourceName = pushStep.Resource?.Name;
                if (resourceName is not null)
                {
                    pushStep.DependsOn($"build-{resourceName}");
                }
            }
        }));

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Configures the Kubernetes version for the AKS cluster.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="version">The Kubernetes version (e.g., "1.30").</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithVersion(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        string version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(version);

        builder.Resource.KubernetesVersion = version;
        return builder;
    }

    /// <summary>
    /// Configures the SKU tier for the AKS cluster.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tier">The SKU tier.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithSkuTier(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        AksSkuTier tier)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SkuTier = tier;
        return builder;
    }

    /// <summary>
    /// Adds a node pool to the AKS cluster.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The name of the node pool.</param>
    /// <param name="vmSize">The VM size for nodes.</param>
    /// <param name="minCount">The minimum node count for autoscaling.</param>
    /// <param name="maxCount">The maximum node count for autoscaling.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AksNodePoolResource}"/> for the new node pool.</returns>
    /// <remarks>
    /// The returned node pool resource can be passed to
    /// <see cref="KubernetesEnvironmentExtensions.WithNodePool{T}"/> on compute resources to schedule workloads on this pool.
    /// </remarks>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    /// var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);
    ///
    /// builder.AddProject&lt;MyApi&gt;()
    ///     .WithNodePool(gpuPool);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AksNodePoolResource> AddNodePool(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name,
        string vmSize,
        int minCount,
        int maxCount)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(vmSize);

        var config = new AksNodePoolConfig(name, vmSize, minCount, maxCount, AksNodePoolMode.User);
        builder.Resource.NodePools.Add(config);

        var nodePool = new AksNodePoolResource(name, config, builder.Resource);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(nodePool);
        }

        return builder.ApplicationBuilder.AddResource(nodePool)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Configures the AKS cluster as a private cluster with a private API server endpoint.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> AsPrivateCluster(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.IsPrivateCluster = true;
        return builder;
    }

    /// <summary>
    /// Configures the AKS cluster to use a VNet subnet for node pool networking.
    /// Unlike <see cref="AzureVirtualNetworkExtensions.WithDelegatedSubnet{T}"/>, this does NOT
    /// add a service delegation to the subnet — AKS uses plain (non-delegated) subnets.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="subnet">The subnet to use for AKS node pools.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
    /// var subnet = vnet.AddSubnet("aks-subnet", "10.0.0.0/22");
    /// var aks = builder.AddAzureKubernetesEnvironment("aks")
    ///     .WithSubnet(subnet);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithSubnet(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureSubnetResource> subnet)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subnet);

        builder.WithAnnotation(new AksSubnetAnnotation(subnet.Resource.Id));
        return builder;
    }

    /// <summary>
    /// Configures a specific AKS node pool to use its own VNet subnet.
    /// When applied, this node pool's subnet overrides the environment-level subnet
    /// set via <see cref="WithSubnet(IResourceBuilder{AzureKubernetesEnvironmentResource}, IResourceBuilder{AzureSubnetResource})"/>.
    /// </summary>
    /// <param name="builder">The node pool resource builder.</param>
    /// <param name="subnet">The subnet to use for this node pool.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AksNodePoolResource}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
    /// var defaultSubnet = vnet.AddSubnet("default", "10.0.0.0/22");
    /// var gpuSubnet = vnet.AddSubnet("gpu-subnet", "10.0.4.0/24");
    ///
    /// var aks = builder.AddAzureKubernetesEnvironment("aks")
    ///     .WithSubnet(defaultSubnet);
    ///
    /// var gpuPool = aks.AddNodePool("gpu", AzureVmSizes.GpuAccelerated.StandardNC6sV3, 0, 5)
    ///     .WithSubnet(gpuSubnet);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AksNodePoolResource> WithSubnet(
        this IResourceBuilder<AksNodePoolResource> builder,
        IResourceBuilder<AzureSubnetResource> subnet)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subnet);

        // Store the subnet on the node pool annotation for Bicep resolution
        builder.WithAnnotation(new AksSubnetAnnotation(subnet.Resource.Id));

        // Also register in the parent AKS environment's per-pool subnet dictionary
        // so Bicep generation can emit the correct parameter per pool.
        builder.Resource.AksParent.NodePoolSubnets[builder.Resource.Name] = subnet.Resource.Id;

        return builder;
    }

    /// <summary>
    /// Configures the AKS environment to use a specific Azure Container Registry for image storage.
    /// When set, this replaces the auto-created default container registry.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="registry">The Azure Container Registry resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    /// <remarks>
    /// If not called, a default Azure Container Registry is automatically created.
    /// The registry endpoint is flowed to the inner Kubernetes environment so that
    /// Helm deployments can push and pull images.
    /// </remarks>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithContainerRegistry(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureContainerRegistryResource> registry)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registry);

        // Remove the default registry from the model if one was auto-created
        if (builder.Resource.DefaultContainerRegistry is not null)
        {
            builder.ApplicationBuilder.Resources.Remove(builder.Resource.DefaultContainerRegistry);
            builder.Resource.DefaultContainerRegistry = null;
        }

        // Set the explicit registry via annotation on both the AKS environment
        // and the inner K8s environment (so KubernetesInfrastructure finds it)
        builder.WithAnnotation(new ContainerRegistryReferenceAnnotation(registry.Resource));
        builder.Resource.KubernetesEnvironment.Annotations.Add(
            new ContainerRegistryReferenceAnnotation(registry.Resource));

        // Update the acrName parameter to reference the explicit registry's output
        // (replaces the default ACR reference set during AddAzureKubernetesEnvironment)
        builder.Resource.Parameters["acrName"] = registry.Resource.NameOutputReference;

        return builder;
    }

    /// <summary>
    /// Enables Container Insights monitoring on the AKS cluster.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="logAnalytics">Optional Log Analytics workspace. If not provided, one will be auto-created.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithContainerInsights(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource>? logAnalytics = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ContainerInsightsEnabled = true;

        if (logAnalytics is not null)
        {
            builder.Resource.LogAnalyticsWorkspace = logAnalytics.Resource;
        }

        return builder;
    }

    /// <summary>
    /// Configures the AKS environment to use a specific Azure Log Analytics workspace.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="workspaceBuilder">The Log Analytics workspace resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithAzureLogAnalyticsWorkspace(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource> workspaceBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(workspaceBuilder);

        builder.Resource.LogAnalyticsWorkspace = workspaceBuilder.Resource;
        return builder;
    }

    /// <summary>
    /// Enables workload identity on the AKS environment, allowing pods to authenticate
    /// to Azure services using federated credentials.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    /// <remarks>
    /// This ensures the AKS cluster is configured with OIDC issuer and workload identity enabled.
    /// Workload identity is automatically wired when compute resources have an <see cref="AppIdentityAnnotation"/>,
    /// which is added by <c>WithAzureUserAssignedIdentity</c> or auto-created by <c>AzureResourcePreparer</c>.
    /// </remarks>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithWorkloadIdentity(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.OidcIssuerEnabled = true;
        builder.Resource.WorkloadIdentityEnabled = true;
        return builder;
    }

    // ConfigureAksInfrastructure is a no-op placeholder required by the
    // AzureProvisioningResource base class constructor. The actual Bicep is
    // generated by GetBicepTemplateString/GetBicepTemplateFile overrides
    // in AzureKubernetesEnvironmentResource.
    private static void ConfigureAksInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        // Intentionally empty — Bicep generation is handled by the resource's
        // GetBicepTemplateString override, not the provisioning infrastructure.
    }
}
