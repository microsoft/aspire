// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications to Azure Container Apps.
/// </summary>
public sealed class AcaStarterDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 40 minutes to allow for Azure provisioning.
    // Full deployments can take up to 30 minutes if Azure infrastructure is backed up.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task DeployStarterTemplateToAzureContainerApps()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterTemplateToAzureContainerAppsCore(false, nameof(DeployStarterTemplateToAzureContainerApps), cancellationToken);
    }

    internal async Task DeployStarterTemplateToAzureContainerAppsCore(bool useDotnetProject, string testName, CancellationToken cancellationToken)
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

        using var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var deploymentUrls = new Dictionary<string, string>();
        // Generate a unique resource group name with pattern: e2e-[testcasename]-[runid]-[attempt]
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName(useDotnetProject ? "starter-v2" : "starter");
        // Project name can be simpler since resource group is explicitly set
        var projectName = "AcaStarter";

        output.WriteLine($"Test: {testName}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal(testName: testName);
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

            // Step 3: Create starter project using aspire new with interactive prompts
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Azure.AppContainers package
            output.WriteLine("Step 5: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            // aspire add may show a version selection prompt
            await auto.WaitForAspireAddCompletionAsync(counter);
            if (useDotnetProject)
            {
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Dotnet");
            }

            // Step 6: Modify AppHost.cs to add Azure Container App Environment
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert the Azure Container App Environment before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Add Azure Container App Environment for deployment
builder.AddAzureContainerAppEnvironment("infra");

builder.Build().Run();
""";

            content = DotnetProjectDeploymentHelpers.ReplaceExactlyOnce(content, buildRunPattern, replacement);
            if (useDotnetProject)
            {
                content = "#pragma warning disable ASPIREDOTNETPROJECT001\n" + content;
                foreach (var (suffix, resource) in new[] { ("ApiService", "apiservice"), ("Web", "webfrontend") })
                {
                    content = DotnetProjectDeploymentHelpers.ReplaceExactlyOnce(content,
                        $"AddProject<Projects.{projectName}_{suffix}>(\"{resource}\")",
                        $"AddDotnetProject(\"{resource}\", \"../{projectName}.{suffix}/{projectName}.{suffix}.csproj\")");
                }
            }

            File.WriteAllText(appHostFilePath, content);
            var marker = $"aca-{Guid.NewGuid():N}";
            DotnetProjectDeploymentHelpers.ReplaceInFile(Path.Combine(projectDir, $"{projectName}.ApiService", "Program.cs"),
                "app.Run();", $$"""
                app.MapGet("/deployment-marker", () => "{{marker}}");
                app.Run();
                """);
            DotnetProjectDeploymentHelpers.ReplaceInFile(Path.Combine(projectDir, $"{projectName}.Web", "Program.cs"),
                "app.Run();", $$"""
                app.MapGet("/deployment-marker", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
                    "web:" + await clients.CreateClient().GetStringAsync("https+http://apiservice/deployment-marker", cancellationToken));
                app.Run();
                """);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            // Step 7: Navigate to AppHost project directory
            output.WriteLine("Step 6: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Set environment variables for deployment
            // - Unset ASPIRE_PLAYGROUND to avoid conflicts
            // - Set Azure location
            // - Set AZURE__RESOURCEGROUP to use our unique resource group name
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP={resourceGroupName} AZURE__SUBSCRIPTIONID={subscriptionId}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy to Azure Container Apps using aspire deploy
            // Use --clear-cache to ensure fresh deployment without cached location from previous runs
            output.WriteLine("Step 7: Starting Azure Container Apps deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            // Wait for pipeline to complete successfully
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // A fresh source marker traverses both .NET images and the template's service-discovery reference.
            // Checking arbitrary ingress URLs also reaches the dashboard and cannot prove this path works.
            var frontendUrlFile = Path.Combine(projectDir, "frontend-url.txt");
            await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, $$"""
                set -euo pipefail
                az() { command az "$@" --subscription {{subscriptionId}}; }
                for resource in apiservice webfrontend; do
                    app=$(az containerapp list -g {{resourceGroupName}} --query "[?starts_with(name, '$resource')].name" -o tsv)
                    [ "$(printf '%s\n' "$app" | wc -l)" -eq 1 ] && [ -n "$app" ]
                    revision=$(az containerapp show -g {{resourceGroupName}} -n "$app" --query properties.latestRevisionName -o tsv)
                    ready=""
                    for attempt in $(seq 1 18); do
                        ready=$(az containerapp show -g {{resourceGroupName}} -n "$app" --query properties.latestReadyRevisionName -o tsv)
                        [ -n "$revision" ] && [ "$revision" = "$ready" ] && break
                        sleep 10
                    done
                    [ -n "$revision" ] && [ "$revision" = "$ready" ]
                    image=$(az containerapp show -g {{resourceGroupName}} -n "$app" --query "properties.template.containers[0].image" -o tsv)
                    running_image=$(az containerapp revision show -g {{resourceGroupName}} -n "$app" --revision "$revision" --query "properties.template.containers[0].image" -o tsv)
                    case "$image" in *.azurecr.io/*) ;; *) exit 1;; esac
                    [ "$image" = "$running_image" ]
                    echo "$resource: $revision $running_image"
                    if [ "$resource" = webfrontend ]; then
                        az containerapp show -g {{resourceGroupName}} -n "$app" --query properties.configuration.ingress.fqdn -o tsv > {{AspireCliShellCommandHelpers.QuoteBashArg(frontendUrlFile)}}
                    fi
                done
                """, TimeSpan.FromMinutes(9));
            var frontendUrl = $"https://{File.ReadAllText(frontendUrlFile).Trim()}";
            await DotnetProjectDeploymentHelpers.VerifyResponseAsync($"{frontendUrl}/deployment-marker", $"web:{marker}", cancellationToken);
            deploymentUrls["webfrontend"] = frontendUrl;

            // Step 11: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                testName,
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
                testName,
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Clean up the resource group we created
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            await DotnetProjectDeploymentHelpers.CleanupAsync(resourceGroupName, subscriptionId, output);
        }
    }
}
