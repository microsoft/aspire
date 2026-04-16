// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// Upgrade safety test: deploys with the GA Aspire CLI, then upgrades to the dev (PR) CLI
/// and redeploys WITHOUT enabling compact naming. Verifies that the default naming behavior
/// is unchanged — no duplicate storage accounts are created on upgrade.
/// </summary>
public sealed class AcaCompactNamingUpgradeDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Deploys with GA CLI → upgrades to dev CLI → redeploys same apphost → verifies
    /// no duplicate storage accounts were created (default naming unchanged).
    /// </summary>
    [Fact]
    public async Task UpgradeFromGaToDevDoesNotDuplicateStorageAccounts()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await UpgradeFromGaToDevDoesNotDuplicateStorageAccountsCore(linkedCts.Token);
    }

    private async Task UpgradeFromGaToDevDoesNotDuplicateStorageAccountsCore(CancellationToken cancellationToken)
    {
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("upgrade");

        output.WriteLine($"Test: {nameof(UpgradeFromGaToDevDoesNotDuplicateStorageAccounts)}");
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

            // ============================================================
            // Phase 1: Install GA CLI and deploy
            // ============================================================

            // Step 2: Back up the dev CLI (pre-installed by CI), then install the GA CLI
            output.WriteLine("Step 2: Backing up dev CLI and installing GA Aspire CLI...");
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.TypeAsync("cp ~/.aspire/bin/aspire /tmp/aspire-dev-backup && cp -r ~/.aspire/hives /tmp/aspire-hives-backup 2>/dev/null; echo 'dev CLI backed up'");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter);
            }
            await auto.InstallAspireCliReleaseAsync(counter);

            // Step 3: Source CLI environment
            output.WriteLine("Step 3: Configuring CLI environment...");
            await auto.SourceAspireCliEnvironmentAsync(counter);

            // Step 4: Log the GA CLI version
            output.WriteLine("Step 4: Logging GA CLI version...");
            await auto.TypeAsync("aspire --version");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Create single-file AppHost with GA CLI
            output.WriteLine("Step 5: Creating single-file AppHost with GA CLI...");
            var waitingForLanguageSelectionPrompt = new CellPatternSearcher()
                .Find("Which language would you like to use?");
            var waitingForTemplateVersionPrompt = new CellPatternSearcher()
                .Find("NuGet.config");
            var waitingForAgentInitPrompt = new CellPatternSearcher()
                .Find("configure AI agent environments");
            var waitingForSuccessPrompt = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");

            await auto.TypeAsync("aspire init");
            await auto.EnterAsync();

            var initState = "success";
            await auto.WaitUntilAsync(s =>
            {
                if (waitingForLanguageSelectionPrompt.Search(s).Count > 0)
                {
                    initState = "language";
                    return true;
                }

                if (waitingForTemplateVersionPrompt.Search(s).Count > 0)
                {
                    initState = "template-version";
                    return true;
                }

                if (waitingForAgentInitPrompt.Search(s).Count > 0)
                {
                    initState = "agent-init";
                    return true;
                }

                if (waitingForSuccessPrompt.Search(s).Count > 0)
                {
                    initState = "success";
                    return true;
                }

                return false;
            }, timeout: TimeSpan.FromMinutes(2), description: "language prompt, template version prompt, agent init prompt, or init success prompt");

            if (initState == "language")
            {
                await auto.EnterAsync();

                await auto.WaitUntilAsync(s =>
                {
                    if (waitingForTemplateVersionPrompt.Search(s).Count > 0)
                    {
                        initState = "template-version";
                        return true;
                    }

                    if (waitingForAgentInitPrompt.Search(s).Count > 0)
                    {
                        initState = "agent-init";
                        return true;
                    }

                    if (waitingForSuccessPrompt.Search(s).Count > 0)
                    {
                        initState = "success";
                        return true;
                    }

                    return false;
                }, timeout: TimeSpan.FromMinutes(2), description: "template version prompt, agent init prompt, or init success prompt");
            }

            if (initState == "template-version")
            {
                await auto.EnterAsync();

                await auto.WaitUntilAsync(s =>
                {
                    if (waitingForAgentInitPrompt.Search(s).Count > 0)
                    {
                        initState = "agent-init";
                        return true;
                    }

                    if (waitingForSuccessPrompt.Search(s).Count > 0)
                    {
                        initState = "success";
                        return true;
                    }

                    return false;
                }, timeout: TimeSpan.FromMinutes(2), description: "agent init prompt or init success prompt");
            }

            if (initState == "agent-init")
            {
                await auto.DeclineAgentInitPromptAsync(counter);
            }
            else
            {
                await auto.WaitForSuccessPromptAsync(counter);
            }

            // Step 6: Add ACA package using GA CLI (uses GA NuGet packages)
            output.WriteLine("Step 6: Adding Azure Container Apps package (GA)...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 7: Modify apphost.cs with a short env name (fits within 24 chars with default naming)
            // and a container with volume to trigger storage account creation
            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);

            var buildRunPattern = "builder.Build().Run();";
            // Use short name "env" (3 chars) so default naming works: "envstoragevolume" (16) + uniqueString fits in 24
            var replacement = """
builder.AddAzureContainerAppEnvironment("env");

builder.AddContainer("worker", "mcr.microsoft.com/dotnet/samples", "aspnetapp")
       .WithVolume("data", "/app/data");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine("Modified apphost.cs with short env name + volume (GA-compatible)");

            // Step 8: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy with GA CLI
            output.WriteLine("Step 9: First deployment with GA CLI...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 10: Record the storage account count after first deploy
            output.WriteLine("Step 10: Recording storage account count after GA deploy...");
            await auto.TypeAsync(
                $"GA_STORAGE_COUNT=$(az storage account list -g \"{resourceGroupName}\" --query \"length([])\" -o tsv) && " +
                "echo \"GA deploy storage count: $GA_STORAGE_COUNT\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // ============================================================
            // Phase 2: Upgrade to dev CLI and redeploy
            // ============================================================

            // Step 11: Install the dev (PR) CLI, overwriting the GA installation
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                output.WriteLine("Step 11: Restoring dev CLI from backup...");
                // Restore the dev CLI and hive that we backed up before GA install
                await auto.TypeAsync("cp -f /tmp/aspire-dev-backup ~/.aspire/bin/aspire && cp -rf /tmp/aspire-hives-backup/* ~/.aspire/hives/ 2>/dev/null; echo 'dev CLI restored'");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter);

                // Ensure the dev CLI uses the local channel (GA install may have changed it)
                await auto.TypeAsync("aspire config set channel local --global 2>/dev/null; echo 'channel set'");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter);

                // Re-source environment to pick up the dev CLI
                await auto.SourceAspireCliEnvironmentAsync(counter);

                // Run aspire update to upgrade the #:package directives in apphost.cs
                // from the GA version to the dev build version. This ensures the actual
                // deployment logic (naming, bicep generation) comes from the dev packages.
                // aspire update shows 3 interactive prompts — handle each explicitly.
                output.WriteLine("Step 11b: Updating project packages to dev version...");
                await auto.TypeAsync("aspire update --channel local");
                await auto.EnterAsync();
                await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromMinutes(2));
                await auto.EnterAsync();
                await auto.WaitUntilTextAsync("NuGet.config file?", timeout: TimeSpan.FromMinutes(2));
                await auto.EnterAsync();
                await auto.WaitUntilTextAsync("Apply these changes", timeout: TimeSpan.FromMinutes(2));
                await auto.EnterAsync();
                await auto.WaitUntilTextAsync("Update successful", timeout: TimeSpan.FromMinutes(2));
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));
            }
            else
            {
                // For local testing, use the PR install script if GITHUB_PR_NUMBER is set
                var prNumber = DeploymentE2ETestHelpers.GetPrNumber();
                if (prNumber > 0)
                {
                    output.WriteLine($"Step 11: Upgrading to dev CLI from PR #{prNumber}...");
                    await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
                    await auto.SourceAspireCliEnvironmentAsync(counter);

                    // Update project packages to the PR version
                    output.WriteLine("Step 11b: Updating project packages to dev version...");
                    await auto.TypeAsync($"aspire update --channel pr-{prNumber}");
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromMinutes(2));
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("NuGet.config file?", timeout: TimeSpan.FromMinutes(2));
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("Apply these changes", timeout: TimeSpan.FromMinutes(2));
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("Update successful", timeout: TimeSpan.FromMinutes(2));
                    await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));
                }
                else
                {
                    output.WriteLine("Step 11: No PR number available, using current CLI as 'dev'...");
                    // Still run aspire update to pick up whatever local packages are available
                    await auto.TypeAsync("aspire update");
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromMinutes(2));
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("NuGet.config file?", timeout: TimeSpan.FromMinutes(2));
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("Apply these changes", timeout: TimeSpan.FromMinutes(2));
                    await auto.EnterAsync();
                    await auto.WaitUntilTextAsync("Update successful", timeout: TimeSpan.FromMinutes(2));
                    await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));
                }
            }

            // Step 12: Log the dev CLI version and verify packages were updated
            output.WriteLine("Step 12: Logging dev CLI version and verifying package update...");
            await auto.TypeAsync("aspire --version");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Verify the #:package directives in apphost.cs were updated from GA version
            await auto.TypeAsync("grep '#:package\\|#:sdk' apphost.cs");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 13: Redeploy with dev packages — same apphost, NO compact naming
            // The dev packages contain our changes but default naming is unchanged,
            // so this should reuse the same resources created by the GA deploy.
            output.WriteLine("Step 13: Redeploying with dev packages (no compact naming)...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 14: Verify no duplicate storage accounts
            output.WriteLine("Step 14: Verifying no duplicate storage accounts...");
            await auto.TypeAsync(
                $"DEV_STORAGE_COUNT=$(az storage account list -g \"{resourceGroupName}\" --query \"length([])\" -o tsv) && " +
                "echo \"Dev deploy storage count: $DEV_STORAGE_COUNT\" && " +
                "echo \"GA deploy storage count: $GA_STORAGE_COUNT\" && " +
                "if [ \"$DEV_STORAGE_COUNT\" = \"$GA_STORAGE_COUNT\" ]; then " +
                "echo '✅ No duplicate storage accounts — default naming unchanged on upgrade'; " +
                "else " +
                "echo \"❌ Storage count changed from $GA_STORAGE_COUNT to $DEV_STORAGE_COUNT — NAMING REGRESSION\"; exit 1; " +
                "fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 15: Clean up Azure resources using aspire destroy
            output.WriteLine("Step 15: Destroying Azure deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 16: Exit
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"✅ Upgrade test completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(UpgradeFromGaToDevDoesNotDuplicateStorageAccounts),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"❌ Test failed: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(UpgradeFromGaToDevDoesNotDuplicateStorageAccounts),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Cleaning up resource group: {resourceGroupName}");
            await CleanupResourceGroupAsync(resourceGroupName);
        }
    }

    private async Task CleanupResourceGroupAsync(string resourceGroupName)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
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
            output.WriteLine(process.ExitCode == 0
                ? $"Resource group deletion initiated: {resourceGroupName}"
                : $"Resource group deletion may have failed (exit code {process.ExitCode})");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to cleanup resource group: {ex.Message}");
        }
    }
}
