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

```
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

```
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

Workload identity enables pods to authenticate to Azure services using federated credentials without storing secrets. This requires three things:
1. A user-assigned managed identity in Azure
2. A Kubernetes service account annotated with the identity's client ID
3. A federated credential linking the identity to the K8s service account via OIDC

**New types**:
```csharp
// Annotation to mark a compute resource for workload identity
public class AksWorkloadIdentityAnnotation(
    IAppIdentityResource identityResource,
    string? serviceAccountName = null) : IResourceAnnotation
{
    public IAppIdentityResource IdentityResource { get; } = identityResource;
    public string? ServiceAccountName { get; set; } = serviceAccountName;
}
```

**Extension method** (on compute resources):
```csharp
public static IResourceBuilder<T> WithAzureWorkloadIdentity<T>(
    this IResourceBuilder<T> builder,
    IResourceBuilder<AzureUserAssignedIdentityResource>? identity = null)
    where T : IResource
{
    // If no identity provided, auto-create one named "{resource}-identity"
    // Add AksWorkloadIdentityAnnotation
    // This will be picked up by the AKS infrastructure to:
    //   1. Create a federated credential (Bicep)
    //   2. Generate a ServiceAccount YAML with azure.workload.identity/client-id annotation
    //   3. Add the azure.workload.identity/use: "true" label to the pod spec
}
```

**Integration with KubernetesInfrastructure**:
When the AKS environment processes a resource with `AksWorkloadIdentityAnnotation`, it:
1. **Bicep side**: Creates a `FederatedIdentityCredential` resource:
   ```bicep
   resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
     parent: identity
     name: '${resourceName}-fedcred'
     properties: {
       issuer: aksCluster.properties.oidcIssuerProfile.issuerURL
       subject: 'system:serviceaccount:${namespace}:${serviceAccountName}'
       audiences: ['api://AzureADTokenExchange']
     }
   }
   ```
2. **Helm chart side**: Generates a ServiceAccount and patches the pod template:
   ```yaml
   apiVersion: v1
   kind: ServiceAccount
   metadata:
     name: {{ .Values.parameters.myapi.serviceAccountName }}
     annotations:
       azure.workload.identity/client-id: {{ .Values.parameters.myapi.azureClientId }}
   ---
   # In the Deployment pod spec:
   spec:
     serviceAccountName: {{ .Values.parameters.myapi.serviceAccountName }}
     labels:
       azure.workload.identity/use: "true"
   ```

**Key design decision**: The federated credential Bicep resource needs the OIDC issuer URL from the AKS cluster output. This creates a dependency ordering:
- AKS cluster must be provisioned first
- Then federated credentials can reference its OIDC issuer URL
- This is handled naturally by Bicep's dependency graph

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

AKS needs a subnet for its nodes (and optionally pods with Azure CNI Overlay). This uses the existing `WithDelegatedSubnet<T>` pattern already established for Container Apps and other Azure compute resources.

**Design** (uses the existing generic extension from `Aspire.Hosting.Azure.Network`):

Since `AzureKubernetesEnvironmentResource` implements `IAzureDelegatedSubnetResource` with `DelegatedSubnetServiceName = "Microsoft.ContainerService/managedClusters"`, the existing `WithDelegatedSubnet<T>()` extension method works directly:

```csharp
// User code — uses the EXISTING WithDelegatedSubnet<T> from Aspire.Hosting.Azure.Network
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithDelegatedSubnet(aksSubnet);
```

The `ConfigureAksInfrastructure` callback reads the delegated subnet annotation and wires it into the `ManagedCluster` Bicep:
```bicep
resource aksCluster 'Microsoft.ContainerService/managedClusters@2024-02-01' = {
  properties: {
    networkProfile: {
      networkPlugin: 'azure'
      networkPolicy: 'calico'
      serviceCidr: '10.0.4.0/22'
      dnsServiceIP: '10.0.4.10'
    }
    agentPoolProfiles: [{
      vnetSubnetID: subnet.id
    }]
  }
}
```

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

```
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

## Implementation Phases

### Phase 1: Unified AKS Environment (Foundation)
- Create `Aspire.Hosting.Azure.Kubernetes` package with dependency on `Azure.Provisioning.Kubernetes`
- `AzureKubernetesEnvironmentResource` combining Azure provisioning + K8s compute environment
- `AddAzureKubernetesEnvironment()` entry point (calls `AddKubernetesInfrastructureCore` internally)
- `AzureKubernetesInfrastructure` eventing subscriber
- Basic cluster Bicep generation (version, SKU, default node pool)
- ACR auto-creation and AcrPull role assignment for kubelet identity
- Kubeconfig retrieval pipeline step
- `AsExisting()` support for bring-your-own AKS
- Helm config delegation (`WithHelm()`, `WithDashboard()`)

### Phase 2: Workload Identity
- `AksWorkloadIdentityAnnotation`
- `WithAzureWorkloadIdentity()` extension on compute resources
- Federated credential Bicep generation (using AKS OIDC issuer URL output)
- ServiceAccount YAML generation in Helm chart
- Pod label injection (`azure.workload.identity/use`)
- Integration with existing `AppIdentityAnnotation` / `IAppIdentityResource` pattern

### Phase 3: Networking
- `WithDelegatedSubnet()` — uses existing generic extension from `Aspire.Hosting.Azure.Network` (since resource implements `IAzureDelegatedSubnetResource`)
- Azure CNI network profile configuration
- `AsPrivateCluster()` for private API server
- Private DNS Zone link verification for backing service private endpoints

### Phase 4: Monitoring
- `WithAzureLogAnalyticsWorkspace()` — matches Container Apps naming convention
- `WithContainerInsights()` — AKS-specific addon with optional Log Analytics auto-create
- Container Insights addon profile
- Azure Monitor metrics profile
- Optional Application Insights OTLP integration
- Data collection rule configuration

### Phase 5: Network Perimeter
- NSP association support (`IAzureNspAssociationTarget` on AKS)
- Private DNS Zone auto-linking when backing services have private endpoints in same VNet
- Network policy integration

## Dependencies / Prerequisites

- `Azure.Provisioning.Kubernetes` NuGet package (v1.0.0-beta.3 — need to add to `Directory.Packages.props`)
- `Azure.Provisioning.ContainerRegistry` (already used, v1.1.0)
- `Azure.Provisioning.Network` (already used, v1.1.0-beta.2)
- `Azure.Provisioning.OperationalInsights` (already used, v1.1.0)
- `Azure.Provisioning.Roles` (already used, for identity/RBAC)
- `Aspire.Hosting.Kubernetes` (the generic K8s package, already in repo)

## Testing Strategy

- **Unit tests**: Bicep template generation verification (snapshot tests like existing K8s tests)
- **Integration tests**: Verify Helm chart output includes ServiceAccount, labels, etc.
- **E2E tests**: Provision AKS + deploy workloads (expensive, CI-gated)
- **Existing test patterns**: Follow `Aspire.Hosting.Kubernetes.Tests` structure

## Todos

1. **aks-package-setup**: Create `Aspire.Hosting.Azure.Kubernetes` project, csproj, dependencies (including `Azure.Provisioning.Kubernetes`), add to `Directory.Packages.props`
2. **aks-environment-resource**: Implement `AzureKubernetesEnvironmentResource` with Bicep provisioning via `Azure.Provisioning.Kubernetes.ManagedCluster`, ACR auto-creation, and inner `KubernetesEnvironmentResource`
3. **aks-extensions**: Implement `AddAzureKubernetesEnvironment()` entry point and configuration extensions (`WithVersion`, `WithSkuTier`, `WithNodePool`, `WithHelm`, `WithDashboard`, `WithContainerRegistry`)
4. **aks-infrastructure**: Implement `AzureKubernetesInfrastructure` eventing subscriber — process compute resources, add `DeploymentTargetAnnotation`, handle kubeconfig retrieval pipeline step
5. **workload-identity-annotation**: `AksWorkloadIdentityAnnotation` and `WithAzureWorkloadIdentity<T>()` extension method on compute resources. Auto-create identity if not provided.
6. **workload-identity-bicep**: Generate `FederatedIdentityCredential` Bicep resource linking managed identity to K8s service account via OIDC issuer URL output
7. **workload-identity-helm**: Generate ServiceAccount YAML with `azure.workload.identity/client-id` annotation. Add `azure.workload.identity/use` label to pod spec.
8. **vnet-integration**: `WithDelegatedSubnet()` — leverages existing `IAzureDelegatedSubnetResource` pattern. Azure CNI network profile. Subnet delegation for `Microsoft.ContainerService/managedClusters`.
9. **private-cluster**: `AsPrivateCluster()` extension. Sets `apiServerAccessProfile.enablePrivateCluster`. Requires delegated subnet.
10. **monitoring**: `WithAzureLogAnalyticsWorkspace()` (matches Container Apps naming) + `WithContainerInsights()` (AKS addon). Log Analytics auto-create, Azure Monitor metrics, data collection rules.
11. **nsp-support**: Implement `IAzureNspAssociationTarget` on AKS resource. Auto-link Private DNS Zones when backing services have private endpoints in same VNet.
12. **existing-cluster**: `AsExisting()` support for referencing pre-provisioned AKS clusters via `ExistingAzureResourceAnnotation` pattern.
13. **tests**: Unit tests (Bicep snapshot verification), integration tests (Helm chart output), E2E tests (provision + deploy).
