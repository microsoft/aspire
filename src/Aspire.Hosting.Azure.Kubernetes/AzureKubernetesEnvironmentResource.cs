// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Kubernetes Service (AKS) environment resource that provisions
/// an AKS cluster and serves as a compute environment for Kubernetes workloads.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure infrastructure.</param>
public class AzureKubernetesEnvironmentResource(
    string name,
    Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure),
      IAzureComputeEnvironmentResource,
      IAzureNspAssociationTarget
{

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
    /// Gets or sets the SKU tier for the AKS cluster.
    /// </summary>
    internal AksSkuTier SkuTier { get; set; } = AksSkuTier.Free;

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
        new AksNodePoolConfig("system", "Standard_D4s_v5", 1, 3, AksNodePoolMode.System)
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

    /// <inheritdoc />
    public override string GetBicepTemplateString()
    {
        return GenerateAksBicep();
    }

    /// <inheritdoc />
    public override BicepTemplateFile GetBicepTemplateFile(string? directory = null, bool deleteTemporaryFileOnDispose = true)
    {
        var bicep = GenerateAksBicep();
        var dir = directory ?? Directory.CreateTempSubdirectory("aspire-aks").FullName;
        var filePath = Path.Combine(dir, Name + ".module.bicep");
        File.WriteAllText(filePath, bicep);
        return new BicepTemplateFile(filePath, directory is null && deleteTemporaryFileOnDispose);
    }

    private string GenerateAksBicep()
    {
        var sb = new StringBuilder();
        var id = this.GetBicepIdentifier();
        var skuTier = SkuTier switch
        {
            AksSkuTier.Free => "Free",
            AksSkuTier.Standard => "Standard",
            AksSkuTier.Premium => "Premium",
            _ => "Free"
        };

        sb.AppendLine("@description('The location for the resource(s) to be deployed.')");
        sb.AppendLine("param location string = resourceGroup().location");
        sb.AppendLine();

        // ACR parameter for role assignment
        if (DefaultContainerRegistry is not null || this.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out _))
        {
            sb.AppendLine("@description('The name of the Azure Container Registry for AcrPull role assignment.')");
            sb.AppendLine("param acrName string");
            sb.AppendLine();
        }

        // Subnet parameters for VNet integration
        // Environment-level subnet (default for all pools)
        var hasDefaultSubnet = this.TryGetLastAnnotation<AksSubnetAnnotation>(out var subnetAnnotation);
        if (hasDefaultSubnet)
        {
            Parameters["subnetId"] = subnetAnnotation!.SubnetId;
        }

        if (hasDefaultSubnet)
        {
            sb.AppendLine("@description('The default subnet ID for AKS node pool VNet integration.')");
            sb.AppendLine("param subnetId string");
            sb.AppendLine();
        }

        // Per-pool subnet overrides
        foreach (var (poolName, poolSubnetRef) in NodePoolSubnets)
        {
            var paramName = $"subnetId_{poolName}";
            Parameters[paramName] = poolSubnetRef;
            sb.Append("@description('The subnet ID for the ").Append(poolName).AppendLine(" node pool.')");
            sb.Append("param ").Append(paramName).AppendLine(" string");
            sb.AppendLine();
        }

        // AKS cluster resource
        sb.Append("resource ").Append(id).AppendLine(" 'Microsoft.ContainerService/managedClusters@2026-01-01' = {");
        sb.Append("  name: '").Append(Name).AppendLine("'");
        sb.AppendLine("  location: location");
        sb.AppendLine("  tags: {");
        sb.Append("    'aspire-resource-name': '").Append(Name).AppendLine("'");
        sb.AppendLine("  }");
        sb.AppendLine("  identity: {");
        sb.AppendLine("    type: 'SystemAssigned'");
        sb.AppendLine("  }");
        sb.AppendLine("  sku: {");
        sb.AppendLine("    name: 'Base'");
        sb.Append("    tier: '").Append(skuTier).AppendLine("'");
        sb.AppendLine("  }");
        sb.AppendLine("  properties: {");

        if (KubernetesVersion is not null)
        {
            sb.Append("    kubernetesVersion: '").Append(KubernetesVersion).AppendLine("'");
        }

        sb.Append("    dnsPrefix: '").Append(Name).AppendLine("-dns'");

        // Agent pool profiles
        sb.AppendLine("    agentPoolProfiles: [");
        foreach (var pool in NodePools)
        {
            var mode = pool.Mode switch
            {
                AksNodePoolMode.System => "System",
                AksNodePoolMode.User => "User",
                _ => "User"
            };
            sb.AppendLine("      {");
            sb.Append("        name: '").Append(pool.Name).AppendLine("'");
            sb.Append("        vmSize: '").Append(pool.VmSize).AppendLine("'");
            sb.Append("        minCount: ").AppendLine(pool.MinCount.ToString(CultureInfo.InvariantCulture));
            sb.Append("        maxCount: ").AppendLine(pool.MaxCount.ToString(CultureInfo.InvariantCulture));
            sb.Append("        count: ").AppendLine(pool.MinCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("        enableAutoScaling: true");
            sb.Append("        mode: '").Append(mode).AppendLine("'");
            sb.AppendLine("        osType: 'Linux'");
            // Use per-pool subnet if available, otherwise fall back to environment default
            if (NodePoolSubnets.ContainsKey(pool.Name))
            {
                sb.Append("        vnetSubnetID: subnetId_").AppendLine(pool.Name);
            }
            else if (hasDefaultSubnet)
            {
                sb.AppendLine("        vnetSubnetID: subnetId");
            }
            sb.AppendLine("      }");
        }
        sb.AppendLine("    ]");

        // OIDC issuer
        if (OidcIssuerEnabled)
        {
            sb.AppendLine("    oidcIssuerProfile: {");
            sb.AppendLine("      enabled: true");
            sb.AppendLine("    }");
        }

        // Workload identity
        if (WorkloadIdentityEnabled)
        {
            sb.AppendLine("    securityProfile: {");
            sb.AppendLine("      workloadIdentity: {");
            sb.AppendLine("        enabled: true");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
        }

        // Private cluster
        if (IsPrivateCluster)
        {
            sb.AppendLine("    apiServerAccessProfile: {");
            sb.AppendLine("      enablePrivateCluster: true");
            sb.AppendLine("    }");
        }

        // Network profile
        if (NetworkProfile is not null)
        {
            sb.AppendLine("    networkProfile: {");
            sb.Append("      networkPlugin: '").Append(NetworkProfile.NetworkPlugin).AppendLine("'");
            if (NetworkProfile.NetworkPolicy is not null)
            {
                sb.Append("      networkPolicy: '").Append(NetworkProfile.NetworkPolicy).AppendLine("'");
            }
            sb.Append("      serviceCidr: '").Append(NetworkProfile.ServiceCidr).AppendLine("'");
            sb.Append("      dnsServiceIP: '").Append(NetworkProfile.DnsServiceIP).AppendLine("'");
            sb.AppendLine("    }");
        }
        else if (hasDefaultSubnet || NodePoolSubnets.Count > 0)
        {
            // Default Azure CNI network profile when a subnet is delegated
            sb.AppendLine("    networkProfile: {");
            sb.AppendLine("      networkPlugin: 'azure'");
            sb.AppendLine("      serviceCidr: '10.0.0.0/16'");
            sb.AppendLine("      dnsServiceIP: '10.0.0.10'");
            sb.AppendLine("    }");
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ACR pull role assignment for kubelet identity
        if (DefaultContainerRegistry is not null || this.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out _))
        {
            sb.AppendLine("// Reference the existing ACR to grant pull access to AKS");
            sb.AppendLine("resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {");
            sb.AppendLine("  name: acrName");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("// AcrPull role assignment for the AKS kubelet managed identity");
            sb.AppendLine("resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {");
            sb.Append("  name: guid(acr.id, ").Append(id).AppendLine(".id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))");
            sb.AppendLine("  scope: acr");
            sb.AppendLine("  properties: {");
            sb.Append("    principalId: ").Append(id).AppendLine(".properties.identityProfile.kubeletidentity.objectId");
            sb.AppendLine("    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')");
            sb.AppendLine("    principalType: 'ServicePrincipal'");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Outputs
        sb.Append("output id string = ").Append(id).AppendLine(".id");
        sb.Append("output name string = ").Append(id).AppendLine(".name");
        sb.Append("output clusterFqdn string = ").Append(id).AppendLine(".properties.fqdn");
        sb.Append("output oidcIssuerUrl string = ").Append(id).AppendLine(".properties.oidcIssuerProfile.issuerURL");
        sb.Append("output kubeletIdentityObjectId string = ").Append(id).AppendLine(".properties.identityProfile.kubeletidentity.objectId");
        sb.Append("output nodeResourceGroup string = ").Append(id).AppendLine(".properties.nodeResourceGroup");

        // Federated identity credentials for workload identity
        foreach (var (resourceName, identityResource) in WorkloadIdentities)
        {
            var saName = $"{resourceName}-sa";
            var sanitizedName = SanitizeBicepIdentifier(resourceName);
            var fedCredId = $"fedcred_{sanitizedName}";
            var identityParamName = $"identityName_{sanitizedName}";

            // Add identity name as parameter — will be resolved from the identity resource
            Parameters[identityParamName] = identityResource.PrincipalName;

            sb.AppendLine();
            sb.Append("@description('The name of the managed identity for ").Append(resourceName).AppendLine(".')");
            sb.Append("param ").Append(identityParamName).AppendLine(" string");
            sb.AppendLine();

            // Reference existing identity
            sb.Append("resource identity_").Append(sanitizedName);
            sb.AppendLine(" 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {");
            sb.Append("  name: ").AppendLine(identityParamName);
            sb.AppendLine("}");
            sb.AppendLine();

            // Federated credential
            sb.Append("resource ").Append(fedCredId);
            sb.AppendLine(" 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {");
            sb.Append("  parent: identity_").AppendLine(sanitizedName);
            sb.Append("  name: '").Append(resourceName).AppendLine("-fedcred'");
            sb.AppendLine("  properties: {");
            sb.Append("    issuer: ").Append(id).AppendLine(".properties.oidcIssuerProfile.issuerURL");
            sb.Append("    subject: 'system:serviceaccount:default:").Append(saName).AppendLine("'");
            sb.AppendLine("    audiences: [");
            sb.AppendLine("      'api://AzureADTokenExchange'");
            sb.AppendLine("    ]");
            sb.AppendLine("  }");
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sanitizes a name for use as a Bicep identifier (alphanumeric + underscore only).
    /// </summary>
    private static string SanitizeBicepIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.ToString();
    }
}
