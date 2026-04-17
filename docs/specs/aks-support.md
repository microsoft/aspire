# AKS Support in Aspire — Implementation Spec

## Problem Statement

Aspire's `Aspire.Hosting.Kubernetes` package currently supports end-to-end deployment to any conformant Kubernetes cluster (including AKS) via Helm charts. However, the support is **generic Kubernetes** — it has no awareness of Azure-specific capabilities. Users who want to deploy to AKS must manually provision the cluster, configure workload identity, set up monitoring, and manage networking outside of Aspire.

The goal is to create a first-class AKS experience in Aspire that supports:
- **Provisioning the AKS cluster itself** via Azure.Provisioning
- **Workload identity** (Azure AD federated credentials for pods)
- **Azure Monitor integration** (Container Insights, Log Analytics, managed Prometheus/Grafana)
- **VNet integration** (subnet delegation, private clusters)
- **Network perimeter support** (NSP, private endpoints for backing Azure services)

## Current State Analysis

### Kubernetes Publishing (Aspire.Hosting.Kubernetes)
- **Helm-chart based** deployment model with 5-step pipeline: Publish → Prepare → Deploy → Summary → Uninstall
- `KubernetesEnvironmentResource` is the root compute environment
- `KubernetesResource` wraps each Aspire resource into Deployment/Service/ConfigMap/Secret YAML
- `HelmDeploymentEngine` executes `helm upgrade --install`
- No Azure awareness — works with any kubeconfig-accessible cluster
- Identity support: ❌ None
- Networking: Basic K8s Service/Ingress only
- Monitoring: OTLP to Aspire Dashboard only

### Azure Provisioning Patterns (established)
- `AzureProvisioningResource` base class → generates Bicep via `Azure.Provisioning` SDK
- `AzureResourceInfrastructure` builder → `CreateExistingOrNewProvisionableResource<T>()` factory
- `BicepOutputReference` for cross-resource wiring
- `AppIdentityAnnotation` + `IAppIdentityResource` for managed identity attachment
- Role assignments via `AddRoleAssignments()` / `IAddRoleAssignmentsContext`

### Azure Container Apps (reference pattern)
- `AzureContainerAppEnvironmentResource` : `AzureProvisioningResource`, `IAzureComputeEnvironmentResource`
- Implements `IAzureContainerRegistry`, `IAzureDelegatedSubnetResource`
- Auto-creates Container Registry, Log Analytics, managed identity
- Subscribes to `BeforeStartEvent` → creates ContainerApp per compute resource → adds `DeploymentTargetAnnotation`

### Azure Networking (established)
- VNet, Subnet, NSG, NAT Gateway, Private DNS Zone, Private Endpoint, Public IP resources
- `IAzurePrivateEndpointTarget` interface (implemented by Storage, SQL, etc.)
- `IAzureNspAssociationTarget` for network security perimeters
- `DelegatedSubnetAnnotation` for service delegation
- `PrivateEndpointTargetAnnotation` to deny public access

### Azure Identity (established)
- `AzureUserAssignedIdentityResource` with Id, ClientId, PrincipalId outputs
- `AppIdentityAnnotation` attaches identity to compute resources
- Container Apps sets `AZURE_CLIENT_ID` + `AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential`
- **No workload identity or federated credential support** exists today

### Azure Monitoring (established)
- `AzureLogAnalyticsWorkspaceResource` via `Azure.Provisioning.OperationalInsights`
- `AzureApplicationInsightsResource` via `Azure.Provisioning.ApplicationInsights`
- Container Apps links Log Analytics workspace to environment

## Proposed Architecture

### New Package: `Aspire.Hosting.Azure.Kubernetes`

This package provides a unified `AddAzureKubernetesEnvironment()` entry point that internally invokes `AddKubernetesEnvironment()` (from the generic K8s package) and layers on AKS-specific Azure provisioning. This mirrors the established pattern of `AddAzureContainerAppEnvironment()` which internally sets up the Container Apps infrastructure.

```text
Aspire.Hosting.Azure.Kubernetes
├── depends on: Aspire.Hosting.Kubernetes
├── depends on: Aspire.Hosting.Azure
├── depends on: Azure.Provisioning.Kubernetes (v1.0.0-beta.3)
├── depends on: Azure.Provisioning.Roles (for federated credentials)
├── depends on: Azure.Provisioning.Network (for VNet integration)
└── depends on: Azure.Provisioning.OperationalInsights (for monitoring)
```

### Design Principle: Unified Environment Resource

Just as `AddAzureContainerAppEnvironment("aca")` creates a single resource that is both the Azure provisioning target AND the compute environment, `AddAzureKubernetesEnvironment("aks")` creates a single `AzureKubernetesEnvironmentResource` that:
1. Extends `AzureProvisioningResource` (generates Bicep for AKS cluster + supporting resources)
2. Implements `IAzureComputeEnvironmentResource` (serves as the compute target)
3. Internally creates and manages a `KubernetesEnvironmentResource` for Helm-based deployment
4. Registers the `KubernetesInfrastructure` eventing subscriber (same as `AddKubernetesEnvironment`)

### Integration Points

```text
┌─────────────────────────────────────────────────────────────┐
│                     User's AppHost                          │
│                                                             │
│  var aks = builder.AddAzureKubernetesEnvironment("aks")     │
│      .WithDelegatedSubnet(subnet)                           │
│      .WithAzureLogAnalyticsWorkspace(logAnalytics)          │
│      .WithWorkloadIdentity()                                │
│      .WithVersion("1.30")                                   │
│      .WithHelm(...)           ← from K8s environment        │
│      .WithDashboard();        ← from K8s environment        │
│                                                             │
│  var db = builder.AddAzureSqlServer("sql")                  │
│      .WithPrivateEndpoint(subnet);  ← existing pattern      │
│                                                             │
│  builder.AddProject<MyApi>()                                │
│      .WithReference(db)                                     │
│      .WithAzureWorkloadIdentity(identity);  ← new           │
└─────────────────────────────────────────────────────────────┘
```

## Detailed Design

### 1. Unified AKS Environment Resource

**New resource**: `AzureKubernetesEnvironmentResource`

This is the single entry point — analogous to `AzureContainerAppEnvironmentResource`. It extends `AzureProvisioningResource` to generate Bicep for the AKS cluster and supporting infrastructure, while also serving as the compute environment by internally delegating to `KubernetesEnvironmentResource` for Helm-based deployment.

```csharp
public class AzureKubernetesEnvironmentResource(
    string name,
    Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure),
      IAzureComputeEnvironmentResource,
      IAzureContainerRegistry,        // For ACR integration
      IAzureDelegatedSubnetResource,  // For VNet integration
      IAzureNspAssociationTarget      // For NSP association
{
    // The underlying generic K8s environment (created internally)
    internal KubernetesEnvironmentResource KubernetesEnvironment { get; set; } = default!;

    // AKS cluster outputs
    public BicepOutputReference Id => new("id", this);
    public BicepOutputReference ClusterFqdn => new("clusterFqdn", this);
    public BicepOutputReference OidcIssuerUrl => new("oidcIssuerUrl", this);
    public BicepOutputReference KubeletIdentityObjectId => new("kubeletIdentityObjectId", this);
    public BicepOutputReference NodeResourceGroup => new("nodeResourceGroup", this);
    public BicepOutputReference NameOutputReference => new("name", this);

    // ACR outputs (like AzureContainerAppEnvironmentResource)
    internal BicepOutputReference ContainerRegistryName => new("AZURE_CONTAINER_REGISTRY_NAME", this);
    internal BicepOutputReference ContainerRegistryUrl => new("AZURE_CONTAINER_REGISTRY_ENDPOINT", this);
    internal BicepOutputReference ContainerRegistryManagedIdentityId 
        => new("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID", this);

    // Service delegation
    string IAzureDelegatedSubnetResource.DelegatedSubnetServiceName 
        => "Microsoft.ContainerService/managedClusters";

    // Configuration
    internal string? KubernetesVersion { get; set; }
    internal AksSkuTier SkuTier { get; set; } = AksSkuTier.Free;
    internal bool OidcIssuerEnabled { get; set; } = true;
    internal bool WorkloadIdentityEnabled { get; set; } = true;
    internal AzureContainerRegistryResource? DefaultContainerRegistry { get; set; }
    internal AzureLogAnalyticsWorkspaceResource? LogAnalyticsWorkspace { get; set; }

    // Node pool configuration
    internal List<AksNodePoolConfig> NodePools { get; } = [
        new AksNodePoolConfig("system", "Standard_D4s_v5", 1, 3, AksNodePoolMode.System)
    ];

    // Networking
    internal AksNetworkProfile? NetworkProfile { get; set; }
    internal AzureSubnetResource? SubnetResource { get; set; }
    internal bool IsPrivateCluster { get; set; }
}
```

**Entry point extension** (mirrors `AddAzureContainerAppEnvironment`):
```csharp
public static class AzureKubernetesEnvironmentExtensions
{
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> AddAzureKubernetesEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        // 1. Set up Azure provisioning infrastructure
        builder.AddAzureProvisioning();
        builder.Services.Configure<AzureProvisioningOptions>(
            o => o.SupportsTargetedRoleAssignments = true);

        // 2. Register the AKS-specific infrastructure eventing subscriber
        builder.Services.TryAddEventingSubscriber<AzureKubernetesInfrastructure>();

        // 3. Also register the generic K8s infrastructure (for Helm chart generation)
        builder.AddKubernetesInfrastructureCore();

        // 4. Create the unified environment resource
        var resource = new AzureKubernetesEnvironmentResource(name, ConfigureAksInfrastructure);

        // 5. Create the inner KubernetesEnvironmentResource (for Helm deployment)
        resource.KubernetesEnvironment = new KubernetesEnvironmentResource($"{name}-k8s")
        {
            HelmChartName = builder.Environment.ApplicationName.ToHelmChartName(),
            Dashboard = builder.CreateDashboard($"{name}-dashboard")
        };

        // 6. Auto-create ACR (like Container Apps does)
        var acr = CreateDefaultContainerRegistry(builder, name);
        resource.DefaultContainerRegistry = acr;

        return builder.AddResource(resource);
    }

    // Configuration extensions
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithVersion(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder, string version);
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithSkuTier(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder, AksSkuTier tier);
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithNodePool(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        string name, string vmSize, int minCount, int maxCount,
        AksNodePoolMode mode = AksNodePoolMode.User);

    // Networking (matches existing pattern: WithDelegatedSubnet<T>)
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithDelegatedSubnet(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureSubnetResource> subnet);
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> AsPrivateCluster(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder);

    // Identity
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithWorkloadIdentity(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder);

    // Monitoring (matches existing pattern: WithAzureLogAnalyticsWorkspace)
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithAzureLogAnalyticsWorkspace(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource> workspaceBuilder);
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithContainerInsights(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource>? logAnalytics = null);

    // Container Registry
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithContainerRegistry(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<AzureContainerRegistryResource> registry);

    // Helm configuration (delegates to inner KubernetesEnvironmentResource)
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithHelm(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        Action<HelmChartOptions> configure);
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithDashboard(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder);
}
```

**`AzureKubernetesInfrastructure`** (eventing subscriber, mirrors `AzureContainerAppsInfrastructure`):
```csharp
internal sealed class AzureKubernetesInfrastructure(
    ILogger<AzureKubernetesInfrastructure> logger,
    DistributedApplicationExecutionContext executionContext)
    : IDistributedApplicationEventingSubscriber
{
    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken ct)
    {
        var aksEnvironments = @event.Model.Resources
            .OfType<AzureKubernetesEnvironmentResource>().ToArray();

        foreach (var environment in aksEnvironments)
        {
            foreach (var r in @event.Model.GetComputeResources())
            {
                var computeEnv = r.GetComputeEnvironment();
                if (computeEnv is not null && computeEnv != environment)
                    continue;

                // 1. Process workload identity annotations
                //    → Generate federated credentials in Bicep
                //    → Add ServiceAccount + labels to Helm chart

                // 2. Create KubernetesResource via inner environment
                //    (delegates to existing KubernetesInfrastructure)

                // 3. Add DeploymentTargetAnnotation
                r.Annotations.Add(new DeploymentTargetAnnotation(environment)
                {
                    ContainerRegistry = environment,
                    ComputeEnvironment = environment
                });
            }
        }
    }
}
```

**Bicep output**: The `ConfigureAksInfrastructure` callback uses `Azure.Provisioning.Kubernetes` to produce:
- `ManagedCluster` with system-assigned or user-assigned identity for the control plane
- OIDC issuer enabled (required for workload identity)
- Workload identity enabled on the cluster
- Azure CNI or Kubenet network profile (based on VNet configuration)
- Container Insights add-on profile (if monitoring configured)
- Node pools with autoscaler configuration
- ACR pull role assignment for kubelet identity
- Container Registry (auto-created or explicit)

### 2. Workload Identity Support

Workload identity enables pods to authenticate to Azure services using federated credentials without storing secrets. This is implemented by honoring the shared `AppIdentityAnnotation` from `Aspire.Hosting.Azure` — the same mechanism used by ACA and AppService.

**How it works**:
1. `AzureResourcePreparer` auto-creates a per-resource managed identity when a compute resource references Azure services (e.g., `WithReference(blobStorage)`)
2. It adds `AppIdentityAnnotation` with the identity to the resource
3. Users can override with `WithAzureUserAssignedIdentity(myIdentity)` to supply their own identity
4. `AzureKubernetesInfrastructure` detects `AppIdentityAnnotation` and generates:
   - A K8s `ServiceAccount` with `azure.workload.identity/client-id` annotation
   - `serviceAccountName` on the pod spec
   - `azure.workload.identity/use: "true"` pod label
   - Federated identity credential in AKS Bicep module

**User API** (same as ACA):
```csharp
// Automatic — identity auto-created when referencing Azure resources
builder.AddProject<MyApi>()
    .WithComputeEnvironment(aks)
    .WithReference(blobStorage);  // gets identity + workload identity + role assignments

// Explicit — bring your own identity
var identity = builder.AddAzureUserAssignedIdentity("api-identity");
builder.AddProject<MyApi>()
    .WithComputeEnvironment(aks)
    .WithAzureUserAssignedIdentity(identity);
```

**Generated Bicep** (federated credential):
```bicep
resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: '${resourceName}-fedcred'
  properties: {
    issuer: aksCluster.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:${namespace}:${resourceName}-sa'
    audiences: ['api://AzureADTokenExchange']
  }
}
```

**Generated Helm chart** (ServiceAccount + pod template):
```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: apiservice-sa
  annotations:
    azure.workload.identity/client-id: {{ .Values.parameters.apiservice.identityClientId }}
  labels:
    azure.workload.identity/use: "true"
---
# In the Deployment pod template:
spec:
  serviceAccountName: apiservice-sa
  template:
    metadata:
      labels:
        azure.workload.identity/use: "true"
```

### 3. Monitoring Integration

**Goal**: When monitoring is enabled on the AKS environment, provision:
- Container Insights (via AKS addon profile) with Log Analytics workspace
- Azure Monitor metrics profile (managed Prometheus)
- Optional: Azure Managed Grafana dashboard
- Optional: Application Insights for application-level telemetry

**Design** (matches `WithAzureLogAnalyticsWorkspace` pattern from Container Apps):
```csharp
// Option 1: Explicit workspace (matches Container Apps naming exactly)
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithAzureLogAnalyticsWorkspace(logAnalytics);

// Option 2: Enable Container Insights (auto-creates workspace if not provided)
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithContainerInsights();

// Option 3: Both — explicit workspace + Container Insights addon
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithContainerInsights(logAnalytics);
```

**Bicep additions**:
- `addonProfiles.omsagent.enabled = true` with Log Analytics workspace ID
- `azureMonitorProfile.metrics.enabled = true` for managed Prometheus
- Data collection rule for container insights
- Optional: `AzureMonitorWorkspaceResource` for managed Prometheus

**OTLP integration**: The existing Kubernetes publishing already injects `OTEL_EXPORTER_OTLP_ENDPOINT`. For AKS, we can optionally route OTLP to Application Insights via the connection string environment variable.

### 4. VNet Integration

AKS needs a subnet for its nodes. Unlike Container Apps, AKS does **not** use subnet delegation — it uses plain (non-delegated) subnets. The API is `WithSubnet()` (not `WithDelegatedSubnet()`).

**Design**:
```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
var defaultSubnet = vnet.AddSubnet("default", "10.0.0.0/22");
var gpuSubnet = vnet.AddSubnet("gpu-subnet", "10.0.4.0/24");

// Environment-level subnet (applies to all pools by default)
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithSubnet(defaultSubnet);

// Per-pool subnet override
var gpuPool = aks.AddNodePool("gpu", AksNodeVmSizes.GpuAccelerated.StandardNC6sV3, 0, 5)
    .WithSubnet(gpuSubnet);
```

**Bicep**: Environment-level subnet → `subnetId` parameter. Per-pool subnets → `subnetId_{poolName}` parameters. Each agent pool profile uses its own subnet if set, else the environment default.

**Network profile**: Azure CNI is auto-configured when any subnet is set.

**Private cluster support**:
```csharp
public static IResourceBuilder<AzureKubernetesEnvironmentResource> AsPrivateCluster(
    this IResourceBuilder<AzureKubernetesEnvironmentResource> builder)
{
    // Enable private cluster (API server behind private endpoint)
    // Requires a delegated subnet to be configured
    // Sets apiServerAccessProfile.enablePrivateCluster = true
}
```

### 5. Network Perimeter Support

AKS backing Azure services (SQL, Storage, Key Vault) should be accessible via private endpoints within the cluster's VNet.

**This largely uses existing infrastructure**:
```csharp
// User code in AppHost
var vnet = builder.AddAzureVirtualNetwork("vnet");
var aksSubnet = vnet.AddSubnet("aks-subnet", "10.0.0.0/22");
var peSubnet = vnet.AddSubnet("pe-subnet", "10.0.4.0/24");

var aks = builder.AddAzureKubernetesService("aks")
    .WithVirtualNetwork(aksSubnet);

var sql = builder.AddAzureSqlServer("sql")
    .WithPrivateEndpoint(peSubnet);  // existing pattern

// The SQL private endpoint is in the same VNet as AKS
// DNS resolution via Private DNS Zone (existing pattern) enables pod → SQL connectivity
```

**New consideration**: When AKS is configured with a VNet and backing services have private endpoints, the AKS infrastructure should verify or configure:
- Private DNS Zone links to the AKS VNet (so pods can resolve private endpoint DNS)
- This may need a new extension or automatic wiring

### 6. Deployment Pipeline Integration

Since `AzureKubernetesEnvironmentResource` unifies both Azure provisioning and K8s deployment, the pipeline is a superset of both:

```text
[Azure Provisioning Phase]          [Kubernetes Deployment Phase]
1. Generate Bicep (AKS + ACR +      4. Publish Helm chart
   identity + fedcreds)              5. Get kubeconfig from AKS (az aks get-credentials)
2. Deploy Bicep via azd              6. Push images to ACR
3. Capture outputs (OIDC URL,        7. Prepare Helm values (resolve AKS outputs)
   ACR endpoint, etc.)               8. helm upgrade --install
                                     9. Print summary
                                    10. (Optional) Uninstall
```

The Azure provisioning happens first (via `AzureEnvironmentResource` / `AzureProvisioner`), then the Kubernetes Helm deployment pipeline steps execute against the provisioned cluster. The kubeconfig step bridges the two phases — it uses the AKS cluster name from Bicep outputs to call `az aks get-credentials`.

This is implemented by adding AKS-specific `DeploymentEngineStepsFactory` entries to the inner `KubernetesEnvironmentResource`:
```csharp
// In AddAzureKubernetesEnvironment, after AKS provisioning completes:
resource.KubernetesEnvironment.AddDeploymentEngineStep(
    "get-kubeconfig",
    async (context, ct) =>
    {
        // Use AKS outputs to fetch kubeconfig
        var clusterName = await resource.NameOutputReference.GetValueAsync(ct);
        var resourceGroup = await resource.ResourceGroupOutput.GetValueAsync(ct);
        // az aks get-credentials --resource-group {rg} --name {name}
    });
```

### 7. Container Registry Integration

AKS needs a container registry for application images. Options:
1. **Auto-create ACR** when AKS is added (like Container Apps does)
2. **Bring your own ACR** via `.WithContainerRegistry()`
3. **Use existing ACR** via `AsExisting()` pattern

```csharp
// Auto-create (default)
var aks = builder.AddAzureKubernetesService("aks");
// → auto-creates ACR, attaches AcrPull role to kubelet identity

// Explicit
var acr = builder.AddAzureContainerRegistry("acr");
var aks = builder.AddAzureKubernetesService("aks")
    .WithContainerRegistry(acr);
```

**Role assignment**: The AKS kubelet managed identity needs `AcrPull` role on the registry.

## Open Questions

1. **`Azure.Provisioning.Kubernetes` readiness**: The package is at v1.0.0-beta.3. We need to verify it has the types we need (`ManagedCluster`, `AgentPool`, `OidcIssuerProfile`, `WorkloadIdentity` flags, etc.) and assess stability risk.

2. **Existing cluster support**: Should we support `AsExisting()` for AKS (reference a pre-provisioned cluster)?
   - **Recommendation**: Yes, this is a common scenario. Use the established `ExistingAzureResourceAnnotation` pattern.

3. **Managed Grafana**: Should `WithMonitoring()` also provision Azure Managed Grafana?
   - Could be a separate `.WithGrafana()` extension to keep it opt-in.

4. **Ingress controller**: Should Aspire configure an ingress controller (NGINX, Traefik, or Application Gateway Ingress Controller)?
   - Application Gateway Ingress Controller (AGIC) would be the Azure-native choice.
   - Could be opt-in via `.WithApplicationGatewayIngress()`.

5. **DNS integration**: Should external endpoints auto-configure Azure DNS zones?
   - Probably out of scope for v1.

6. **Deployment mode**: For publish, should AKS support work with `aspire publish` only, or also `aspire run` (local dev with AKS)?
   - Recommendation: `aspire publish` first. Local dev uses the generic K8s environment with local/kind clusters.

7. **Multi-cluster**: Should we support multiple AKS environments in one AppHost?
   - The `KubernetesEnvironmentResource` model already supports this conceptually.

8. **Helm config delegation**: How cleanly can `WithHelm()` / `WithDashboard()` be forwarded from `AzureKubernetesEnvironmentResource` to the inner `KubernetesEnvironmentResource`? Should the inner resource be exposed or kept fully internal?

## Implementation Status

### ✅ Implemented

#### Phase 1: Unified AKS Environment (Foundation)
- ✅ `Aspire.Hosting.Azure.Kubernetes` package created
- ✅ `AzureKubernetesEnvironmentResource` — extends `AzureProvisioningResource`, implements `IAzureComputeEnvironmentResource`, `IAzureNspAssociationTarget`
- ✅ `AddAzureKubernetesEnvironment()` entry point — calls `AddKubernetesEnvironment()` internally
- ✅ `AzureKubernetesInfrastructure` eventing subscriber
- ✅ Hand-crafted Bicep generation (not Azure.Provisioning SDK — `Azure.Provisioning.ContainerService` not in internal feeds)
- ✅ ACR auto-creation + AcrPull role assignment in Bicep
- ✅ Kubeconfig retrieval via `az aks get-credentials` to isolated temp file
- ✅ Multi-environment support (scoped Helm chart names, per-env kubeconfig)
- ✅ `WithContainerRegistry()`
- ❌ ~~`WithVersion()`, `WithSkuTier()`, `AsPrivateCluster()`~~ — **Removed** in initial sweep; use `ConfigureInfrastructure()` instead
- ✅ Push step dependency wiring for container image builds

#### Phase 2: Workload Identity
- ✅ Honors `AppIdentityAnnotation` from `Aspire.Hosting.Azure` (same mechanism as ACA/AppService)
- ✅ Auto-identity via `AzureResourcePreparer` when resources reference Azure services
- ✅ Override with `WithAzureUserAssignedIdentity(identity)` (standard API)
- ✅ ServiceAccount YAML generation with `azure.workload.identity/client-id` annotation
- ✅ Pod label `azure.workload.identity/use: "true"` on pod template
- ✅ `serviceAccountName` set on pod spec
- ✅ Federated identity credential Bicep generation per workload
- ✅ Identity `clientId` wired as deferred Helm value (resolved at deploy time)
- ✅ `ServiceAccountV1` resource added to `Aspire.Hosting.Kubernetes`
- ❌ ~~`AksWorkloadIdentityAnnotation`~~ — **Removed** (redundant with `AppIdentityAnnotation`)
- ❌ ~~`WithAzureWorkloadIdentity()`~~ — **Removed** (standard `WithAzureUserAssignedIdentity` works)

#### Phase 3: Networking
- ✅ `WithSubnet()` (NOT `WithDelegatedSubnet` — AKS doesn't support subnet delegation)
- ✅ Per-node-pool subnet support via `WithSubnet()` on `AksNodePoolResource`
- ✅ Azure CNI network profile auto-configured when subnet is set
- ✅ `AsPrivateCluster()` for private API server
- ❌ AKS does NOT implement `IAzureDelegatedSubnetResource` (intentionally — AKS uses plain subnets)

#### Node Pools (not in original spec)
- ✅ Base `KubernetesNodePoolResource` in `Aspire.Hosting.Kubernetes` (cloud-agnostic)
- ✅ `AksNodePoolResource` extends base with VM size, scaling, mode config
- ✅ `AddNodePool()` on both K8s and AKS environments
- ✅ `WithNodePool()` schedules workloads via `nodeSelector` on pod spec
- ✅ `AksNodeVmSizes` constants class (GeneralPurpose, ComputeOptimized, MemoryOptimized, GpuAccelerated, StorageOptimized, Burstable, Arm)
- ✅ `GenVmSizes.cs` tool + `update-azure-vm-sizes.yml` monthly workflow
- ✅ Default "workload" user pool auto-created if none configured

#### IValueProvider Resolution (not in original spec)
- ✅ Azure resource connection strings and endpoints resolved at deploy time
- ✅ Composite expressions (e.g., `Endpoint={storage.outputs.blobEndpoint};ContainerName=photos`) handled
- ✅ Phase 4 in HelmDeploymentEngine for generic `IValueProvider` resolution

### 🔲 Not Yet Implemented

#### Monitoring (Phase 4) — Bicep not emitted
- 🔲 `WithContainerInsights()` and `WithAzureLogAnalyticsWorkspace()` **exist as APIs** but the Bicep generation does NOT emit:
  - Container Insights addon profile (`addonProfiles.omsagent`)
  - Azure Monitor metrics profile (managed Prometheus)
  - Data collection rules
  - Application Insights OTLP integration

#### Helm/Dashboard delegation
- 🔲 `WithHelm()` and `WithDashboard()` are not exposed on `AzureKubernetesEnvironmentResource`
  - They work on the inner `KubernetesEnvironmentResource` but users can't access them from the AKS builder

#### AsExisting() support
- 🔲 `AsExisting()` for referencing pre-provisioned AKS clusters

#### Private DNS Zone auto-linking
- 🔲 When backing services have private endpoints in same VNet as AKS, Private DNS Zones should be auto-linked

#### IAzureContainerRegistry interface
- 🔲 AKS resource does not implement `IAzureContainerRegistry` (ACR outputs not exposed via standard interface)

#### Ingress controller
- 🔲 Application Gateway Ingress Controller (AGIC) or other ingress support

#### Managed Prometheus/Grafana
- 🔲 Azure Monitor workspace for managed Prometheus
- 🔲 Azure Managed Grafana provisioning

## Key Design Changes from Original Spec

1. **Bicep generation**: Uses hand-crafted `StringBuilder` via `GetBicepTemplateString()` override, NOT `Azure.Provisioning.ContainerService` SDK (package not available in internal NuGet feeds)
2. **Workload identity**: Uses shared `AppIdentityAnnotation` from `Aspire.Hosting.Azure`, not AKS-specific annotation. Same mechanism as ACA/AppService.
3. **Subnet integration**: `WithSubnet()` not `WithDelegatedSubnet()` — AKS uses plain subnets, not delegated ones
4. **Node pools**: First-class resources with `AddNodePool()` returning `IResourceBuilder<AksNodePoolResource>`, `WithNodePool()` for scheduling, per-pool subnets, `AksNodeVmSizes` constants
5. **Multi-environment**: Full support for multiple AKS environments with scoped chart names and isolated kubeconfigs

## Dependencies / Prerequisites

- ~~`Azure.Provisioning.Kubernetes`~~ — Not used (hand-crafted Bicep instead)
- `Azure.Provisioning.ContainerRegistry` (for ACR resource type reference)
- `Azure.Provisioning.OperationalInsights` (for Log Analytics workspace type)
- `Aspire.Hosting.Kubernetes` (the generic K8s package)
- `Aspire.Hosting.Azure` (for `AppIdentityAnnotation`, `AzureProvisioningResource`, etc.)
- `Aspire.Hosting.Azure.Network` (for subnet, VNet, NSP types)
- `Aspire.Hosting.Azure.ContainerRegistry` (for ACR auto-creation)

## Testing

- 31 AKS unit tests passing (extensions + infrastructure)
- 88 K8s base tests passing
- Manual E2E validation against live Azure clusters
