// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Pipeline step types used for push/deploy dependency wiring
#pragma warning disable ASPIREAZURE001 // AzureEnvironmentResource.ProvisionInfrastructureStepName for pipeline ordering
#pragma warning disable ASPIREAZURE003 // AzureSubnetResource used in WithSubnet extensions

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.ContainerService;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
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
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an Azure Kubernetes Service environment resource")]
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

        // Create the inner KubernetesEnvironmentResource via the public API.
        // This registers the Kubernetes infrastructure pipeline step, creates the
        // resource with Helm chart name/dashboard, adds it to the model, and sets up
        // the default Helm deployment engine.
        var k8sEnvBuilder = builder.AddKubernetesEnvironment($"{name}-k8s");

        // Scope the Helm chart name to this AKS environment to avoid
        // conflicts when multiple environments deploy to the same cluster
        // or when re-deploying with different environment names.
        k8sEnvBuilder.Resource.HelmChartName = $"{builder.Environment.ApplicationName}-{name}".ToHelmChartName();

        // Create the unified AKS environment resource
        var resource = new AzureKubernetesEnvironmentResource(name, ConfigureAksInfrastructure);
        resource.KubernetesEnvironment = k8sEnvBuilder.Resource;

        // Set the parent so KubernetesInfrastructure matches resources that use
        // WithComputeEnvironment(aksEnv) — the inner K8s env checks both itself
        // and its parent when filtering compute resources.
        k8sEnvBuilder.Resource.OwningComputeEnvironment = resource;

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        // Auto-create a default Azure Container Registry for image push/pull.
        // Wire it to the inner K8s environment immediately so the inner Kubernetes
        // env's prepare-deployment-targets step can discover it as the container
        // registry for compute resources.
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
    /// Adds a node pool to the AKS cluster.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The name of the node pool.</param>
    /// <param name="vmSize">The VM size for nodes. Defaults to <c>Standard_D2s_v5</c> if not specified.</param>
    /// <param name="minCount">The minimum node count for autoscaling. Defaults to 1.</param>
    /// <param name="maxCount">The maximum node count for autoscaling. Defaults to 3.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AksNodePoolResource}"/> for the new node pool.</returns>
    /// <remarks>
    /// The returned node pool resource can be passed to
    /// <see cref="KubernetesEnvironmentExtensions.WithNodePool{T}"/> on compute resources to schedule workloads on this pool.
    /// </remarks>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    ///
    /// // With defaults (Standard_D2s_v5, 1-3 nodes)
    /// var pool = aks.AddNodePool("workload");
    ///
    /// // With explicit VM size and scaling
    /// var gpuPool = aks.AddNodePool("gpu", "Standard_NC6s_v3", 0, 5);
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a node pool to the AKS cluster")]
    public static IResourceBuilder<AksNodePoolResource> AddNodePool(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name,
        string vmSize = "Standard_D2s_v5",
        int minCount = 1,
        int maxCount = 3)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(vmSize);
        ArgumentOutOfRangeException.ThrowIfNegative(minCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCount, maxCount);

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
    /// Replaces the default system node pool with a customized configuration.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="vmSize">The VM size for system pool nodes. Defaults to <c>Standard_D2s_v5</c> if not specified.</param>
    /// <param name="minCount">The minimum node count for autoscaling. Defaults to 1.</param>
    /// <param name="maxCount">The maximum node count for autoscaling. Defaults to 3.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    /// <remarks>
    /// Every AKS cluster requires exactly one system node pool for hosting system pods.
    /// By default, the system pool uses <c>Standard_D2s_v5</c>. Use this method to change
    /// the VM size when the default SKU is not available in your subscription or region.
    /// Calling this method multiple times replaces the previous system pool configuration.
    /// </remarks>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks")
    ///     .WithSystemNodePool("Standard_B2s");
    ///
    /// // With explicit scaling
    /// var aks2 = builder.AddAzureKubernetesEnvironment("aks2")
    ///     .WithSystemNodePool("Standard_B2s", minCount: 2, maxCount: 5);
    /// </code>
    /// </example>
    [AspireExport(Description = "Replaces the default system node pool with a customized configuration")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithSystemNodePool(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        string vmSize = "Standard_D2s_v5",
        int minCount = 1,
        int maxCount = 3)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(vmSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(minCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCount, maxCount);

        // Remove existing system pool(s) and replace with the new configuration
        builder.Resource.NodePools.RemoveAll(p => p.Mode is AksNodePoolMode.System);
        builder.Resource.NodePools.Insert(0, new AksNodePoolConfig("system", vmSize, minCount, maxCount, AksNodePoolMode.System));

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
    [AspireExport(Description = "Configures the AKS cluster to use a VNet subnet")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithSubnet(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureSubnetResource> subnet)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subnet);

        builder.WithAnnotation(new AksSubnetAnnotation(subnet.Resource.Id), ResourceAnnotationMutationBehavior.Replace);
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
    /// var gpuPool = aks.AddNodePool("gpu", AksNodeVmSizes.StandardNCSv3.StandardNC6sV3, 0, 5)
    ///     .WithSubnet(gpuSubnet);
    /// </code>
    /// </example>
    [AspireExport(Description = "Configures an AKS node pool to use a specific VNet subnet")]
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
    [AspireExport(Description = "Configures the AKS environment to use a specific container registry")]
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

        // Remove any stale container registry annotations from the inner K8s environment
        // before adding the new one (the default ACR annotation was added during
        // AddAzureKubernetesEnvironment and now references a removed resource).
        var staleAnnotations = builder.Resource.KubernetesEnvironment.Annotations
            .OfType<ContainerRegistryReferenceAnnotation>().ToList();
        foreach (var old in staleAnnotations)
        {
            builder.Resource.KubernetesEnvironment.Annotations.Remove(old);
        }

        builder.Resource.KubernetesEnvironment.Annotations.Add(
            new ContainerRegistryReferenceAnnotation(registry.Resource));

        // Update the acrName parameter to reference the explicit registry's output
        // (replaces the default ACR reference set during AddAzureKubernetesEnvironment)
        builder.Resource.Parameters["acrName"] = registry.Resource.NameOutputReference;

        return builder;
    }

    /// <summary>
    /// Enables or disables workload identity on the AKS environment, allowing pods to authenticate
    /// to Azure services using federated credentials.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="enabled"><c>true</c> to enable workload identity (the default); <c>false</c> to disable it.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    /// <remarks>
    /// This ensures the AKS cluster is configured with OIDC issuer and workload identity enabled.
    /// Workload identity is automatically wired when compute resources have an <see cref="AppIdentityAnnotation"/>,
    /// which is added by <c>WithAzureUserAssignedIdentity</c> or auto-created by <c>AzureResourcePreparer</c>.
    /// </remarks>
    [AspireExport(Description = "Enables workload identity on the AKS cluster")]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithWorkloadIdentity(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.OidcIssuerEnabled = enabled;
        builder.Resource.WorkloadIdentityEnabled = enabled;
        return builder;
    }

    private static void ConfigureAksInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var aksResource = (AzureKubernetesEnvironmentResource)infrastructure.AspireResource;

        var skuTier = aksResource.SkuTier switch
        {
            AksSkuTier.Free => ManagedClusterSkuTier.Free,
            AksSkuTier.Standard => ManagedClusterSkuTier.Standard,
            AksSkuTier.Premium => ManagedClusterSkuTier.Premium,
            _ => ManagedClusterSkuTier.Free
        };

        // Create the AKS managed cluster
        var aks = new ContainerServiceManagedCluster(aksResource.GetBicepIdentifier())
        {
            ClusterIdentity = new ManagedClusterIdentity
            {
                IdentityType = ManagedServiceIdentityType.SystemAssigned
            },
            Sku = new ManagedClusterSku
            {
                Name = ManagedClusterSkuName.Base,
                Tier = skuTier
            },
            DnsPrefix = $"{aksResource.Name}-dns",
            Tags = { { "aspire-resource-name", aksResource.Name } }
        };

        if (aksResource.KubernetesVersion is not null)
        {
            aks.KubernetesVersion = aksResource.KubernetesVersion;
        }

        // Agent pool profiles
        var hasDefaultSubnet = aksResource.TryGetLastAnnotation<AksSubnetAnnotation>(out var subnetAnnotation);
        ProvisioningParameter? defaultSubnetParam = null;

        if (hasDefaultSubnet)
        {
            defaultSubnetParam = new ProvisioningParameter("subnetId", typeof(string));
            infrastructure.Add(defaultSubnetParam);
            aksResource.Parameters["subnetId"] = subnetAnnotation!.SubnetId;
        }

        // Per-pool subnet parameters
        var poolSubnetParams = new Dictionary<string, ProvisioningParameter>();
        foreach (var (poolName, poolSubnetRef) in aksResource.NodePoolSubnets)
        {
            var paramName = $"subnetId_{poolName}";
            var param = new ProvisioningParameter(paramName, typeof(string));
            infrastructure.Add(param);
            poolSubnetParams[poolName] = param;
            aksResource.Parameters[paramName] = poolSubnetRef;
        }

        foreach (var pool in aksResource.NodePools)
        {
            var mode = pool.Mode switch
            {
                AksNodePoolMode.System => AgentPoolMode.System,
                AksNodePoolMode.User => AgentPoolMode.User,
                _ => AgentPoolMode.User
            };

            var agentPool = new ManagedClusterAgentPoolProfile
            {
                Name = pool.Name,
                VmSize = pool.VmSize,
                MinCount = pool.MinCount,
                MaxCount = pool.MaxCount,
                Count = pool.MinCount,
                IsAutoScalingEnabled = true,
                Mode = mode,
                OSType = ContainerServiceOSType.Linux,
            };

            // Per-pool subnet override, else environment default
            if (poolSubnetParams.TryGetValue(pool.Name, out var poolSubnetParam))
            {
                agentPool.VnetSubnetId = poolSubnetParam;
            }
            else if (defaultSubnetParam is not null)
            {
                agentPool.VnetSubnetId = defaultSubnetParam;
            }

            aks.AgentPoolProfiles.Add(agentPool);
        }

        // OIDC issuer
        if (aksResource.OidcIssuerEnabled)
        {
            aks.OidcIssuerProfile = new ManagedClusterOidcIssuerProfile
            {
                IsEnabled = true
            };
        }

        // Workload identity
        if (aksResource.WorkloadIdentityEnabled)
        {
            aks.SecurityProfile = new ManagedClusterSecurityProfile
            {
                IsWorkloadIdentityEnabled = true
            };
        }

        // Private cluster
        if (aksResource.IsPrivateCluster)
        {
            aks.ApiServerAccessProfile = new ManagedClusterApiServerAccessProfile
            {
                IsPrivateClusterEnabled = true
            };
        }

        // Network profile
        var hasSubnetConfig = hasDefaultSubnet || aksResource.NodePoolSubnets.Count > 0;
        if (aksResource.NetworkProfile is not null)
        {
            aks.NetworkProfile = new ContainerServiceNetworkProfile
            {
                NetworkPlugin = aksResource.NetworkProfile.NetworkPlugin switch
                {
                    "azure" => ContainerServiceNetworkPlugin.Azure,
                    "kubenet" => ContainerServiceNetworkPlugin.Kubenet,
                    _ => ContainerServiceNetworkPlugin.Azure
                },
                ServiceCidr = aksResource.NetworkProfile.ServiceCidr,
                DnsServiceIP = aksResource.NetworkProfile.DnsServiceIP
            };
            if (aksResource.NetworkProfile.NetworkPolicy is not null)
            {
                aks.NetworkProfile.NetworkPolicy = aksResource.NetworkProfile.NetworkPolicy switch
                {
                    "calico" => ContainerServiceNetworkPolicy.Calico,
                    "azure" => ContainerServiceNetworkPolicy.Azure,
                    _ => ContainerServiceNetworkPolicy.Calico
                };
            }
        }
        else if (hasSubnetConfig)
        {
            aks.NetworkProfile = new ContainerServiceNetworkProfile
            {
                NetworkPlugin = ContainerServiceNetworkPlugin.Azure,
            };
        }

        infrastructure.Add(aks);

        // ACR pull role assignment for kubelet identity
        if (aksResource.DefaultContainerRegistry is not null || aksResource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out _))
        {
            var acrNameParam = new ProvisioningParameter("acrName", typeof(string));
            infrastructure.Add(acrNameParam);

            var acr = ContainerRegistryService.FromExisting("acr");
            acr.Name = acrNameParam;
            infrastructure.Add(acr);

            // AcrPull role: 7f951dda-4ed3-4680-a7ca-43fe172d538d
            var acrPullRoleId = BicepFunction.GetSubscriptionResourceId(
                "Microsoft.Authorization/roleDefinitions",
                "7f951dda-4ed3-4680-a7ca-43fe172d538d");

            // Access kubelet identity objectId via property path
            var kubeletObjectId = new MemberExpression(
                new MemberExpression(
                    new MemberExpression(
                        new MemberExpression(
                            new IdentifierExpression(aks.BicepIdentifier),
                            "properties"),
                        "identityProfile"),
                    "kubeletidentity"),
                "objectId");

            var roleAssignment = new RoleAssignment("acrPullRole")
            {
                Name = BicepFunction.CreateGuid(acr.Id, aks.Id, acrPullRoleId),
                Scope = new IdentifierExpression(acr.BicepIdentifier),
                RoleDefinitionId = acrPullRoleId,
                PrincipalId = kubeletObjectId,
                PrincipalType = RoleManagementPrincipalType.ServicePrincipal
            };
            infrastructure.Add(roleAssignment);
        }

        // Outputs
        infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = aks.Id });
        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = aks.Name });

        // OIDC issuer URL and kubelet identity require property path expressions
        var aksId = new IdentifierExpression(aks.BicepIdentifier);
        infrastructure.Add(new ProvisioningOutput("clusterFqdn", typeof(string))
        {
            Value = new MemberExpression(new MemberExpression(aksId, "properties"), "fqdn")
        });
        // OIDC issuer URL and kubelet identity outputs are only valid when the
        // corresponding features are enabled on the cluster.
        if (aksResource.OidcIssuerEnabled)
        {
            infrastructure.Add(new ProvisioningOutput("oidcIssuerUrl", typeof(string))
            {
                Value = new MemberExpression(
                    new MemberExpression(new MemberExpression(aksId, "properties"), "oidcIssuerProfile"),
                    "issuerURL")
            });
        }

        infrastructure.Add(new ProvisioningOutput("kubeletIdentityObjectId", typeof(string))
        {
            Value = new MemberExpression(
                new MemberExpression(
                    new MemberExpression(new MemberExpression(aksId, "properties"), "identityProfile"),
                    "kubeletidentity"),
                "objectId")
        });
        infrastructure.Add(new ProvisioningOutput("nodeResourceGroup", typeof(string))
        {
            Value = new MemberExpression(new MemberExpression(aksId, "properties"), "nodeResourceGroup")
        });

        // Federated identity credentials for workload identity
        // Resolve the K8s namespace for the service account subject.
        // If not explicitly configured, defaults to "default".
        var k8sNamespace = "default";
        if (aksResource.KubernetesEnvironment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            // Use the namespace expression's format string as the literal value.
            // Dynamic (parameter-based) namespaces are not supported for federated
            // credentials since Azure AD needs a fixed subject at provision time.
            var nsFormat = nsAnnotation.Namespace.Format;
            if (!string.IsNullOrEmpty(nsFormat) && !nsFormat.Contains('{'))
            {
                k8sNamespace = nsFormat;
            }
        }

        foreach (var (resourceName, identityResource) in aksResource.WorkloadIdentities)
        {
            var saName = $"{resourceName}-sa";
            var sanitizedName = Infrastructure.NormalizeBicepIdentifier(resourceName);
            var identityParamName = $"identityName_{sanitizedName}";

            var identityNameParam = new ProvisioningParameter(identityParamName, typeof(string));
            infrastructure.Add(identityNameParam);
            aksResource.Parameters[identityParamName] = identityResource.PrincipalName;

            var existingIdentity = UserAssignedIdentity.FromExisting($"identity_{sanitizedName}");
            existingIdentity.Name = identityNameParam;
            infrastructure.Add(existingIdentity);

            var fedCred = new FederatedIdentityCredential($"fedcred_{sanitizedName}")
            {
                Parent = existingIdentity,
                Name = $"{resourceName}-fedcred",
                IssuerUri = new MemberExpression(
                    new MemberExpression(
                        new MemberExpression(new IdentifierExpression(aks.BicepIdentifier), "properties"),
                        "oidcIssuerProfile"),
                    "issuerURL"),
                Subject = $"system:serviceaccount:{k8sNamespace}:{saName}",
                Audiences = { "api://AzureADTokenExchange" }
            };
            infrastructure.Add(fedCred);
        }
    }
}
