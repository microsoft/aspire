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
    // Timeout set to 40 minutes to allow for Azure Front Door and ACA provisioning.
    // Full deployments can take up to 30 minutes if Azure infrastructure is backed up.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task DeployReactTemplateWithFrontDoor()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployReactTemplateWithFrontDoorCore(cancellationToken);
    }

    /// <summary>
    /// Deploys one application to two Azure regions as regional stamps, behind a single Front Door hostname.
    /// </summary>
    /// <remarks>
    /// This is the global entry point topology: two Azure Container Apps environments in different regions,
    /// the server project stamped across both, and one Front Door origin group holding an origin per stamp.
    /// The verification checks that both regional Container Apps are reachable directly, which is what proves
    /// each stamp landed in its own region and is healthy enough for Front Door to route to.
    /// </remarks>
    [Fact]
    public async Task DeployMultiRegionTemplateWithFrontDoor()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployMultiRegionTemplateWithFrontDoorCore(cancellationToken);
    }

    private async Task DeployMultiRegionTemplateWithFrontDoorCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("fdmultiregion");
        var projectName = "FrontDoorMultiRegionApp";

        output.WriteLine($"Test: {nameof(DeployMultiRegionTemplateWithFrontDoor)}");
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

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                output.WriteLine("Step 2: Using pre-installed Aspire CLI from local build...");
                await auto.SourceAspireCliEnvironmentAsync(counter);
            }

            output.WriteLine("Step 3: Creating React + ASP.NET Core project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.JsReact, useRedisCache: false);

            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // See DeployReactTemplateWithFrontDoorCore for why WaitForAspireAddCompletionAsync is used here.
            output.WriteLine("Step 5: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));

            output.WriteLine("Step 5b: Adding Azure Front Door hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.FrontDoor");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));

            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // With more than one compute environment in the model every compute resource must be bound
            // explicitly, so webfrontend is pinned to the primary region while server is stamped across both.
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Two regional Azure Container App Environments
var acaEastUs = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
var acaWestUs = builder.AddAzureContainerAppEnvironment("aca-westus").WithLocation("westus3");

// Deploy the server to both regions as regional stamps
server.WithComputeEnvironments(acaEastUs, acaWestUs);
webfrontend.WithComputeEnvironment(acaEastUs);

// One global entry point in front of both stamps
builder.AddAzureFrontDoor("frontdoor")
    .WithOriginGroup(server, g => g
        .WithRouting(FrontDoorOriginRouting.LatencyBased)
        .WithHealthProbe("/health", FrontDoorHealthProbeProtocol.Https, TimeSpan.FromSeconds(30)));

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // AZURE__LOCATION still sets the region of the resource group and of any resource that is not
            // pinned with WithLocation. The two Container Apps environments override it per stamp.
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 9: Starting multi-region Azure deployment with Front Door...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Verify that two Container Apps environments were created, one per region, and that the stamped
            // server produced a container app in each of them. Front Door itself is covered by the successful
            // deployment: querying it with `az afd` would trigger an interactive `cdn` extension install, and
            // its DNS takes 5-15 minutes to propagate.
            output.WriteLine("Step 10: Verifying both regional stamps...");
            await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
                  "echo \"Resource group: $RG_NAME\" && " +
                  "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                  // Two managed environments, one per region.
                  "envs=$(az containerapp env list -g \"$RG_NAME\" --query \"[].location\" -o tsv 2>/dev/null | sort -u) && " +
                  "env_count=$(echo \"$envs\" | grep -c . ) && " +
                  "echo \"Container Apps environment regions: $envs\" && " +
                  "if [ \"$env_count\" -lt 2 ]; then echo \"❌ Expected 2 regions, found $env_count\"; exit 1; fi && " +
                  // Two stamps of the server, one per environment.
                  "stamps=$(az containerapp list -g \"$RG_NAME\" --query \"[?starts_with(name, 'server')].name\" -o tsv 2>/dev/null) && " +
                  "stamp_count=$(echo \"$stamps\" | grep -c . ) && " +
                  "echo \"Server stamps: $stamps\" && " +
                  "if [ \"$stamp_count\" -lt 2 ]; then echo \"❌ Expected 2 server stamps, found $stamp_count\"; exit 1; fi && " +
                  "urls=$(az containerapp list -g \"$RG_NAME\" --query \"[].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | grep -v '\\.internal\\.') && " +
                  "if [ -z \"$urls\" ]; then echo \"❌ No external container app endpoints found\"; exit 1; fi && " +
                  "failed=0 && " +
                  // A single shared deadline keeps the loop bounded regardless of how many endpoints exist.
                  "deadline=$(( $(date +%s) + 660 )) && " +
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
                  "done && " +
                  "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more endpoint checks failed\"; exit 1; fi");
            await auto.EnterAsync();
            // Must exceed the in-terminal deadline plus the final in-flight `curl --max-time 30`; see
            // DeployReactTemplateWithFrontDoorCore for the full rationale.
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(15));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployMultiRegionTemplateWithFrontDoor),
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
                nameof(DeployMultiRegionTemplateWithFrontDoor),
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

    private async Task DeployReactTemplateWithFrontDoorCore(CancellationToken cancellationToken)
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
        var projectName = "FrontDoorApp";

        output.WriteLine($"Test: {nameof(DeployReactTemplateWithFrontDoor)}");
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

            // Step 6: Modify AppHost.cs to add Azure Container App Environment and Front Door
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert Azure infrastructure before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Add Azure Container App Environment for deployment
builder.AddAzureContainerAppEnvironment("aca");

// Add Azure Front Door in front of the server
builder.AddAzureFrontDoor("frontdoor")
    .WithOrigin(server);

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
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
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Front Door provisioning is covered by the successful deployment above. Do not query it
            // through `az afd` here: recent Azure CLI versions dynamically install the `cdn` extension,
            // which can block this interactive terminal waiting for installation confirmation. Front Door
            // HTTP checks are also intentionally skipped because DNS propagation can take 5-15 minutes.
            output.WriteLine("Step 10: Verifying deployed ACA endpoints...");
            await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
                  "echo \"Resource group: $RG_NAME\" && " +
                  "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                  // Check ACA endpoints (exclude internal endpoints)
                  "urls=$(az containerapp list -g \"$RG_NAME\" --query \"[].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | grep -v '\\.internal\\.') && " +
                  "if [ -z \"$urls\" ]; then echo \"❌ No external container app endpoints found\"; exit 1; fi && " +
                  "failed=0 && " +
                  // Share a single deadline across every endpoint (see the rationale comment below the
                  // command) so the whole loop stays bounded no matter how many endpoints are returned.
                  "deadline=$(( $(date +%s) + 660 )) && " +
                  // Verify ACA endpoints
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
                  "done && " +
                  "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more endpoint checks failed\"; exit 1; fi");
            await auto.EnterAsync();
            // The in-terminal verification loop above checks every external ACA endpoint, retrying each
            // with `curl --max-time 30` + `sleep 10`. `urls` can contain more than one endpoint (for
            // example the workload plus the Aspire dashboard), and the endpoints are checked
            // sequentially, so a per-endpoint retry budget would let the total runtime grow with the
            // number of endpoints. Instead the loop shares a single ~11-minute deadline across all
            // endpoints (each still gets at least one attempt), which keeps the worst case bounded.
            // The outer success-prompt wait must exceed that deadline plus the final in-flight
            // `curl --max-time 30`, so 15 minutes covers it; a shorter wait would abandon a
            // still-running (and often eventually-successful) loop and fail an otherwise healthy deploy.
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(15));

            // Step 11: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployReactTemplateWithFrontDoor),
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
                nameof(DeployReactTemplateWithFrontDoor),
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
