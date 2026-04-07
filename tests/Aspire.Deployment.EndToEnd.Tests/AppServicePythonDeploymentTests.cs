// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Python FastAPI Aspire applications to Azure App Service.
/// </summary>
public sealed class AppServicePythonDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 40 minutes to allow for Azure provisioning and Python environment setup.
    // Full deployments can take up to 30 minutes if Azure infrastructure is backed up.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task DeployPythonFastApiTemplateToAzureAppService()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployPythonFastApiTemplateToAzureAppServiceCore(cancellationToken);
    }

    private async Task DeployPythonFastApiTemplateToAzureAppServiceCore(CancellationToken cancellationToken)
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
        // Generate a unique resource group name with pattern: e2e-[testcasename]-[runid]-[attempt]
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("python-appsvc");
        // Project name can be simpler since resource group is explicitly set
        var projectName = "PyAppSvc";

        output.WriteLine($"Test: {nameof(DeployPythonFastApiTemplateToAzureAppService)}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
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

            // Step 3: Create Python FastAPI project using aspire new with interactive prompts
            output.WriteLine("Step 3: Creating Python FastAPI project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.PythonReact, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Azure.AppService package (instead of AppContainers)
            output.WriteLine("Step 5: Adding Azure App Service hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppService");
            await auto.EnterAsync();

            // In CI, aspire add shows a version selection prompt
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync(); // select first version (PR build)
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 6: Modify apphost.cs to add Azure App Service Environment
            // Note: Python template uses single-file AppHost (apphost.cs in project root)
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            // Single-file AppHost is in the project root, not a subdirectory
            var appHostFilePath = Path.Combine(projectDir, "apphost.cs");

            output.WriteLine($"Looking for apphost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert the Azure App Service Environment before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Add Azure App Service Environment for deployment
builder.AddAzureAppServiceEnvironment("infra");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified apphost.cs at: {appHostFilePath}");

            // Step 7: Set environment for deployment
            // - Unset ASPIRE_PLAYGROUND to avoid conflicts
            // - Set Azure location to westus3 (same as other tests to use region with capacity)
            // - Set AZURE__RESOURCEGROUP to use our unique resource group name
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Deploy to Azure App Service using aspire deploy
            output.WriteLine("Step 7: Starting Azure App Service deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            // Wait for pipeline to complete successfully (App Service can take longer)
            await auto.WaitUntilTextAsync("PIPELINE SUCCEEDED", timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 9: Extract deployment URLs and verify endpoints with retry
            // For App Service, we use az webapp list instead of az containerapp list
            // Retry each endpoint for up to 3 minutes (18 attempts * 10 seconds)
            output.WriteLine("Step 8: Verifying deployed endpoints...");
            await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
                  "echo \"Resource group: $RG_NAME\" && " +
                  "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                  // Get App Service hostnames (defaultHostName for each web app)
                  "urls=$(az webapp list -g \"$RG_NAME\" --query \"[].defaultHostName\" -o tsv 2>/dev/null) && " +
                  "if [ -z \"$urls\" ]; then echo \"❌ No App Service endpoints found\"; exit 1; fi && " +
                  "failed=0 && " +
                  "for url in $urls; do " +
                  "echo \"Checking https://$url...\"; " +
                  "success=0; " +
                  "for i in $(seq 1 18); do " +
                  "STATUS=$(curl -s -o /dev/null -w \"%{http_code}\" \"https://$url\" --max-time 30 2>/dev/null); " +
                  "if [ \"$STATUS\" = \"200\" ] || [ \"$STATUS\" = \"302\" ]; then echo \"  ✅ $STATUS (attempt $i)\"; success=1; break; fi; " +
                  "echo \"  Attempt $i: $STATUS, retrying in 10s...\"; sleep 10; " +
                  "done; " +
                  "if [ \"$success\" -eq 0 ]; then echo \"  ❌ Failed after 18 attempts\"; failed=1; fi; " +
                  "done && " +
                  "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more endpoint checks failed\"; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 10: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployPythonFastApiTemplateToAzureAppService),
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
                nameof(DeployPythonFastApiTemplateToAzureAppService),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Clean up the resource group we created
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName, output);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
    }

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
