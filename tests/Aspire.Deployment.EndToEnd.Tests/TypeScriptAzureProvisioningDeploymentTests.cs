// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class TypeScriptAzureProvisioningDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public Task DeployAppConfigurationWithProvisioningOverrides()
    {
        return DeployAsync(
            nameof(DeployAppConfigurationWithProvisioningOverrides),
            "ts-proxy-appconfig",
            ["Aspire.Hosting.Azure.AppConfiguration", "Aspire.Hosting.Azure.Provisioning.AppConfiguration"],
            """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();
            const appConfig = await builder.addAzureAppConfiguration("appconfig");
            await appConfig.configureInfrastructure(async infrastructure => {
                const store = await infrastructure.getAppConfigurationStore();
                await store.softDeleteRetentionInDays.set(1);
                await store.disableLocalAuth.set(true);
                const tags = await store.tags.get();
                await tags.set("provisioning-proxy", "typescript-appconfig");
            });

            await builder.build().run();
            """,
            """
            set -euo pipefail
            az appconfig list --subscription "$AZURE__SUBSCRIPTIONID" -g "$AZURE__RESOURCEGROUP" -o json > stores.json
            store_name=$(jq -er 'if length == 1 then .[0].name else error("Expected exactly one App Configuration store") end' stores.json)
            az appconfig show --subscription "$AZURE__SUBSCRIPTIONID" -g "$AZURE__RESOURCEGROUP" -n "$store_name" -o json > store.json
            jq -e '.provisioningState == "Succeeded" and .softDeleteRetentionInDays == 1 and .disableLocalAuth == true and .tags["provisioning-proxy"] == "typescript-appconfig"' store.json
            """);
    }

    [Fact]
    public Task DeployNetworkAndPrivateDnsWithProvisioningOverrides()
    {
        return DeployAsync(
            nameof(DeployNetworkAndPrivateDnsWithProvisioningOverrides),
            "ts-proxy-network",
            [
                "Aspire.Hosting.Azure.Network",
                "Aspire.Hosting.Azure.Provisioning.Network",
                "Aspire.Hosting.Azure.Provisioning.PrivateDns"
            ],
            """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();
            const vnet = await builder.addAzureVirtualNetwork("vnet", { addressPrefix: "10.42.0.0/16" });
            await vnet.addSubnet("apps", "10.42.1.0/24");
            await vnet.configureInfrastructure(async infrastructure => {
                const network = await infrastructure.getVirtualNetwork();
                await network.flowTimeoutInMinutes.set(10);
                const tags = await network.tags.get();
                await tags.set("provisioning-proxy", "typescript-network");
            });

            const nsg = await builder.addNetworkSecurityGroup("nsg");
            await nsg.configureInfrastructure(async infrastructure => {
                const group = await infrastructure.getNetworkSecurityGroup();
                const tags = await group.tags.get();
                await tags.set("provisioning-proxy", "typescript-nsg");
            });

            const dns = await builder.addAzureInfrastructure("privateDns", async infrastructure => {
                const zone = await infrastructure.addPrivateDnsZone("privateDns");
                await zone.location.set(infrastructure.bicep().location("global"));
            });
            await dns.configureInfrastructure(async infrastructure => {
                const zone = await infrastructure.getPrivateDnsZone();
                await zone.name.set("provisioning.internal");
                const tags = await zone.tags.get();
                await tags.set("provisioning-proxy", "typescript-dns");
            });

            await builder.build().run();
            """,
            """
            set -euo pipefail
            az network vnet list --subscription "$AZURE__SUBSCRIPTIONID" -g "$AZURE__RESOURCEGROUP" -o json > networks.json
            jq -e 'length == 1 and (.[0] | .provisioningState == "Succeeded" and .flowTimeoutInMinutes == 10 and .tags["provisioning-proxy"] == "typescript-network" and .addressSpace.addressPrefixes == ["10.42.0.0/16"] and (.subnets | length == 1 and .[0].addressPrefix == "10.42.1.0/24"))' networks.json
            az network nsg list --subscription "$AZURE__SUBSCRIPTIONID" -g "$AZURE__RESOURCEGROUP" -o json > security-groups.json
            jq -e 'length == 1 and (.[0] | .provisioningState == "Succeeded" and .tags["provisioning-proxy"] == "typescript-nsg")' security-groups.json
            az network private-dns zone show --subscription "$AZURE__SUBSCRIPTIONID" -g "$AZURE__RESOURCEGROUP" -n provisioning.internal -o json > zone.json
            jq -e '.name == "provisioning.internal" and .location == "global" and .tags["provisioning-proxy"] == "typescript-dns"' zone.json
            """);
    }

    private async Task DeployAsync(
        string testName,
        string resourceGroupPrefix,
        string[] packages,
        string appHostSource,
        string verificationScript)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Deployment terminal automation requires Linux.");
        var subscriptionId = DotnetProjectDeploymentHelpers.GetSubscriptionId();
        var strategy = DeploymentE2ETestHelpers.GetCurrentBuildCliInstallStrategy();
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.Preinstalled or CliInstallMode.LocalHive or CliInstallMode.LocalArchive or CliInstallMode.PullRequest,
            "Provisioning proxies require current-build artifacts. Set ASPIRE_E2E_ARCHIVE for a local run.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(35));
        using var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName($"{resourceGroupPrefix}-{Guid.NewGuid():N}");
        var startTime = DateTime.UtcNow;
        var deploymentAttempted = false;

        output.WriteLine($"Test: {testName}");
        output.WriteLine($"Resource group: {resourceGroupName}");

        try
        {
            using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal(testName: testName);
            var pendingRun = terminal.RunAsync(terminalCancellation.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            try
            {
                await auto.PrepareEnvironmentAsync(workspace, counter);
                await auto.InstallAspireCliAsync(strategy, counter, output, includeBundlePath: true, artifactName: "bundle");
                await auto.RunCommandAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));
                foreach (var package in packages)
                {
                    await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, package);
                }

                File.WriteAllText(Path.Combine(workspace.Path, "apphost.mts"), appHostSource);
                await auto.RunCommandAsync(
                    "unset ASPIRE_PLAYGROUND Azure__Location Azure__ResourceGroup Azure__SubscriptionId && " +
                    $"export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP={AspireCliShellCommandHelpers.QuoteBashArg(resourceGroupName)} " +
                    $"AZURE__SUBSCRIPTIONID={AspireCliShellCommandHelpers.QuoteBashArg(subscriptionId)}",
                    counter);
                await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter,
                    """
                    set -euo pipefail
                    exists=$(az group exists --subscription "$AZURE__SUBSCRIPTIONID" -n "$AZURE__RESOURCEGROUP")
                    test "$exists" = false
                    """, TimeSpan.FromSeconds(30));

                deploymentAttempted = true;
                await auto.TypeAsync("aspire deploy --clear-cache");
                await auto.EnterAsync();
                await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(20));
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

                await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, verificationScript, TimeSpan.FromMinutes(2));
                await auto.AspireDestroyAsync(counter, TimeSpan.FromMinutes(5));
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun.WaitAsync(TimeSpan.FromSeconds(30), timeout.Token);
            }
            finally
            {
                // Stop the deployment terminal before cleanup, including when the test times out.
                await terminalCancellation.CancelAsync();
                try
                {
                    await pendingRun.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (OperationCanceledException) when (terminalCancellation.IsCancellationRequested)
                {
                    // Cancellation is the expected shutdown path after a failed deployment or assertion.
                }
            }

            DeploymentReporter.ReportDeploymentSuccess(testName, resourceGroupName, new Dictionary<string, string>(), DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(testName, resourceGroupName, ex.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            if (deploymentAttempted)
            {
                await DotnetProjectDeploymentHelpers.CleanupAsync(resourceGroupName, subscriptionId, output);
            }
        }
    }
}
