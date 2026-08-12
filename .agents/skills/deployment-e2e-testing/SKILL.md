---
name: deployment-e2e-testing
description: Guide for writing Aspire deployment end-to-end tests. Use this when asked to create, modify, or debug deployment E2E tests that deploy to Azure.
---

# Aspire Deployment End-to-End Testing

This skill provides patterns and practices for writing end-to-end tests that deploy Aspire applications to real Azure infrastructure.

## Overview

Deployment E2E tests extend the CLI E2E testing patterns with actual Azure deployments. They use the Hex1b terminal automation library to drive the Aspire CLI and verify that deployed applications work correctly.

**Location**: `tests/Aspire.Deployment.EndToEnd.Tests/`

**Supported Platforms**: Linux only (Hex1b requirement).

**Prerequisites**:
- Azure subscription with appropriate permissions
- OIDC authentication (CI) or Azure CLI authentication (local)

## Relationship to CLI E2E Tests

Deployment tests build on the CLI E2E testing skill. Before working with deployment tests, familiarize yourself with:

- [CLI E2E Testing Skill](../cli-e2e-testing/SKILL.md) - Core terminal automation patterns

Key differences from CLI E2E tests:

| Aspect | CLI E2E Tests | Deployment E2E Tests |
|--------|---------------|----------------------|
| Duration | 5-15 minutes | 15-45 minutes |
| Resources | Local only | Azure resources |
| Authentication | None | Azure OIDC/CLI |
| Cleanup | Temp directories | Azure resource groups |
| Triggers | PR, push | Nightly, manual, deploy-test/* |

## Key Components

### Core Classes

- **`DeploymentE2ETestHelpers`** (`Helpers/DeploymentE2ETestHelpers.cs`): Terminal factory and environment helpers
- **`DeploymentE2EAutomatorHelpers`** (`Helpers/DeploymentE2EAutomatorHelpers.cs`): Async extension methods on `Hex1bTerminalAutomator` for deployment scenarios
- **`Hex1bAutomatorTestHelpers`** (shared): Common async extension methods on `Hex1bTerminalAutomator` (`WaitForSuccessPromptAsync`, `AspireNewAsync`, etc.)
- **`AzureAuthenticationHelpers`** (`Helpers/AzureAuthenticationHelpers.cs`): Azure auth and resource naming
- **`DeploymentReporter`** (`Helpers/DeploymentReporter.cs`): GitHub step summary reporting
- **`SequenceCounter`** (`Helpers/SequenceCounter.cs`): Prompt tracking (same as CLI E2E)

### Test Architecture

Each deployment test:

1. Validates Azure authentication (skip if not available locally)
2. Generates a unique resource group name
3. Creates a project using `aspire new`
4. Deploys using `aspire deploy`
5. Verifies deployed endpoints work
6. Reports results to GitHub step summary
7. Cleans up Azure resources

## Test Structure

```csharp
public sealed class MyDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeployMyScenario()
    {
        // 1. Validate prerequisites
        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Azure subscription not configured.");
        }

        if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
        {
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                Assert.Fail("Azure auth not available in CI.");
            }
            Assert.Skip("Azure auth not available. Run 'az login'.");
        }

        // 2. Setup
        var resourceGroupName = AzureAuthenticationHelpers.GenerateResourceGroupName("my-scenario");
        var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;

        try
        {
            // 3. Build terminal and run deployment
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.SourceAspireCliEnvironmentAsync(counter);
            }
            await auto.AspireNewAsync("MyProject", counter, useRedisCache: false);
            // ... deployment steps ...

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            // 4. Report success
            var duration = DateTime.UtcNow - startTime;
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployMyScenario),
                resourceGroupName,
                deploymentUrls,
                duration);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployMyScenario),
                resourceGroupName,
                ex.Message);
            throw;
        }
        finally
        {
            // 5. Cleanup Azure resources
            await CleanupResourceGroupAsync(resourceGroupName);
        }
    }
}
```

## Extension Methods

### DeploymentE2EAutomatorHelpers Extensions on Hex1bTerminalAutomator

| Method | Description |
|--------|-------------|
| `PrepareEnvironmentAsync(workspace, counter)` | Sets up the terminal environment with custom prompt and workspace directory |
| `InstallAspireCliFromPullRequestAsync(prNumber, counter)` | Downloads and installs the Aspire CLI from a PR build artifact |
| `InstallAspireCliReleaseAsync(counter)` | Installs the latest released Aspire CLI |
| `SourceAspireCliEnvironmentAsync(counter)` | Adds `~/.aspire/bin` to PATH so the `aspire` command is available |

These extend `Hex1bTerminalAutomator` and are used alongside the shared `Hex1bAutomatorTestHelpers` methods (`WaitForSuccessPromptAsync`, `AspireNewAsync`, etc.) documented in the [CLI E2E Testing Skill](../cli-e2e-testing/SKILL.md).

## Azure Authentication

### In CI (GitHub Actions)

Tests use OIDC (Workload Identity Federation) for authentication:

```yaml
- name: Azure Login (OIDC)
  uses: azure/login@v2
  with:
    client-id: ${{ secrets.AZURE_CLIENT_ID }}
    tenant-id: ${{ secrets.AZURE_TENANT_ID }}
    subscription-id: ${{ vars.ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION }}
```

The test code automatically detects CI and uses `DefaultAzureCredential` which picks up the OIDC session.

### Local Development

Authenticate with Azure CLI before running tests:

```bash
# Login to Azure
az login

# Set your subscription
az account set --subscription "your-subscription-id"

# Set environment variable
export ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION="your-subscription-id"

# Run tests
dotnet test tests/Aspire.Deployment.EndToEnd.Tests/
```

### Authentication Helpers

```csharp
// Check if auth is available
if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
{
    Assert.Skip("Azure auth not available");
}

// Get subscription ID
var subscriptionId = AzureAuthenticationHelpers.GetSubscriptionId();

// Generate unique resource group name
var rgName = AzureAuthenticationHelpers.GenerateResourceGroupName("my-test");
// Result: "aspire-e2e-my-test-20240115-abc12345"

// Check auth type
if (AzureAuthenticationHelpers.IsOidcConfigured())
{
    // Using OIDC (CI)
}
else
{
    // Using Azure CLI (local)
}
```

## Resource Group Naming

Resource groups are named with a consistent pattern for easy identification and cleanup:

```
{prefix}-{testname}-{date}-{runid}
```

Example: `aspire-e2e-aca-starter-20240115-12345678`

Components:
- **prefix**: From `ASPIRE_DEPLOYMENT_TEST_RG_PREFIX` (default: `aspire-e2e`)
- **testname**: Sanitized test name (lowercase, alphanumeric, hyphens)
- **date**: UTC date in YYYYMMDD format
- **runid**: GitHub run ID or random GUID suffix

## Reporting Results

### GitHub Step Summary

Tests automatically write to the GitHub step summary:

```csharp
// Report success with URLs
DeploymentReporter.ReportDeploymentSuccess(
    testName: "DeployStarterToACA",
    resourceGroupName: "aspire-e2e-...",
    deploymentUrls: new Dictionary<string, string>
    {
        ["Dashboard"] = "https://dashboard.azurecontainerapps.io",
        ["Web Frontend"] = "https://webfrontend.azurecontainerapps.io"
    },
    duration: TimeSpan.FromMinutes(15));

// Report failure
DeploymentReporter.ReportDeploymentFailure(
    testName: "DeployStarterToACA",
    resourceGroupName: "aspire-e2e-...",
    errorMessage: "Deployment timed out",
    logs: "Full deployment logs...");
```

### Asciinema Recordings

Tests generate recordings for debugging:

```csharp
var recordingPath = DeploymentE2ETestHelpers.GetTestResultsRecordingPath(nameof(MyTest));
// CI: $GITHUB_WORKSPACE/testresults/recordings/MyTest.cast
// Local: /tmp/aspire-deployment-e2e/recordings/MyTest.cast
```

## Cleanup

Always cleanup Azure resources in a `finally` block:

```csharp
private static async Task CleanupResourceGroupAsync(string resourceGroupName)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "az",
            Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }
    };

    process.Start();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
        var error = await process.StandardError.ReadToEndAsync();
        throw new InvalidOperationException($"Cleanup failed: {error}");
    }
}
```

## Workflow Triggers

Deployment tests are triggered by:

1. **Nightly schedule** (03:00 UTC) - Runs on `main`
2. **Manual dispatch** - Via GitHub Actions UI
3. **Push to `deploy-test/*`** - For rapid iteration

### Iterating on Tests

To iterate quickly during development:

```bash
# Create a protected branch
git checkout -b deploy-test/my-feature

# Make changes
# ...

# Push to trigger workflow
git push origin deploy-test/my-feature
```

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION` | Yes | Azure subscription ID |
| `ASPIRE_DEPLOYMENT_TEST_RG_PREFIX` | No | Resource group prefix (default: `aspire-e2e`) |
| `AZURE_DEPLOYMENT_TEST_TENANT_ID` | CI | Azure AD tenant ID (GitHub secret) |
| `AZURE_DEPLOYMENT_TEST_CLIENT_ID` | CI | OIDC app client ID (GitHub secret) |
| `AZURE_DEPLOYMENT_TEST_SUBSCRIPTION_ID` | CI | Azure subscription ID (GitHub secret) |

## DO: Always Validate Prerequisites

```csharp
var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
if (string.IsNullOrEmpty(subscriptionId))
{
    Assert.Skip("Subscription not configured");
}

if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
{
    Assert.Skip("Azure auth not available");
}
```

## DO: Generate Unique Resource Groups

```csharp
var rgName = AzureAuthenticationHelpers.GenerateResourceGroupName("my-test");
```

## DO: Report to GitHub Summary

```csharp
DeploymentReporter.ReportDeploymentSuccess(...);
// or
DeploymentReporter.ReportDeploymentFailure(...);
```

## DO: Always Cleanup Resources

```csharp
try
{
    // ... deployment test ...
}
finally
{
    await CleanupResourceGroupAsync(resourceGroupName);
}
```

## DON'T: Hardcode Subscription IDs

```csharp
// DON'T
var subscriptionId = "12345-abcde-...";

// DO
var subscriptionId = AzureAuthenticationHelpers.GetSubscriptionId();
```

## DON'T: Skip Cleanup on Failure

```csharp
// DON'T - cleanup might not run
await DeployAsync();
await CleanupAsync();  // Skipped if deploy throws!

// DO - always cleanup
try
{
    await DeployAsync();
}
finally
{
    await CleanupAsync();  // Always runs
}
```

## Troubleshooting

### Authentication Failures

**Local**: Ensure Azure CLI is authenticated:
```bash
az login
az account show
```

**CI**: Check OIDC configuration:
- `AZURE_CLIENT_ID` secret is set
- `AZURE_TENANT_ID` secret is set
- Workload Identity Federation is configured in Azure AD

### Deployment Timeouts

Deployments can take 15-30+ minutes. If tests timeout:
- Check the asciinema recording for where it stopped
- Increase timeout in `WaitUntil` calls
- Check Azure portal for deployment status

### Orphaned Resources

Find and cleanup orphaned test resources:

```bash
# List all test resource groups
az group list --query "[?starts_with(name, 'aspire-e2e')]" -o table

# Delete specific resource group
az group delete --name aspire-e2e-xxx --yes
```

### Tenant Rotation

The test tenant rotates every ~90 days. When rotation occurs:

1. Create new App Registration in new tenant
2. Configure Workload Identity Federation for the `deployment-testing` environment
3. Grant Owner role on subscription (constrained - cannot create other Owner identities)
4. Update GitHub secrets: `AZURE_DEPLOYMENT_TEST_CLIENT_ID`, `AZURE_DEPLOYMENT_TEST_TENANT_ID`, `AZURE_DEPLOYMENT_TEST_SUBSCRIPTION_ID`
