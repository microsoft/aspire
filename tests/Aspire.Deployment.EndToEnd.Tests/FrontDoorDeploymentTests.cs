// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications with Azure Front Door and Azure Container Apps.
/// </summary>
public sealed class FrontDoorDeploymentTests(ITestOutputHelper output)
{
    // Two regional ACA environments plus Front Door can take longer to converge under Azure load.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(90);

    [Fact]
    public async Task DeployReactTemplateWithRegionalFrontDoor()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployReactTemplateWithRegionalFrontDoorCore(cancellationToken);
    }

    private async Task DeployReactTemplateWithRegionalFrontDoorCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("frontdoor");
        var eastResourceGroupName = $"{resourceGroupName}-east";
        var westResourceGroupName = $"{resourceGroupName}-west";
        var projectName = "FrontDoorApp";

        output.WriteLine($"Test: {nameof(DeployReactTemplateWithRegionalFrontDoor)}");
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

            // Step 3: Create React + ASP.NET Core project
            output.WriteLine("Step 3: Creating React + ASP.NET Core project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.JsReact, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Azure Container Apps and Front Door hosting packages.
            // Use WaitForAspireAddCompletionAsync because `aspire add` only prompts for a
            // version when multiple candidates are found; when the package is resolved from
            // the local bundle (the typical CI case) the command installs directly with no
            // prompt, so a hard-coded WaitUntilText for the legacy "(based on NuGet.config)"
            // prompt times out. The helper covers both code paths.
            output.WriteLine("Step 5: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));

            output.WriteLine("Step 5b: Adding Azure Front Door hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.FrontDoor");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));

            output.WriteLine("Step 5c: Adding Azure Container Registry hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.ContainerRegistry");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));

            // Step 6: Modify AppHost.cs to add two regional environments and one global Front Door endpoint.
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert Azure infrastructure before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = $$"""
var registry = builder.AddAzureContainerRegistry("registry");

var eastGroup = builder.AddAzureResourceGroup("east-rg", "eastus2")
    .WithResourceGroupName("{{eastResourceGroupName}}");
var westGroup = builder.AddAzureResourceGroup("west-rg", "westus3")
    .WithResourceGroupName("{{westResourceGroupName}}");

var east = builder.AddAzureContainerAppEnvironment("east")
    .WithLocation("eastus2")
    .WithResourceGroup(eastGroup)
    .WithAzureContainerRegistry(registry);
var west = builder.AddAzureContainerAppEnvironment("west")
    .WithLocation("westus3")
    .WithResourceGroup(westGroup)
    .WithAzureContainerRegistry(registry);

server.WithContainerRegistry(registry)
    .WithComputeEnvironments([east, west]);

builder.AddAzureFrontDoor("frontdoor")
    .WithOrigin(server);

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            content = "#pragma warning disable ASPIRECOMPUTE003\n#pragma warning disable ASPIRECOMPUTE004\n#pragma warning disable ASPIREAZURERG001\n" + content;
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            // Step 7: Navigate to AppHost project directory
            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy to Azure
            output.WriteLine("Step 9: Starting Azure deployment with Front Door...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(45));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Use generic `az resource` queries rather than `az afd`; the latter may block while
            // dynamically installing the CDN extension in the interactive test terminal.
            output.WriteLine("Step 10: Verifying regional ACA endpoints and global Front Door...");
            await auto.TypeAsync($"PRIMARY_RG=\"{resourceGroupName}\" && EAST_RG=\"{eastResourceGroupName}\" && WEST_RG=\"{westResourceGroupName}\" && " +
                  "for rg in \"$PRIMARY_RG\" \"$EAST_RG\" \"$WEST_RG\"; do if ! az group show -n \"$rg\" &>/dev/null; then echo \"❌ Resource group $rg not found\"; exit 1; fi; done && " +
                  "failed=0 && " +
                  "deadline=$(( $(date +%s) + 660 )) && " +
                  "for rg in \"$EAST_RG\" \"$WEST_RG\"; do " +
                  "urls=$(az containerapp list -g \"$rg\" --query \"[].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | grep -v '\\.internal\\.') && " +
                  "if [ -z \"$urls\" ]; then echo \"❌ No external container app endpoints found in $rg\"; exit 1; fi; " +
                  "for url in $urls; do " +
                  "echo \"Checking ACA https://$url...\"; " +
                  "success=0; " +
                  "while true; do " +
                  "STATUS=$(curl -s -o /dev/null -w \"%{http_code}\" \"https://$url\" --max-time 30 2>/dev/null); " +
                  "if [ \"$STATUS\" = \"200\" ] || [ \"$STATUS\" = \"302\" ]; then echo \"  ✅ $STATUS\"; success=1; break; fi; " +
                  "if [ \"$(date +%s)\" -ge \"$deadline\" ]; then break; fi; " +
                  "echo \"  $STATUS, retrying in 10s...\"; sleep 10; " +
                  "done; " +
                  "if [ \"$success\" -eq 0 ]; then echo \"  ❌ $url not reachable before deadline\"; failed=1; fi; " +
                  "done; done && " +
                  "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more regional endpoint checks failed\"; exit 1; fi && " +
                  "origin_count=$(az resource list -g \"$PRIMARY_RG\" --resource-type \"Microsoft.Cdn/profiles/originGroups/origins\" --query \"length(@)\" -o tsv) && " +
                  "if [ \"$origin_count\" -lt 2 ]; then echo \"❌ Expected two Front Door origins, found $origin_count\"; exit 1; fi && " +
                  "fd_host=$(az resource list -g \"$PRIMARY_RG\" --resource-type \"Microsoft.Cdn/profiles/afdEndpoints\" --query \"[0].properties.hostName\" -o tsv) && " +
                  "if [ -z \"$fd_host\" ]; then echo \"❌ Front Door endpoint not found\"; exit 1; fi && " +
                  "echo \"Checking Front Door https://$fd_host...\" && success=0 && deadline=$(( $(date +%s) + 900 )) && " +
                  "while true; do STATUS=$(curl -s -o /dev/null -w \"%{http_code}\" \"https://$fd_host\" --max-time 30 2>/dev/null); " +
                  "if [ \"$STATUS\" = \"200\" ] || [ \"$STATUS\" = \"302\" ]; then echo \"  ✅ $STATUS\"; success=1; break; fi; " +
                  "if [ \"$(date +%s)\" -ge \"$deadline\" ]; then break; fi; echo \"  $STATUS, retrying in 15s...\"; sleep 15; done && " +
                  "if [ \"$success\" -eq 0 ]; then echo \"❌ Front Door endpoint did not become healthy\"; exit 1; fi");
            await auto.EnterAsync();
            // Regional checks share an 11-minute deadline, then Front Door gets a separate 15-minute
            // DNS/probe convergence budget. The outer wait covers both bounded phases.
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(30));

            // Step 11: Verify Aspire-owned primary and regional groups are all cleaned up.
            await auto.AspireDestroyAsync(counter);

            // Step 12: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployReactTemplateWithRegionalFrontDoor),
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
                nameof(DeployReactTemplateWithRegionalFrontDoor),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName, output);
            TriggerCleanupResourceGroup(eastResourceGroupName, output);
            TriggerCleanupResourceGroup(westResourceGroupName, output);
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
