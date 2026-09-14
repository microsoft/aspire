# Aspire Deployment End-to-End Tests

This project contains end-to-end tests that deploy Aspire applications to real Azure infrastructure.  These tests verify that the complete deployment workflow works correctly, from project creation to live deployment and endpoint verification.

## Overview

These tests use the [Hex1b](https://github.com/hex1b/hex1b) terminal automation library to drive the Aspire CLI, similar to the CLI E2E tests. The key difference is that these tests actually deploy to Azure and verify the deployed applications work correctly.

## Azure Subscription Quota Requirements

The deployment tests require an Azure subscription with sufficient quota for the resources being deployed. Most scenarios, including the AKS (Azure Kubernetes Service) scenarios, deploy to `westus3`; a resource-specific test uses `eastus2`. Ensure the quotas below are available in the region noted for each section.

### Container Apps

| Resource | Quota Required | Current Setting | Notes |
|----------|---------------|-----------------|-------|
| Managed Environments | 150+ | 150 | Each test run creates a new environment. High quota allows concurrent runs and handles cleanup delays. |
| Standard Public IP Addresses (`Microsoft.Network`) | 150+ | 50 | Public Container Apps environments consume this regional quota. Request manually in `westus3`; Microsoft.Quota exposes the limit but rejects CLI create/update requests. |
| Container App Instances | Default | - | Standard quota is typically sufficient |

### App Service

| Resource | Quota Required | Current Setting | Notes |
|----------|---------------|-----------------|-------|
| PremiumV3 vCPUs | 10+ | TBD | App Service Plans use PremiumV3 tier (P0V3). Each deployment needs ~1 vCPU. |
| App Service Plans | 10+ | Default | Each deployment creates a new plan |

### AKS / Kubernetes node pools

The AKS scenarios deploy to `westus3`, where the subscription holds `Standard_D2as_v5` (DASv5) capacity. The CI workflow's quota self-healing (`QUOTA_TARGETS` in `.github/workflows/deployment-tests.yml`) requests the compute vCPU and managed-cluster quotas automatically:

| Resource | Region | Quota Required | Notes |
|----------|--------|----------------|-------|
| `StandardDASv5Family` vCPUs (`Microsoft.Compute`) | `westus3` | 200 (dedicated) | System and workload node pools use `Standard_D2as_v5`. Self-healed by the workflow. |
| Total Regional vCPUs (`Microsoft.Compute`, `cores`) | `westus3` | 200 (dedicated) | Azure enforces this regional total independently of the family quota, so node pools need headroom in both. Self-healed by the workflow. |
| Managed Clusters (`Microsoft.ContainerService`) | `westus3` | 20 | Each AKS test creates a cluster; headroom covers concurrent runs and cleanup lag. Self-healed by the workflow. |

One test intentionally uses another region for a resource-specific requirement:

- `AcaManagedRedisDeploymentTests` → `eastus2` (Azure Managed Redis availability-zone support).

### Container Registry

| Resource | Quota Required | Notes |
|----------|---------------|-------|
| Azure Container Registry | Default | Standard quota is typically sufficient |

### General

| Resource | Quota Required | Notes |
|----------|---------------|-------|
| Resource Groups | 100+ | Each test creates a unique resource group (e.g., `e2e-starter-12345678-1`) |
| Role Assignments | Default | Tests may create role assignments for managed identities, and `AzureRoleAssignmentRunModeTests` creates them for the ambient deployment principal |

### Requesting Quota Increases

To request quota increases:

1. Go to the [Azure Portal](https://portal.azure.com)
2. Navigate to **Subscriptions** → Select your subscription
3. Go to **Usage + quotas**
4. Filter by the resource type:
   - `Microsoft.App` for Container Apps
   - `Microsoft.Web` for App Service
   - `Microsoft.Compute` for AKS node pool vCPUs
   - `Microsoft.ContainerService` for AKS managed clusters
5. Select the quota to increase and click **Request increase**

Common quota increase requests:
- **Container Apps Managed Environments**: Request 150+ in westus3
- **Standard Public IP Addresses**: Request 150+ in westus3
- **App Service PremiumV3 vCPUs**: Request 10+ in westus3
- **AKS `StandardDASv5Family` vCPUs**: Request 200 (dedicated) in westus3
- **AKS Managed Clusters**: Request 20 in westus3

## Prerequisites

### For Local Development

1. **Linux environment** - Hex1b requires a Linux terminal (WSL2 works on Windows)
2. **Azure CLI** - Install and authenticate with `az login`
3. **Azure subscription** - You need access to an Azure subscription for deployments

### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION` | Yes | Azure subscription ID for test deployments |
| `ASPIRE_DEPLOYMENT_TEST_RG_PREFIX` | No | Prefix for resource group names (default: `aspire-e2e`) |
| `ASPIRE_DEPLOYMENT_TEST_ENABLE_CONNECTOR_NAMESPACE` | Connector Namespace test only | Set to `true` after the subscription is enrolled in the Connector Namespace preview |
| `ASPIRE_DEPLOYMENT_TEST_CONNECTOR_NAMESPACE_LOCATION` | Connector Namespace test only | Preview-enabled Azure region |
| `ASPIRE_DEPLOYMENT_TEST_CONNECTOR_NAMESPACE_PRINCIPAL_OBJECT_ID` | Connector Namespace test only | Object ID of a tenant user used to validate connection and MCP access policies |
| `ASPIRE_DEPLOYMENT_TEST_CONNECTOR_NAMESPACE_TENANT_ID` | Connector Namespace test only | Tenant ID containing the access-policy user |
| `AZURE_DEPLOYMENT_TEST_TENANT_ID` | CI only | Azure AD tenant ID for OIDC authentication |
| `AZURE_DEPLOYMENT_TEST_CLIENT_ID` | CI only | Azure AD app client ID for OIDC authentication |
| `AZURE_DEPLOYMENT_TEST_SUBSCRIPTION_ID` | CI only | Azure subscription ID (GitHub variable) |

### Local Setup

```bash
# Authenticate with Azure CLI
az login

# Set your subscription
export ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION="your-subscription-id"

# Optional: customize resource group prefix
export ASPIRE_DEPLOYMENT_TEST_RG_PREFIX="my-aspire-tests"
```

## Running Tests

### Run All Tests Locally

```bash
# From repository root
./build.sh

# Run the deployment tests
dotnet test tests/Aspire.Deployment.EndToEnd.Tests/Aspire.Deployment.EndToEnd.Tests.csproj
```

### Run a Specific Test

```bash
dotnet test tests/Aspire.Deployment.EndToEnd.Tests/Aspire.Deployment.EndToEnd.Tests.csproj \
  -- --filter-method "*.DeployStarterTemplateToAzureContainerApps"
```

## CI/CD

### Triggers

The deployment tests are triggered by:

1. **Nightly schedule** - Runs at 03:00 UTC daily on `main`
2. **Manual dispatch** - Via GitHub Actions workflow_dispatch
3. **Push to `deploy-test/*` branches** - For rapid iteration during development

### Branch Protection

The `deploy-test/*` branch pattern is protected to ensure only team members can trigger deployment tests. This provides security at the Git level.

To iterate on deployment tests:

```bash
# Create a branch with the protected prefix
git checkout -b deploy-test/my-feature

# Make changes and push
git push origin deploy-test/my-feature
# This automatically triggers deployment tests
```

### OIDC Authentication

In CI, tests use Azure Workload Identity Federation (OIDC) for authentication. This eliminates the need for stored secrets.

Required GitHub repository configuration:
- Secret: `AZURE_DEPLOYMENT_TEST_CLIENT_ID` - App registration client ID
- Secret: `AZURE_DEPLOYMENT_TEST_TENANT_ID` - Azure AD tenant ID
- Secret: `AZURE_DEPLOYMENT_TEST_SUBSCRIPTION_ID` - Subscription ID
- Environment: `deployment-testing` with branch protection rules

## Test Structure

```text
Aspire.Deployment.EndToEnd.Tests/
├── Helpers/
│   ├── AzureAuthenticationHelpers.cs  # Azure auth (OIDC/CLI)
│   ├── DeploymentE2ETestHelpers.cs    # Terminal automation helpers
│   ├── DeploymentReporter.cs          # GitHub step summary reporting
│   └── SequenceCounter.cs             # Prompt tracking
├── AcaStarterDeploymentTests.cs           # Blazor to Azure Container Apps
├── AppServicePythonDeploymentTests.cs     # Python FastAPI to Azure App Service
├── AppServiceReactDeploymentTests.cs      # React + ASP.NET Core to Azure App Service
├── AzureAppConfigDeploymentTests.cs       # Azure App Configuration resource
├── AzureContainerRegistryDeploymentTests.cs # Azure Container Registry resource
├── AzureEventHubsDeploymentTests.cs       # Azure Event Hubs resource
├── AzureKeyVaultDeploymentTests.cs        # Azure Key Vault resource
├── AzureLogAnalyticsDeploymentTests.cs    # Azure Log Analytics resource
├── AzureRoleAssignmentRunModeTests.cs     # Run-mode role assignments under the ambient credential
├── AzureServiceBusDeploymentTests.cs      # Azure Service Bus resource
├── AzureStorageDeploymentTests.cs         # Azure Storage resource
├── AzureStorageRunModeTests.cs            # Run-mode resource commands against live Azure
├── PythonFastApiDeploymentTests.cs        # Python FastAPI to Azure Container Apps
├── RadiusStarterDeploymentTests.cs        # Starter template to Radius on AKS (rad deploy)
├── RadiusAzureResourcesDeploymentTests.cs # Gap: cloud-managed Azure resource refs on Radius
├── TypeScriptAzureContainerAppJobDeploymentTests.cs # TypeScript AppHost ACA jobs
├── xunit.runner.json                  # Test runner config
└── README.md                          # This file
```

## Run-mode role assignment coverage

Most tests here drive `aspire deploy` (publish mode). `AzureRoleAssignmentRunModeTests` and
`AzureStorageRunModeTests` instead drive `aspire start`, which is a materially different code path
for RBAC.

In publish mode a role assignment targets a user-assigned managed identity, so `principalType` is
statically `ServicePrincipal` and `BicepProvisioner` refuses to infer principal parameters at all.
In run mode there is no managed identity: the assignment targets the ambient credential, and
`principalType` / `principalId` / `principalName` become plain Bicep parameters filled from the
signed-in identity's access token. ARM rejects a mismatched `principalType` with
`UnmatchedPrincipalType` / `PrincipalNotFound`, which is exactly how
[#13933](https://github.com/microsoft/aspire/issues/13933) surfaced.

`AzureRoleAssignmentRunModeTests.RoleAssignmentsSucceedForAmbientCredentialInRunMode` covers that
path. Its AppHost is a single `builder.AddAzureStorage("storage")` with **no**
`ClearDefaultRoleAssignments()` — in run mode an Azure resource that no compute resource references
still has its default role assignments applied, which synthesizes a `storage-roles` resource. The
test waits for `storage-roles` and `storage` to come up, then reads the ARM deployment back with
`az deployment group show` and asserts the recorded `principalType` matches the kind of identity
`az account show` reports. In CI it additionally asserts that identity is a service principal, so
the job fails loudly if the credential ever degrades to a user and silently stops covering the
app-only scenario.

This is the cheapest scenario in the matrix — one storage account plus one role-assignment
deployment — and runs as its own parallel job with its own resource group.

`AzureStorageRunModeTests` deliberately keeps `ClearDefaultRoleAssignments()`: it covers resource
command metadata, and mixing the RBAC path into it would blur the failure signal.

## Radius deployment coverage

`RadiusStarterDeploymentTests.DeployStarterTemplateToRadiusOnAks` is the live counterpart to the
`Aspire.Hosting.Radius` unit/snapshot tests. Those prove the Bicep serializer output; this test
proves that `aspire publish` + `rad deploy app.bicep` produce a working deployment. It provisions an
AKS cluster + ACR, installs the Radius control plane onto the cluster, deploys the starter app
(`AddRadiusEnvironment`), and verifies the workloads become ready and serve HTTP traffic.

Radius-specific notes:

- **`rad` CLI installation.** `aspire deploy` against a Radius environment shells out to `rad`.
  `RadiusStarterDeploymentTests.DeployStarterTemplateToRadiusOnAks` installs the pinned rad CLI
  version into the test workspace's `radbin` directory and prepends it to `PATH`, so CI and local
  runs do not require a machine-wide `rad` installation.
- **Images must be pre-pushed.** The Radius publisher does not build or push images for project
  resources yet (<https://github.com/microsoft/aspire/issues/16844>). The test builds/pushes the
  starter images to ACR and attaches them with `WithContainerImage` (Experimental
  `ASPIRERADIUS057`) so the generated `app.bicep` references pullable images.
- **Verifies the Radius.Core UDT app, not just Redis.** Graph validation uses
  `rad app graph -a app --preview` (the legacy `rad app graph` routes to Applications.Core, which
  the Redis-only legacy `app` satisfies on its own) and asserts the graph names both project
  containers. Each container is then probed on its own HTTP endpoint via `kubectl port-forward`:
  `apiservice`'s `/weatherforecast`, `webfrontend`'s home page (`/`), and a Redis output-cache
  diagnostic endpoint on `webfrontend` (which verifies recipe-backed cache connection injection).
- **Cross-container connectivity is asserted end-to-end.** The `webfrontend` `/weather` page fans
  out to `apiservice` through the Redis output cache. The Radius `Radius.Compute/containers`
  Kubernetes recipe creates a ClusterIP `Service` for each container that declares ports, named
  `${normalizedName}-${containerName}` with `port`/`targetPort` set to the container port. Aspire
  emits a single container entry keyed by the resource name, so `apiservice`'s Service is
  `apiservice-apiservice` on port `8080`, and Aspire's Radius service discovery emits the matching
  address `http://apiservice-apiservice.<namespace>.svc.cluster.local:8080` (see
  `RadiusEnvironmentResource.GetHostAddressExpression` / `RadiusServiceDiscovery`). The test asserts
  the recipe-created Service exists on the container port, then asserts the direct
  `webfrontend` → `apiservice` diagnostic endpoint and the `/weather` page.

## Radius Azure resource injection (gap)

`RadiusAzureResourcesDeploymentTests.PublishWithAzureKeyVaultReferenceDocumentsCurrentRadiusGap`
documents a **current gap**, not working coverage. The Aspire Radius publisher does not translate
cloud-managed Azure resources (Key Vault, Storage, Service Bus, Azure Managed Redis) into Radius
resources — only portable recipe-backed resources such as `AddRedis` are mapped. When a Radius
container `WithReference`s an Azure resource, `aspire publish` currently **hangs** resolving
`Azure.BicepOutputReference.GetValueAsync` inside `RadiusInfrastructureBuilder.ResolveEnvironmentAsync`
and produces no `app.bicep`. The tracking issue is
[#18802](https://github.com/microsoft/aspire/issues/18802).

Because that hang is the bug being tracked, the test is marked `[ActiveIssue("…/18802")]` and is
**skipped** in CI. Its body asserts the **intended fail-fast behavior** — `aspire publish` should
exit non-zero within a bounded timeout and emit no `app.bicep` — rather than the current hang, so it
starts passing only once the gap is closed. It needs only the current-build Aspire CLI (no
AKS/Azure deployment). When Azure resource injection is implemented (or publish is made to fail
fast), **remove the `[ActiveIssue]` attribute** so the test runs and guards the behavior; do not
expect it to fail automatically while skipped. Product follow-up: add Azure resource mappings, or a
deploy-time bridge from Azure Bicep outputs to Radius container env/connections, and fail fast
instead of hanging.

## TypeScript deployment coverage

TypeScript AppHost publish APIs are first type-checked in `tests/PolyglotAppHosts/**/TypeScript/apphost.ts`. The deployment E2E tests below provide the smaller set of real Azure validations used to catch target-specific deployment regressions.

| TypeScript publish pattern | Polyglot coverage | Real deployment coverage | Notes |
|----------------------------|-------------------|--------------------------|-------|
| Azure Container Apps environment + standard app resources | `tests/PolyglotAppHosts/Aspire.Hosting.Azure.AppContainers/TypeScript/apphost.ts` | `TypeScriptExpressDeploymentTests.DeployTypeScriptExpressTemplateToAzureContainerApps` | Verifies the TypeScript Express/React template deploys to Azure Container Apps and serves traffic. |
| JavaScript app publishing to Azure Container Apps | `tests/PolyglotAppHosts/Aspire.Hosting.JavaScript/TypeScript/apphost.ts` | `TypeScriptJavaScriptHostingDeploymentTests.DeployTypeScriptStaticWebsiteWithNodeApiToAzureContainerApps` | Verifies `publishAsStaticWebsite` with a Node API target from a TypeScript AppHost. |
| Azure Container App jobs | `tests/PolyglotAppHosts/Aspire.Hosting.Azure.AppContainers/TypeScript/apphost.ts` | `TypeScriptAzureContainerAppJobDeploymentTests.DeployTypeScriptContainerAppJobsToAzureContainerApps` | Verifies manual and scheduled Container App Job resources are deployed with the expected trigger configuration. |
| Azure infrastructure dependencies used from TypeScript | `tests/PolyglotAppHosts/Aspire.Hosting.Azure.Sql/TypeScript/apphost.ts` and Azure support package apphosts | `TypeScriptVnetSqlServerInfraDeploymentTests.DeployTypeScriptVnetSqlServerInfrastructure` | Verifies Azure SQL Server, VNet, private endpoint, and deployment-script subnet wiring from TypeScript. |
| Azure Kubernetes Environment gateway and cert-manager | `tests/PolyglotAppHosts/Aspire.Hosting.Kubernetes/TypeScript/apphost.ts` | `AksAzureKubernetesEnvironmentCertManagerTypeScriptDeploymentTests.DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironment` | Verifies AKS provisioning, AGC gateway routing, cert-manager issuer configuration, and HTTPS traffic from TypeScript. |
| Kubernetes service and custom manifest publishing | `tests/PolyglotAppHosts/Aspire.Hosting.Kubernetes/TypeScript/apphost.ts` | `AksAzureKubernetesEnvironmentCertManagerTypeScriptDeploymentTests.DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironment` | The TypeScript AKS test also deploys a Redis service via `publishAsKubernetesService` and verifies a custom ConfigMap manifest. |

### Intentional TypeScript deployment gaps

The following TypeScript publish paths remain type-checked by the polyglot apphosts but are not each covered by a dedicated real deployment test:

| Gap | Rationale |
|-----|-----------|
| Azure Container Apps custom domain and certificate binding | The TypeScript AppContainers polyglot apphost validates the exported shape, while real custom-domain deployment requires owned DNS and certificate setup that would make the deployment test tenant-specific and difficult to clean up reliably. |
| Starting and asserting Azure Container App job executions | The real deployment test validates the deployed job resources and trigger configuration. It does not start jobs because the current coverage goal is deployment-shape validation and scheduled jobs are not practical to wait for deterministically. |
| Every Kubernetes custom resource shape accepted by `addManifest` | The real TypeScript AKS test validates that custom manifests are emitted and applied using a core `ConfigMap`. CRD-backed examples such as KEDA `ScaledObject` stay in polyglot type-check coverage because installing every CRD would substantially increase runtime and failure modes. |
| Docker Compose, Dockerfile, App Service, YARP, Entity Framework migration, and Foundry publish APIs from TypeScript | These APIs are type-checked in their package-specific TypeScript polyglot apphosts. Real deployment coverage is either target-specific outside Azure deployment E2E, already covered through C# scenarios, or would require additional external services and quotas not justified for the TypeScript smoke matrix. |

## Writing New Tests

See the [Deployment E2E Testing Skill](../../.agents/skills/deployment-e2e-testing/SKILL.md) for detailed patterns and guidance.

Basic test structure:

```csharp
public sealed class MyDeploymentTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _resourceGroupName;

    public MyDeploymentTests(ITestOutputHelper output)
    {
        _output = output;
        _resourceGroupName = AzureAuthenticationHelpers.GenerateResourceGroupName(nameof(MyDeploymentTests));
    }

    [Fact]
    public async Task DeployMyScenario()
    {
        // 1. Create workspace and terminal
        var workspace = TemporaryWorkspace.Create(_output);
        var recordingPath = DeploymentE2ETestHelpers.GetTestResultsRecordingPath(nameof(DeployMyScenario));

        // 2. Build terminal and sequence
        // 3. Create project, deploy, verify endpoints
        // 4. Report results and cleanup
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup resources
    }
}
```

## Troubleshooting

### Authentication Failures

**Local**: Ensure you're logged in with `az login` and have access to the subscription.

**CI**: Check that OIDC federation is correctly configured between GitHub and Azure AD.

### Deployment Timeouts

Deployments can take 15-30+ minutes. Current timeout settings:

| Step | Timeout | Description |
|------|---------|-------------|
| Overall test | 40 minutes | Maximum time for entire test execution |
| Pipeline deployment | 30 minutes | Time for `aspire deploy` to complete |
| Endpoint verification | 5 minutes | Time for endpoint check command with retries |
| Per-endpoint retry | ~3 minutes | 18 attempts × 10 seconds per endpoint |

### Resource Cleanup

Tests attempt to clean up Azure resources after completion. The cleanup workflow runs hourly to remove orphaned resources.

To find orphaned resources:

```bash
# Resource groups created by deployment tests (current naming)
az group list --query "[?starts_with(name, 'e2e-')]" -o table

# Resource groups created by aspire deploy (legacy naming)
az group list --query "[?starts_with(name, 'rg-aspire-')]" -o table

# Delete all test resource groups (use with caution!)
az group list --query "[?starts_with(name, 'e2e-')].name" -o tsv | xargs -I {} az group delete --name {} --yes --no-wait
```

### Viewing Recordings

Tests generate asciinema recordings in CI. Download from the workflow artifacts to replay:

```bash
asciinema play path/to/recording.cast
```

## Tenant Rotation

The test Azure tenant/subscription rotates approximately every 90 days per policy. When rotation occurs:

1. Create new App Registration in the new tenant
2. Configure Workload Identity Federation for the `deployment-tests` GitHub environment
3. Grant Owner role on subscription (constrained - cannot create other Owner identities)
4. Update GitHub secrets: `AZURE_DEPLOYMENT_TEST_CLIENT_ID`, `AZURE_DEPLOYMENT_TEST_TENANT_ID`
5. Update GitHub variable: `AZURE_DEPLOYMENT_TEST_SUBSCRIPTION_ID`
6. Ensure regional quotas per [Azure Subscription Quota Requirements](#azure-subscription-quota-requirements): Container Apps, App Service, and AKS in `westus3` (`StandardDASv5Family` vCPUs, Total Regional vCPUs, and managed clusters). The CI workflow self-heals all three AKS quotas.

See [Deployment Testing Documentation](../../docs/deployment-testing.md) for detailed rotation procedures.
