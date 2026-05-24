// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying TypeScript AppHosts that publish Azure Container App jobs.
/// </summary>
public sealed class TypeScriptAzureContainerAppJobDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(35);

    [Fact]
    public async Task DeployTypeScriptContainerAppJobsToAzureContainerApps()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployTypeScriptContainerAppJobsToAzureContainerAppsCore(cancellationToken);
    }

    private async Task DeployTypeScriptContainerAppJobsToAzureContainerAppsCore(CancellationToken cancellationToken)
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

            Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
        }

        using var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("ts-aca-jobs");

        output.WriteLine($"Test: {nameof(DeployTypeScriptContainerAppJobsToAzureContainerApps)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);

            await auto.RunCommandFailFastAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));
            await AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppContainers");

            WriteContainerAppJobsAppHost(workspace);

            await auto.RunCommandFailFastAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}", counter);

            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(25));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.RunCommandFailFastAsync(BuildJobVerificationCommand(resourceGroupName), counter, TimeSpan.FromMinutes(5));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployTypeScriptContainerAppJobsToAzureContainerApps),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployTypeScriptContainerAppJobsToAzureContainerApps),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName, output);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, errorMessage: "Cleanup triggered (fire-and-forget)");
        }
    }

    private static async Task AddPackageAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string packageName)
    {
        await auto.TypeAsync($"aspire add {packageName}");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
    }

    private static void WriteContainerAppJobsAppHost(TemporaryWorkspace workspace)
    {
        // `aspire init --language typescript` creates apphost.mts (since PR #16984), not
        // apphost.ts. Overwrite that file with the test's custom AppHost; otherwise
        // `aspire deploy` runs against the empty default apphost.mts and no Azure
        // resources get provisioned.
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"), """
            import { createBuilder } from './.aspire/modules/aspire.js';

            const builder = await createBuilder();

            const env = await builder.addAzureContainerAppEnvironment('env');

            await (await builder.addContainer('manual-job', 'mcr.microsoft.com/azurelinux/base/core:3.0'))
                .withComputeEnvironment(env)
                .publishAsAzureContainerAppJob();

            await (await builder.addContainer('scheduled-job', 'mcr.microsoft.com/azurelinux/base/core:3.0'))
                .withComputeEnvironment(env)
                .publishAsScheduledAzureContainerAppJob('0 0 * * *');

            await builder.build().run();
            """);
    }

    private static string BuildJobVerificationCommand(string resourceGroupName)
    {
        return
            $"RG_NAME=\"{resourceGroupName}\" && " +
            "echo \"Resource group: $RG_NAME\" && " +
            "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"Resource group not found\"; exit 1; fi && " +
            "az containerapp job list -g \"$RG_NAME\" --query \"[].{name:name,trigger:properties.configuration.triggerType,cron:properties.configuration.scheduleTriggerConfig.cronExpression}\" -o table && " +
            "manual_trigger=$(az containerapp job list -g \"$RG_NAME\" --query \"[?contains(name, 'manual-job')].properties.configuration.triggerType | [0]\" -o tsv) && " +
            "scheduled_trigger=$(az containerapp job list -g \"$RG_NAME\" --query \"[?contains(name, 'scheduled-job')].properties.configuration.triggerType | [0]\" -o tsv) && " +
            "scheduled_cron=$(az containerapp job list -g \"$RG_NAME\" --query \"[?contains(name, 'scheduled-job')].properties.configuration.scheduleTriggerConfig.cronExpression | [0]\" -o tsv) && " +
            "if [ \"$manual_trigger\" != \"Manual\" ]; then echo \"manual-job trigger was '$manual_trigger', expected Manual\"; exit 1; fi && " +
            "if [ \"$scheduled_trigger\" != \"Schedule\" ]; then echo \"scheduled-job trigger was '$scheduled_trigger', expected Schedule\"; exit 1; fi && " +
            "if [ \"$scheduled_cron\" != \"0 0 * * *\" ]; then echo \"scheduled-job cron was '$scheduled_cron', expected 0 0 * * *\"; exit 1; fi";
    }

    private static void TriggerCleanupResourceGroup(string resourceGroupName, ITestOutputHelper output)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
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
