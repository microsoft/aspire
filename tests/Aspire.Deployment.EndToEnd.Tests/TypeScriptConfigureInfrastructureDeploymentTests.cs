// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// Verifies that a TypeScript AppHost can customize generated Azure infrastructure before deployment.
/// </summary>
public sealed class TypeScriptConfigureInfrastructureDeploymentTests(ITestOutputHelper output)
{
    private const string AzureProvisioningPackageVersion = "0.1.0";
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task DeployTypeScriptCustomizedAzureStorage()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployTypeScriptCustomizedAzureStorageCore(linkedCts.Token);
    }

    private async Task DeployTypeScriptCustomizedAzureStorageCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("ts-infra-storage");

        output.WriteLine($"Test: {nameof(DeployTypeScriptCustomizedAzureStorage)}");
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
            await auto.RunCommandAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));

            await AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppContainers");
            await AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.Storage");
            await auto.RunCommandAsync(
                $"npm install @azure/provisioning-serialization@{AzureProvisioningPackageVersion} @azure/provisioning-storage@{AzureProvisioningPackageVersion}",
                counter,
                TimeSpan.FromMinutes(3));

            WriteAppHost(workspace);

            await auto.RunCommandAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}",
                counter);
            // This test deliberately enables local authentication, so opt the isolated test resource
            // group out of subscriptions that enforce the Safe Secrets storage policy.
            await auto.RunCommandAsync(
                $"az group create --name \"{resourceGroupName}\" --location westus3 --tags 'Az.Sec.DisableLocalAuth.Storage::Skip=true' --output none",
                counter,
                TimeSpan.FromMinutes(2));

            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(20));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.RunCommandAsync(BuildStorageVerificationCommand(resourceGroupName), counter, TimeSpan.FromMinutes(2));

            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployTypeScriptCustomizedAzureStorage),
                resourceGroupName,
                new Dictionary<string, string>(),
                DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployTypeScriptCustomizedAzureStorage),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            TriggerCleanupResourceGroup(resourceGroupName);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
    }

    private static async Task AddPackageAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string packageName)
    {
        await auto.TypeAsync($"aspire add {packageName}");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
    }

    private static void WriteAppHost(TemporaryWorkspace workspace)
    {
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"), """
            import { deserialize, serialize } from '@azure/provisioning-serialization';
            import { StorageAccount } from '@azure/provisioning-storage';
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();
            await builder.addAzureContainerAppEnvironment('env');
            const storage = await builder.addAzureStorage('storage');

            await storage.configureInfrastructure(async ({ infrastructureJson }) => {
                if (!infrastructureJson) {
                    throw new Error('Aspire did not provide the generated infrastructure.');
                }

                const [infrastructure] = deserialize(infrastructureJson);
                const storageAccount = infrastructure?.getResources(StorageAccount)[0];
                if (!storageAccount) {
                    throw new Error('The generated infrastructure did not contain a storage account.');
                }

                storageAccount.sku = { name: 'Standard_LRS' };
                storageAccount.properties.allowSharedKeyAccess = true;

                return { infrastructureJson: JSON.stringify(serialize([infrastructure])) };
            });

            await builder.build().run();
            """);
    }

    private static string BuildStorageVerificationCommand(string resourceGroupName)
    {
        return
            $"storage_name=\"$(az storage account list -g \\\"{resourceGroupName}\\\" --query \\\"[0].name\\\" -o tsv)\" && " +
            "test -n \"$storage_name\" && " +
            $"read -r storage_sku shared_key_access <<< \"$(az storage account show -g \\\"{resourceGroupName}\\\" -n \\\"$storage_name\\\" --query \\\"{{sku:sku.name,shared:allowSharedKeyAccess}}\\\" -o tsv)\" && " +
            "echo \"Storage SKU: $storage_sku; shared key access: $shared_key_access\" && " +
            "test \"$storage_sku\" = \"Standard_LRS\" && { test \"$shared_key_access\" = \"true\" || test \"$shared_key_access\" = \"True\"; }";
    }

    private void TriggerCleanupResourceGroup(string resourceGroupName)
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
