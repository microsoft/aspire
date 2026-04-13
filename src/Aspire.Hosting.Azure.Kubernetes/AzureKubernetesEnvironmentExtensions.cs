// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Lifecycle;
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

        // Create the unified AKS environment resource
        var resource = new AzureKubernetesEnvironmentResource(name, ConfigureAksInfrastructure);
        resource.KubernetesEnvironment = k8sEnvBuilder.Resource;

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
    /// <see cref="WithNodePoolAffinity{T}"/> on compute resources to schedule workloads on this pool.
    /// </remarks>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    /// var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);
    ///
    /// builder.AddProject&lt;MyApi&gt;()
    ///     .WithNodePoolAffinity(gpuPool);
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
    /// Schedules a compute resource's workload on the specified AKS node pool.
    /// This translates to a Kubernetes <c>nodeSelector</c> with the <c>agentpool</c> label
    /// targeting the named node pool.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nodePool">The node pool to schedule the workload on.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    /// var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);
    ///
    /// builder.AddProject&lt;MyApi&gt;()
    ///     .WithNodePoolAffinity(gpuPool);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<T> WithNodePoolAffinity<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AksNodePoolResource> nodePool)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(nodePool);

        builder.WithAnnotation(new AksNodePoolAffinityAnnotation(nodePool.Resource));
        return builder;
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
    /// Use <see cref="WithAzureWorkloadIdentity{T}"/> on individual compute resources to assign
    /// specific managed identities.
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

    /// <summary>
    /// Configures a compute resource to use AKS workload identity with the specified managed identity.
    /// This generates a Kubernetes ServiceAccount and a federated identity credential in Azure.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="identity">The managed identity to federate with. If null, an identity will be auto-created.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var identity = builder.AddAzureUserAssignedIdentity("myIdentity");
    /// builder.AddProject&lt;MyApi&gt;()
    ///     .WithAzureWorkloadIdentity(identity);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "AKS hosting is not yet supported in ATS")]
    public static IResourceBuilder<T> WithAzureWorkloadIdentity<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource>? identity = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (identity is null)
        {
            // Auto-create an identity named after the resource
            var appBuilder = builder.ApplicationBuilder;
            var identityName = $"{builder.Resource.Name}-identity";
            var identityBuilder = appBuilder.AddAzureUserAssignedIdentity(identityName);
            identity = identityBuilder;
        }

        // Add both the standard AppIdentityAnnotation (for Azure service role assignments)
        // and the AKS-specific annotation (for ServiceAccount + federated credential generation)
        builder.WithAnnotation(new AppIdentityAnnotation(identity.Resource));
        builder.WithAnnotation(new AksWorkloadIdentityAnnotation(identity.Resource));

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
