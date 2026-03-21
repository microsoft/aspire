// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire React starter template with Azure Managed Redis to Azure Container Apps.
/// Validates that Azure Managed Redis with Entra ID authentication (WithAzureAuthentication) works end-to-end.
/// </summary>
public sealed class AcaManagedRedisDeploymentTests(ITestOutputHelper output)
{
    // Azure Managed Redis typically provisions in ~5 minutes.
    // Set timeout to 30 minutes to allow for provisioning plus app deployment.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task DeployStarterWithManagedRedisToAzureContainerApps()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterWithManagedRedisToAzureContainerAppsCore(cancellationToken);
    }

    private async Task DeployStarterWithManagedRedisToAzureContainerAppsCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("acaredis");
        var projectName = "AcaRedis";

        output.WriteLine($"Test: {nameof(DeployStarterWithManagedRedisToAzureContainerApps)}");
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

            // Step 1b: Register Microsoft.Cache provider (required for Azure Managed Redis zone support)
            output.WriteLine("Step 1b: Registering Microsoft.Cache resource provider...");
            await auto.TypeAsync("az provider register --namespace Microsoft.Cache --wait");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 2: Set up CLI environment (in CI)
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                output.WriteLine("Step 2: Using pre-installed Aspire CLI from local build...");
                await auto.SourceAspireCliEnvironmentAsync(counter);
            }

            // Step 3: Create starter project (React) with Redis enabled
            output.WriteLine("Step 3: Creating React starter project with Redis...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.JsReact, useRedisCache: true);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Azure.AppContainers package
            output.WriteLine("Step 5: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync(); // select first version (PR build)
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 6: Add Aspire.Hosting.Azure.Redis package
            output.WriteLine("Step 6: Adding Azure Redis hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Redis");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync(); // select first version (PR build)
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 7: Add Aspire.Microsoft.Azure.StackExchangeRedis to Server project for WithAzureAuthentication
            // Use --prerelease because this package may only be available as a prerelease version
            output.WriteLine("Step 7: Adding Azure StackExchange Redis client package to Server project...");
            await auto.TypeAsync($"dotnet add {projectName}.Server/{projectName}.Server.csproj package Aspire.Microsoft.Azure.StackExchangeRedis --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Step 8: Modify AppHost.cs - Replace AddRedis with AddAzureManagedRedis and add ACA environment
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Modifying AppHost.cs at: {appHostFilePath}");

            var appHostContent = File.ReadAllText(appHostFilePath);

            // Replace AddRedis("cache") with AddAzureManagedRedis("cache")
            appHostContent = appHostContent.Replace(
                "builder.AddRedis(\"cache\")",
                "builder.AddAzureManagedRedis(\"cache\")");

            // Insert the Azure Container App Environment before builder.Build().Run();
            appHostContent = appHostContent.Replace(
                "builder.Build().Run();",
                """
                // Add Azure Container App Environment for deployment
                builder.AddAzureContainerAppEnvironment("infra");

                builder.Build().Run();
                """);
            File.WriteAllText(appHostFilePath, appHostContent);

            output.WriteLine($"Modified AppHost.cs: replaced AddRedis with AddAzureManagedRedis, added ACA environment");
            output.WriteLine($"New content:\n{appHostContent}");

            // Step 9: Modify Server Program.cs - Add WithAzureAuthentication for Azure Managed Redis
            var serverDir = Path.Combine(projectDir, $"{projectName}.Server");
            var programFilePath = Path.Combine(serverDir, "Program.cs");

            output.WriteLine($"Modifying Server Program.cs at: {programFilePath}");

            var programContent = File.ReadAllText(programFilePath);

            // The React template uses AddRedisClientBuilder("cache").WithOutputCache()
            // Add .WithAzureAuthentication() to the chain
            programContent = programContent.Replace(
                ".WithOutputCache();",
                """
                .WithOutputCache()
                    .WithAzureAuthentication();
                """);

            File.WriteAllText(programFilePath, programContent);

            output.WriteLine($"Modified Server Program.cs: added WithAzureAuthentication to Redis client builder");
            output.WriteLine($"New content:\n{programContent}");

            // Step 10: Navigate to AppHost project directory
            output.WriteLine("Step 10: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 11: Set environment variables for deployment
            // Use eastus for Azure Managed Redis availability zone support
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=eastus && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 12: Deploy to Azure Container Apps
            // Azure Managed Redis provisioning typically takes ~5 minutes
            output.WriteLine("Step 12: Starting Azure Container Apps deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            // Wait for pipeline to complete successfully
            await auto.WaitUntilTextAsync("PIPELINE SUCCEEDED", timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 13: Verify deployed endpoints with retry
            // Retry each endpoint for up to 3 minutes (18 attempts * 10 seconds)
            output.WriteLine("Step 13: Verifying deployed endpoints...");
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

            // Step 14: Verify /api/weatherforecast returns valid JSON (exercises Redis output cache)
            output.WriteLine("Step 14: Verifying /api/weatherforecast returns valid JSON...");
            await auto.TypeAsync(
                $"RG_NAME=\"{resourceGroupName}\" && " +
                // Get the server container app FQDN
                "SERVER_FQDN=$(az containerapp list -g \"$RG_NAME\" --query \"[?contains(name,'server')].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | head -1) && " +
                "if [ -z \"$SERVER_FQDN\" ]; then echo \"❌ Server container app not found\"; exit 1; fi && " +
                "echo \"Server FQDN: $SERVER_FQDN\" && " +
                // Retry fetching /api/weatherforecast and validate JSON
                "success=0 && " +
                "for i in $(seq 1 18); do " +
                "RESPONSE=$(curl -s \"https://$SERVER_FQDN/api/weatherforecast\" --max-time 10 2>/dev/null) && " +
                "echo \"$RESPONSE\" | python3 -m json.tool > /dev/null 2>&1 && " +
                "echo \"  ✅ Valid JSON response (attempt $i)\" && echo \"$RESPONSE\" | head -c 200 && echo && success=1 && break; " +
                "echo \"  Attempt $i: not valid JSON yet, retrying in 10s...\"; sleep 10; " +
                "done && " +
                "if [ \"$success\" -eq 0 ]; then echo \"❌ /api/weatherforecast did not return valid JSON after 18 attempts\"; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 15: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterWithManagedRedisToAzureContainerApps),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed - Aspire starter with Azure Managed Redis deployed to ACA!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterWithManagedRedisToAzureContainerApps),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Clean up the resource group
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
