// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the ACR purge task feature (WithPurgeTask / GetAzureContainerRegistry).
/// Deploys a Python starter app twice, runs the purge task, and verifies images were cleaned up.
/// </summary>
public sealed class AcrPurgeTaskDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 30 minutes to allow for two Azure deployments and purge verification.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task DeployPythonStarterWithPurgeTask()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployPythonStarterWithPurgeTaskCore(cancellationToken);
    }

    private async Task DeployPythonStarterWithPurgeTaskCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("acr-purge");
        var projectName = "AcrPurge";

        output.WriteLine($"Test: {nameof(DeployPythonStarterWithPurgeTask)}");
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
            // Python apphosts need the full bundle because
            // the prebuilt AppHost server is required for aspire new with Python templates.
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                var prNumber = DeploymentE2ETestHelpers.GetPrNumber();
                if (prNumber > 0)
                {
                    output.WriteLine($"Step 2: Installing Aspire bundle from PR #{prNumber}...");
                    await auto.InstallAspireBundleFromPullRequestAsync(prNumber, counter);
                }
                await auto.SourceAspireBundleEnvironmentAsync(counter);
            }

            // Step 3: Create Python FastAPI project using aspire new
            output.WriteLine("Step 3: Creating Python FastAPI project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.PythonReact, useRedisCache: false);

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
                await auto.WaitForAspireAddCompletionAsync(counter);
            }
            else
            {
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));
            }

            // Step 6: Modify apphost.ts to add ACA environment with purge task
            // Python template uses TypeScript AppHost (apphost.ts in project root)
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostFilePath = Path.Combine(projectDir, "apphost.ts");

            output.WriteLine($"Looking for apphost.ts at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Add Azure Container App Environment with purge task before build().run()
            content = content.Replace(
                "await builder.build().run();",
                """
// Add Azure Container App Environment and configure ACR purge task
const infra = await builder.addAzureContainerAppEnvironment("infra");
// Schedule once a month so it never fires during the test; the task is triggered manually via az acr task run
await infra.getAzureContainerRegistry()
    .withPurgeTask("0 0 1 * *", { keep: 1 });

await builder.build().run();
""");

            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified apphost.ts at: {appHostFilePath}");

            // Step 7: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: First deployment to Azure
            output.WriteLine("Step 8: Starting first Azure deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 9: Get the ACR name and count tags before second deploy
            output.WriteLine("Step 9: Getting ACR name and counting initial tags...");
            await auto.TypeAsync($"ACR_NAME=$(az acr list -g \"{resourceGroupName}\" --query \"[0].name\" -o tsv) && " +
                  "echo \"ACR: $ACR_NAME\" && " +
                  "if [ -z \"$ACR_NAME\" ]; then echo \"❌ No ACR found in resource group\"; exit 1; fi && " +
                  "REPOS=$(az acr repository list --name \"$ACR_NAME\" -o tsv) && " +
                  "echo \"Repositories after first deploy:\" && " +
                  "for repo in $REPOS; do " +
                  "TAGS=$(az acr repository show-tags --name \"$ACR_NAME\" --repository \"$repo\" -o tsv); " +
                  "TAG_COUNT=$(echo \"$TAGS\" | wc -l); " +
                  "echo \"  $repo: $TAG_COUNT tag(s) - $TAGS\"; " +
                  "done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 10: Modify Python code to guarantee a new container image is pushed on second deploy
            var projectDir2 = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var mainPyPath = Path.Combine(projectDir2, "app", "main.py");

            output.WriteLine($"Modifying {mainPyPath} to force new image...");

            var content2 = File.ReadAllText(mainPyPath);
            content2 += "\n# Force new image for E2E purge test\n";
            File.WriteAllText(mainPyPath, content2);

            output.WriteLine("Modified main.py to force a new container image build");

            // Step 11: Second deployment to push new images
            // Clear the terminal so WaitUntilTextAsync doesn't match the pipeline succeeded text from the first deploy
            output.WriteLine("Step 11: Starting second Azure deployment...");
            await auto.TypeAsync("export TERM=xterm && clear");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 12: Verify there are now multiple tags (from both deploys)
            output.WriteLine("Step 12: Verifying multiple tags exist after second deploy...");
            await auto.TypeAsync($"ACR_NAME=$(az acr list -g \"{resourceGroupName}\" --query \"[0].name\" -o tsv) && " +
                  "echo \"ACR: $ACR_NAME\" && " +
                  "REPOS=$(az acr repository list --name \"$ACR_NAME\" -o tsv) && " +
                  "echo \"Repositories after second deploy:\" && " +
                  "for repo in $REPOS; do " +
                  "TAGS=$(az acr repository show-tags --name \"$ACR_NAME\" --repository \"$repo\" -o tsv); " +
                  "TAG_COUNT=$(echo \"$TAGS\" | wc -l); " +
                  "echo \"  $repo: $TAG_COUNT tag(s) - $TAGS\"; " +
                  "done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 13: Run the purge task manually to trigger image cleanup
            // az acr task run is synchronous - it waits for completion and streams output
            output.WriteLine("Step 13: Running ACR purge task...");
            await auto.TypeAsync($"ACR_NAME=$(az acr list -g \"{resourceGroupName}\" --query \"[0].name\" -o tsv) && " +
                  "echo \"Running purge task on ACR: $ACR_NAME\" && " +
                  "az acr task run --name purgeOldImages --registry \"$ACR_NAME\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 14: Verify images were purged - only 1 tag should remain per repo
            output.WriteLine("Step 14: Verifying images were purged...");
            await auto.TypeAsync($"ACR_NAME=$(az acr list -g \"{resourceGroupName}\" --query \"[0].name\" -o tsv) && " +
                  "echo \"ACR: $ACR_NAME\" && " +
                  "REPOS=$(az acr repository list --name \"$ACR_NAME\" -o tsv) && " +
                  "if [ -z \"$REPOS\" ]; then echo \"❌ No repositories found in ACR - cannot verify purge\"; exit 1; fi && " +
                  "echo \"Repositories after purge:\" && " +
                  "all_ok=1 && " +
                  "for repo in $REPOS; do " +
                  "TAGS=$(az acr repository show-tags --name \"$ACR_NAME\" --repository \"$repo\" -o tsv); " +
                  "TAG_COUNT=$(echo \"$TAGS\" | wc -l); " +
                  "echo \"  $repo: $TAG_COUNT tag(s) - $TAGS\"; " +
                  "if [ \"$TAG_COUNT\" -gt 1 ]; then echo \"  ❌ Expected at most 1 tag after purge, got $TAG_COUNT\"; all_ok=0; fi; " +
                  "done && " +
                  "if [ \"$all_ok\" -eq 1 ]; then echo \"✅ Purge task verified - only 1 tag remains per repo\"; " +
                  "else echo \"❌ Purge task did not clean up as expected\"; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 15: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployPythonStarterWithPurgeTask),
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
                nameof(DeployPythonStarterWithPurgeTask),
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
