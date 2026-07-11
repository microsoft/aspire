// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications to Azure App Service.
/// </summary>
public sealed class AppServiceReactDeploymentTests(ITestOutputHelper output)
{
    // This test deploys both the initial slot-enabled site and its VNet-integration upgrade.
    // Two 30-minute provisioning operations plus bounded verification waits leave time for
    // template creation, package installation, and the commands between deployments.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(85);

    [Fact]
    public async Task DeployReactTemplateToAzureAppServiceWithDelegatedSubnet()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployReactTemplateToAzureAppServiceWithDelegatedSubnetCore(cancellationToken);
    }

    private async Task DeployReactTemplateToAzureAppServiceWithDelegatedSubnetCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("appservice-vnet");
        // Project name can be simpler since resource group is explicitly set
        var projectName = "ReactAppSvc";

        output.WriteLine($"Test: {nameof(DeployReactTemplateToAzureAppServiceWithDelegatedSubnet)}");
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

            // Step 2: Set up CLI environment
            // The workflow builds and installs the CLI to ~/.aspire/bin before running tests
            // We just need to source it in the bash session
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            // Step 3: Create React + ASP.NET Core project using aspire new with interactive prompts
            output.WriteLine("Step 3: Creating React + ASP.NET Core project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.JsReact, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Azure.AppService package (instead of AppContainers)
            output.WriteLine("Step 5: Adding Azure App Service hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppService");
            await auto.EnterAsync();

            // aspire add may show a version selection prompt
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Add Azure Virtual Network hosting package
            output.WriteLine("Step 6: Adding Azure Virtual Network hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Network");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 7: Configure the first deployment with a staging slot but without VNet integration.
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            const string buildRunPattern = "builder.Build().Run();";
            const string initialEnvironmentConfiguration = """
builder.AddAzureAppServiceEnvironment("infra")
    .WithDeploymentSlot("stage");
""";
            const string upgradedEnvironmentConfiguration = """
#pragma warning disable ASPIREAZURE003 // Azure Virtual Network APIs are experimental.
var vnet = builder.AddAzureVirtualNetwork("vnet");
var subnet = vnet.AddSubnet("app-service-subnet", "10.0.0.0/24");
builder.AddAzureAppServiceEnvironment("infra")
    .WithDelegatedSubnet(subnet)
    .WithDeploymentSlot("stage");
#pragma warning restore ASPIREAZURE003
""";

            var content = File.ReadAllText(appHostFilePath);
            var initialAppHostConfiguration = $"""
// Add Azure App Service Environment with a staging slot for deployment.
{initialEnvironmentConfiguration}

{buildRunPattern}
""";
            if (!content.Contains(buildRunPattern, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Could not find '{buildRunPattern}' in the generated AppHost.");
            }

            content = content.Replace(buildRunPattern, initialAppHostConfiguration, StringComparison.Ordinal);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            // Step 8: Navigate to AppHost project directory
            output.WriteLine("Step 8: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Set environment variables for deployment
            // - Unset ASPIRE_PLAYGROUND to avoid conflicts
            // - Set Azure location
            // - Set AZURE__RESOURCEGROUP to use our unique resource group name
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 10: Deploy the site and slot without VNet integration.
            output.WriteLine("Step 10: Starting the initial Azure App Service deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // The protected production-site path is only exercised when VNet integration is added
            // after the slot-enabled site already exists.
            output.WriteLine("Step 11: Verifying the initial deployment has no VNet integration...");
            await VerifyNoVnetIntegrationAsync(auto, counter, resourceGroupName);

            // Step 12: Add the delegated subnet before redeploying to the same resource group.
            content = File.ReadAllText(appHostFilePath);
            var upgradedContent = content.Replace(
                initialEnvironmentConfiguration,
                upgradedEnvironmentConfiguration,
                StringComparison.Ordinal);
            if (upgradedContent == content)
            {
                throw new InvalidOperationException("Could not add regional VNet integration to the generated AppHost.");
            }

            File.WriteAllText(appHostFilePath, upgradedContent);

            // Step 13: Upgrade the existing production site, staging slot, and dashboard.
            output.WriteLine("Step 13: Upgrading the deployment with regional VNet integration...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 14: Extract deployment URLs and verify endpoints with retry
            // The workload is deployed to its staging slot, so the production site's empty
            // hostname is not a liveness target. Sites without slots, such as the dashboard,
            // are reached through their production hostname.
            // Retry each endpoint for up to 3 minutes (18 attempts * 10 seconds)
            output.WriteLine("Step 14: Verifying deployed endpoints...");
            await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
                  "echo \"Resource group: $RG_NAME\" && " +
                  "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                  "webapps=$(az webapp list -g \"$RG_NAME\" --query \"[].name\" -o tsv 2>/dev/null) && " +
                  "if [ -z \"$webapps\" ]; then echo \"❌ No App Service sites found\"; exit 1; fi && " +
                  "urls='' && staging_endpoint_count=0 && dashboard_endpoint_count=0 && " +
                  "for webapp in $webapps; do " +
                  "slots=$(az webapp deployment slot list -g \"$RG_NAME\" -n \"$webapp\" --query \"[].name\" -o tsv 2>/dev/null); " +
                  "if [ -n \"$slots\" ]; then " +
                  "for slot in $slots; do " +
                  "if ! url=$(az webapp show -g \"$RG_NAME\" -n \"$webapp\" --slot \"$slot\" --query defaultHostName -o tsv); then echo \"❌ Failed to query hostname for staging slot $webapp/$slot\"; exit 1; fi; " +
                  "if [ -z \"$url\" ]; then echo \"❌ Missing hostname for staging slot $webapp/$slot\"; exit 1; fi; " +
                  "urls=\"$urls $url\"; staging_endpoint_count=$((staging_endpoint_count + 1)); " +
                  "done; " +
                  "else " +
                  "if ! url=$(az webapp show -g \"$RG_NAME\" -n \"$webapp\" --query defaultHostName -o tsv); then echo \"❌ Failed to query hostname for dashboard $webapp\"; exit 1; fi; " +
                  "if [ -z \"$url\" ]; then echo \"❌ Missing hostname for dashboard $webapp\"; exit 1; fi; " +
                  "urls=\"$urls $url\"; dashboard_endpoint_count=$((dashboard_endpoint_count + 1)); " +
                  "fi; " +
                  "done && " +
                  "if [ \"$staging_endpoint_count\" -ne 1 ] || [ \"$dashboard_endpoint_count\" -ne 1 ]; then echo \"❌ Expected one staging slot endpoint and one dashboard endpoint\"; exit 1; fi && " +
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

            // Step 15: Verify that the production site, staging slot, and dashboard use the delegated subnet.
            output.WriteLine("Step 15: Verifying regional VNet integration...");
            await VerifyVnetIntegrationAsync(auto, counter, resourceGroupName);

            // Step 16: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployReactTemplateToAzureAppServiceWithDelegatedSubnet),
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
                nameof(DeployReactTemplateToAzureAppServiceWithDelegatedSubnet),
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

    private static async Task VerifyNoVnetIntegrationAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string resourceGroupName)
    {
        await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
            "webapps=$(az webapp list -g \"$RG_NAME\" --query \"[].name\" -o tsv 2>/dev/null) && " +
            "if [ -z \"$webapps\" ]; then echo \"ERROR: No App Service sites found\"; exit 1; fi && " +
            "webapp_count=0 && slot_count=0 && " +
            "for webapp in $webapps; do " +
            "webapp_count=$((webapp_count + 1)); " +
            "if ! subnet_id=$(az webapp show -g \"$RG_NAME\" -n \"$webapp\" --query \"virtualNetworkSubnetId\" -o tsv); then echo \"ERROR: Failed to query VNet integration for $webapp\"; exit 1; fi; " +
            "if [ -n \"$subnet_id\" ]; then echo \"ERROR: $webapp unexpectedly has VNet integration\"; exit 1; fi; " +
            "slots=$(az webapp deployment slot list -g \"$RG_NAME\" -n \"$webapp\" --query \"[].name\" -o tsv 2>/dev/null); " +
            "for slot in $slots; do " +
            "slot_count=$((slot_count + 1)); " +
            "if ! subnet_id=$(az webapp show -g \"$RG_NAME\" -n \"$webapp\" --slot \"$slot\" --query \"virtualNetworkSubnetId\" -o tsv); then echo \"ERROR: Failed to query VNet integration for $webapp/$slot\"; exit 1; fi; " +
            "if [ -n \"$subnet_id\" ]; then echo \"ERROR: $webapp/$slot unexpectedly has VNet integration\"; exit 1; fi; " +
            "done; " +
            "done && " +
            "if [ \"$webapp_count\" -ne 2 ] || [ \"$slot_count\" -ne 1 ]; then echo \"ERROR: Expected two sites and one staging slot\"; exit 1; fi && " +
            "echo \"Verified no VNet integration on the initial deployment\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }

    private static async Task VerifyVnetIntegrationAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string resourceGroupName)
    {
        await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
            "vnet_name=$(az network vnet list -g \"$RG_NAME\" --query \"[?subnets[?name == 'app-service-subnet']].name | [0]\" -o tsv 2>/dev/null) && " +
            "expected_subnet_id=$(az network vnet subnet show -g \"$RG_NAME\" --vnet-name \"$vnet_name\" --name app-service-subnet --query \"id\" -o tsv 2>/dev/null) && " +
            "if [ -z \"$expected_subnet_id\" ]; then echo \"ERROR: Delegated subnet not found\"; exit 1; fi && " +
            "delegation=$(az network vnet subnet show --ids \"$expected_subnet_id\" --query \"delegations[?serviceName == 'Microsoft.Web/serverFarms'].serviceName | [0]\" -o tsv 2>/dev/null) && " +
            "if [ \"$delegation\" != \"Microsoft.Web/serverFarms\" ]; then echo \"ERROR: Subnet is not delegated to Microsoft.Web/serverFarms\"; exit 1; fi && " +
            "webapps=$(az webapp list -g \"$RG_NAME\" --query \"[].name\" -o tsv 2>/dev/null) && " +
            "if [ -z \"$webapps\" ]; then echo \"ERROR: No App Service sites found\"; exit 1; fi && " +
            // Keep the interactive shell alive after successful verification so Hex1b can observe its prompt.
            "integration_verified=0 && " +
            "for attempt in $(seq 1 12); do " +
            "all_integrated=1; webapp_count=0; slot_count=0; " +
            "for webapp in $webapps; do " +
            "webapp_count=$((webapp_count + 1)); " +
            "subnet_id=$(az webapp show -g \"$RG_NAME\" -n \"$webapp\" --query \"virtualNetworkSubnetId\" -o tsv 2>/dev/null); " +
            "if [ \"$subnet_id\" != \"$expected_subnet_id\" ]; then all_integrated=0; fi; " +
            "slots=$(az webapp deployment slot list -g \"$RG_NAME\" -n \"$webapp\" --query \"[].name\" -o tsv 2>/dev/null); " +
            "for slot in $slots; do " +
            "slot_count=$((slot_count + 1)); " +
            "subnet_id=$(az webapp show -g \"$RG_NAME\" -n \"$webapp\" --slot \"$slot\" --query \"virtualNetworkSubnetId\" -o tsv 2>/dev/null); " +
            "if [ \"$subnet_id\" != \"$expected_subnet_id\" ]; then all_integrated=0; fi; " +
            "done; " +
            "done; " +
            "if [ \"$all_integrated\" -eq 1 ] && [ \"$webapp_count\" -eq 2 ] && [ \"$slot_count\" -eq 1 ]; then echo \"Verified VNet integration on production, staging, and dashboard\"; integration_verified=1; break; fi; " +
            "echo \"Waiting for VNet integration (attempt $attempt)...\"; sleep 10; " +
            "done && " +
            "if [ \"$integration_verified\" -ne 1 ]; then echo \"ERROR: VNet integration was not configured on every site and slot\"; exit 1; fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));
    }

    /// <summary>
    /// Triggers cleanup of a specific resource group.
    /// This is fire-and-forget - the hourly cleanup workflow handles any missed resources.
    /// </summary>
    private static void TriggerCleanupResourceGroup(string resourceGroupName, ITestOutputHelper output)
    {
        // Fire and forget - trigger deletion of the specific resource group created by this test
        // The cleanup workflow will handle any that don't get deleted
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
