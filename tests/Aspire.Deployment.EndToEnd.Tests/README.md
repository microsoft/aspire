# Aspire Deployment End-to-End Tests

This project contains end-to-end tests that deploy Aspire applications to real Azure infrastructure.  These tests verify that the complete deployment workflow works correctly, from project creation to live deployment and endpoint verification.

## Overview

These tests use the [Hex1b](https://github.com/hex1b/hex1b) terminal automation library to drive the Aspire CLI, similar to the CLI E2E tests. The key difference is that these tests actually deploy to Azure and verify the deployed applications work correctly.

## Azure Subscription Quota Requirements

The deployment tests require an Azure subscription with sufficient quota for the resources being deployed. Ensure the following quotas are available in the test region (currently `westus3`).

### Container Apps

| Resource | Quota Required | Current Setting | Notes |
|----------|---------------|-----------------|-------|
| Managed Environments | 150+ | 150 | Each test run creates a new environment. High quota allows concurrent runs and handles cleanup delays. |
| Container App Instances | Default | - | Standard quota is typically sufficient |

### App Service

| Resource | Quota Required | Current Setting | Notes |
|----------|---------------|-----------------|-------|
| PremiumV3 vCPUs | 10+ | TBD | App Service Plans use PremiumV3 tier (P0V3). Each deployment needs ~1 vCPU. |
| App Service Plans | 10+ | Default | Each deployment creates a new plan |

### Container Registry

| Resource | Quota Required | Notes |
|----------|---------------|-------|
| Azure Container Registry | Default | Standard quota is typically sufficient |

### General

| Resource | Quota Required | Notes |
|----------|---------------|-------|
| Resource Groups | 100+ | Each test creates a unique resource group (e.g., `e2e-starter-12345678-1`) |
| Role Assignments | Default | Tests may create role assignments for managed identities |

### Requesting Quota Increases

To request quota increases:

1. Go to the [Azure Portal](https://portal.azure.com)
2. Navigate to **Subscriptions** → Select your subscription
3. Go to **Usage + quotas**
4. Filter by the resource type:
   - `Microsoft.App` for Container Apps
   - `Microsoft.Web` for App Service
5. Select the quota to increase and click **Request increase**

Common quota increase requests:
- **Container Apps Managed Environments**: Request 150+ in westus3
- **App Service PremiumV3 vCPUs**: Request 10+ in westus3

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
├── AzureServiceBusDeploymentTests.cs      # Azure Service Bus resource
├── AzureStorageDeploymentTests.cs         # Azure Storage resource
├── PythonFastApiDeploymentTests.cs        # Python FastAPI to Azure Container Apps
├── TypeScriptAzureContainerAppJobDeploymentTests.cs # TypeScript AppHost ACA jobs
├── xunit.runner.json                  # Test runner config
└── README.md                          # This file
```

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

See [Deployment Testing Documentation](../../docs/deployment-testing.md) for detailed rotation procedures.
