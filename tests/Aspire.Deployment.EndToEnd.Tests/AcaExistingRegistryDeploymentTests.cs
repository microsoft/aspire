// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications to Azure Container Apps
/// using a pre-existing Azure Container Registry referenced via AsExisting.
/// </summary>
public sealed class AcaExistingRegistryDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for ACR pre-creation and Azure provisioning.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployStarterTemplateWithExistingRegistry()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterTemplateWithExistingRegistryCore(cancellationToken);
    }

    private async Task DeployStarterTemplateWithExistingRegistryCore(CancellationToken cancellationToken)
    {
        // Validate prerequisites
        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Azure subscription not configured. Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION.");
        }

        if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
        {
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                Assert.Fail("Azure authentication not available in CI. Check OIDC configuration.");
            }
            else
            {
                Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
            }
        }

        var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var deploymentUrls = new Dictionary<string, string>();
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("aca-existing-acr");
        var projectName = "AcaExistingAcr";

        // ACR names must be alphanumeric only, 5-50 chars, globally unique
        var runId = DeploymentE2ETestHelpers.GetRunId();
        var attempt = DeploymentE2ETestHelpers.GetRunAttempt();
        var acrName = $"e2eexistingacr{runId}{attempt}";
        // Truncate to 50 chars max (ACR name limit)
        if (acrName.Length > 50)
        {
            acrName = acrName[..50];
        }

        output.WriteLine($"Test: {nameof(DeployStarterTemplateWithExistingRegistry)}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Pre-created ACR Name: {acrName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // Step 1: Prepare environment
            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            // Step 2: Set up CLI environment (in CI)
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                output.WriteLine("Step 2: Using pre-installed Aspire CLI from local build...");
                await auto.SourceAspireCliEnvironmentAsync(counter);
            }

            // Step 3: Pre-create resource group and ACR via az CLI
            output.WriteLine("Step 3: Pre-creating resource group and ACR...");
            await auto.TypeAsync($"az group create --name {resourceGroupName} --location westus3 -o none");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync($"az acr create --name {acrName} --resource-group {resourceGroupName} --sku Basic --admin-enabled false -o none");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            output.WriteLine($"Pre-created ACR: {acrName} in resource group: {resourceGroupName}");

            // Step 4: Create starter project using aspire new with interactive prompts
            output.WriteLine("Step 4: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 5: Navigate to project directory
            output.WriteLine("Step 5: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 6: Add Aspire.Hosting.Azure.AppContainers package
            output.WriteLine("Step 6: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 7: Add Aspire.Hosting.Azure.ContainerRegistry package
            output.WriteLine("Step 7: Adding Azure Container Registry hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.ContainerRegistry");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 8: Modify AppHost.cs to reference the existing ACR
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Reference existing Azure Container Registry via parameter
var acrName = builder.AddParameter("acrName");
var acr = builder.AddAzureContainerRegistry("existingacr").AsExisting(acrName, null);
builder.AddAzureContainerAppEnvironment("infra").WithAzureContainerRegistry(acr);

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            // Step 9: Navigate to AppHost project directory
            output.WriteLine("Step 9: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 10: Set environment variables for deployment including ACR parameter
            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && " +
                $"export AZURE__LOCATION=westus3 && " +
                $"export AZURE__RESOURCEGROUP={resourceGroupName} && " +
                $"export Parameters__acrName={acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 11: Deploy to Azure Container Apps using aspire deploy
            output.WriteLine("Step 11: Starting Azure Container Apps deployment...");
            var pipelineSucceeded = false;
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitUntilAsync(s =>
            {
                if (s.ContainsText("PIPELINE SUCCEEDED"))
                {
                    pipelineSucceeded = true;
                    return true;
                }
                return s.ContainsText("PIPELINE FAILED");
            }, timeout: TimeSpan.FromMinutes(35), description: "pipeline succeeded or failed");

            if (!pipelineSucceeded)
            {
                throw new InvalidOperationException("Deployment pipeline failed. Check the terminal output for details.");
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 12: Extract deployment URLs and verify endpoints with retry
            output.WriteLine("Step 12: Verifying deployed endpoints...");
            await auto.TypeAsync(
                $"RG_NAME=\"{resourceGroupName}\" && " +
                      "echo \"Resource group: $RG_NAME\" && " +
                      "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                      "urls=$(az containerapp list -g \"$RG_NAME\" --query \"[].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | grep -v '\\.internal\\.') && " +
                      "if [ -z \"$urls\" ]; then echo \"❌ No external container app endpoints found\"; exit 1; fi && " +
                      "failed=0 && " +
                      "for url in $urls; do " +
                      "echo \"Checking https://$url...\"; " +
                      "success=0; " +
                      "for i in $(seq 1 18); do " +
                      "STATUS=$(curl -s -o /dev/null -w \"%{http_code}\" \"https://$url\" --max-time 10 2>/dev/null); " +
                      "if [ \"$STATUS\" = \"200\" ] || [ \"$STATUS\" = \"302\" ]; then echo \"  ✅ $STATUS (attempt $i)\"; success=1; break; fi; " +
                      "echo \"  Attempt $i: $STATUS, retrying in 10s...\"; sleep 10; " +
                      "done; " +
                      "if [ \"$success\" -eq 0 ]; then echo \"  ❌ Failed after 18 attempts\"; failed=1; fi; " +
                      "done && " +
                "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more endpoint checks failed\"; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 13: Verify the pre-existing ACR contains container images
            output.WriteLine("Step 13: Verifying container images in pre-existing ACR...");
            await auto.TypeAsync(
                $"echo \"ACR: {acrName}\" && " +
                      $"REPOS=$(az acr repository list --name \"{acrName}\" -o tsv) && " +
                      "echo \"Repositories: $REPOS\" && " +
                      "if [ -z \"$REPOS\" ]; then echo \"❌ No container images found in ACR\"; exit 1; fi && " +
                      "for repo in $REPOS; do " +
                      $"TAGS=$(az acr repository show-tags --name \"{acrName}\" --repository \"$repo\" -o tsv); " +
                      "echo \"  $repo: $TAGS\"; " +
                      "if [ -z \"$TAGS\" ]; then echo \"  ❌ No tags for $repo\"; exit 1; fi; " +
                      "done && " +
                "echo \"✅ All container images verified in ACR\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 14: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterTemplateWithExistingRegistry),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterTemplateWithExistingRegistry),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName, output);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
    }

    /// <summary>
    /// Triggers cleanup of a specific resource group.
    /// This is fire-and-forget - the hourly cleanup workflow handles any missed resources.
    /// </summary>
    private static void TriggerCleanupResourceGroup(string resourceGroupName, ITestOutputHelper output)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            output.WriteLine($"Cleanup triggered for resource group: {resourceGroupName}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to trigger cleanup: {ex.Message}");
        }
    }
}
