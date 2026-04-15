// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications across multiple Azure compute environments.
/// Validates cross-environment references between Azure App Service and Azure Container Apps.
/// </summary>
public sealed class CrossEnvironmentDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for both ACA and App Service provisioning.
    // Cross-environment deployments provision infrastructure in two environments, so they take longer.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployStarterWithWebFrontendOnAppServiceAndApiServiceOnAca()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterWithWebFrontendOnAppServiceAndApiServiceOnAcaCore(cancellationToken);
    }

    private async Task DeployStarterWithWebFrontendOnAppServiceAndApiServiceOnAcaCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("crossenv");
        var projectName = "CrossEnvDeploy";

        output.WriteLine($"Test: {nameof(DeployStarterWithWebFrontendOnAppServiceAndApiServiceOnAca)}");
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

            // Step 3: Create starter project (Blazor) without Redis
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add both Azure hosting packages for cross-environment deployment
            output.WriteLine("Step 5a: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            output.WriteLine("Step 5b: Adding Azure App Service hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppService");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 6: Modify AppHost.cs for cross-environment deployment
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);
            output.WriteLine($"Original AppHost.cs:\n{content}");

            // 6a: Add compute environment declarations before apiService
            var original = content;
            content = content.Replace(
                "var apiService =",
                """
                // Add both compute environments for cross-environment deployment
                var aca = builder.AddAzureContainerAppEnvironment("aca")
                    .WithDashboard(false);
                var appService = builder.AddAzureAppServiceEnvironment("appService")
                    .WithDashboard(false);

                var apiService =
                """);
            Assert.True(content != original, "Failed to insert compute environment declarations into AppHost.cs");

            // 6b: Add .WithComputeEnvironment(aca) and .WithExternalHttpEndpoints() to apiservice
            original = content;
            content = content.Replace(
                "(\"apiservice\")\n    .WithHttpHealthCheck",
                "(\"apiservice\")\n    .WithComputeEnvironment(aca)\n    .WithExternalHttpEndpoints()\n    .WithHttpHealthCheck");
            Assert.True(content != original, "Failed to add WithComputeEnvironment(aca) to apiservice in AppHost.cs");

            // 6c: Add .WithComputeEnvironment(appService) to webfrontend
            original = content;
            content = content.Replace(
                "(\"webfrontend\")\n    .WithExternalHttpEndpoints",
                "(\"webfrontend\")\n    .WithComputeEnvironment(appService)\n    .WithExternalHttpEndpoints");
            Assert.True(content != original, "Failed to add WithComputeEnvironment(appService) to webfrontend in AppHost.cs");

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine($"Modified AppHost.cs:\n{content}");

            // Step 7: Navigate to AppHost project directory
            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy to Azure using aspire deploy
            output.WriteLine("Step 9: Starting cross-environment deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            // Cross-environment deployment provisions both ACA and App Service infrastructure
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(35));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 10: Verify deployed endpoints
            // Get the App Service URL for webfrontend and verify the weather page works
            output.WriteLine("Step 10: Verifying deployed endpoints...");
            await auto.TypeAsync(
                $"RG_NAME=\"{resourceGroupName}\" && " +
                "echo \"Resource group: $RG_NAME\" && " +
                "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                // Verify App Service (webfrontend) endpoints
                "webapp_urls=$(az webapp list -g \"$RG_NAME\" --query \"[].defaultHostName\" -o tsv 2>/dev/null) && " +
                "if [ -z \"$webapp_urls\" ]; then echo \"❌ No App Service endpoints found\"; exit 1; fi && " +
                // Verify Container App (apiservice) endpoints - exclude internal endpoints
                "ca_urls=$(az containerapp list -g \"$RG_NAME\" --query \"[].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | grep -v '\\.internal\\.') && " +
                "if [ -z \"$ca_urls\" ]; then echo \"❌ No external Container App endpoints found\"; exit 1; fi && " +
                // Check each App Service endpoint is accessible
                "failed=0 && " +
                "for url in $webapp_urls; do " +
                "echo \"Checking App Service: https://$url...\"; " +
                "success=0; " +
                "for i in $(seq 1 18); do " +
                "STATUS=$(curl -s -o /dev/null -w \"%{http_code}\" \"https://$url\" --max-time 30 2>/dev/null); " +
                "if [ \"$STATUS\" = \"200\" ] || [ \"$STATUS\" = \"302\" ]; then echo \"  ✅ $STATUS (attempt $i)\"; success=1; break; fi; " +
                "echo \"  Attempt $i: $STATUS, retrying in 10s...\"; sleep 10; " +
                "done; " +
                "if [ \"$success\" -eq 0 ]; then echo \"  ❌ App Service endpoint failed after 18 attempts\"; failed=1; fi; " +
                "done && " +
                // Check each Container App endpoint is accessible
                "for url in $ca_urls; do " +
                "echo \"Checking Container App: https://$url...\"; " +
                "success=0; " +
                "for i in $(seq 1 18); do " +
                "STATUS=$(curl -s -o /dev/null -w \"%{http_code}\" \"https://$url\" --max-time 10 2>/dev/null); " +
                "if [ \"$STATUS\" = \"200\" ] || [ \"$STATUS\" = \"302\" ]; then echo \"  ✅ $STATUS (attempt $i)\"; success=1; break; fi; " +
                "echo \"  Attempt $i: $STATUS, retrying in 10s...\"; sleep 10; " +
                "done; " +
                "if [ \"$success\" -eq 0 ]; then echo \"  ❌ Container App endpoint failed after 18 attempts\"; failed=1; fi; " +
                "done && " +
                "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more endpoint checks failed\"; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 11: Verify the weather page on webfrontend returns actual data from apiservice (cross-env reference)
            // This proves the App Service webfrontend can successfully call the ACA apiservice
            output.WriteLine("Step 11: Verifying weather page returns data from apiservice (cross-environment reference)...");
            await auto.TypeAsync(
                $"RG_NAME=\"{resourceGroupName}\" && " +
                "webapp_url=$(az webapp list -g \"$RG_NAME\" --query \"[0].defaultHostName\" -o tsv 2>/dev/null) && " +
                "echo \"Fetching weather page from https://$webapp_url/weather...\" && " +
                "success=0 && " +
                "for i in $(seq 1 12); do " +
                "BODY=$(curl -s \"https://$webapp_url/weather\" --max-time 30 2>/dev/null); " +
                "if echo \"$BODY\" | grep -q 'class=\"table\"'; then " +
                "echo \"  ✅ Weather page contains table data (attempt $i)\"; " +
                // Also verify it's not stuck on Loading
                "if echo \"$BODY\" | grep -q 'Loading...'; then " +
                "echo \"  ⚠️ Page still loading, retrying...\"; sleep 10; continue; fi; " +
                "success=1; break; fi; " +
                "echo \"  Attempt $i: Weather table not found, retrying in 10s...\"; sleep 10; " +
                "done && " +
                "if [ \"$success\" -eq 0 ]; then echo \"❌ Weather page did not return expected data\"; exit 1; fi && " +
                "echo \"✅ Cross-environment reference verified: webfrontend (App Service) successfully called apiservice (ACA)\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Step 12: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterWithWebFrontendOnAppServiceAndApiServiceOnAca),
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
                nameof(DeployStarterWithWebFrontendOnAppServiceAndApiServiceOnAca),
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
