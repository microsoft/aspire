// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// L1 infrastructure verification test for deploying a TypeScript AppHost with Azure SQL Server,
/// VNet, and Private Endpoint to Azure. Validates that <c>withAdminDeploymentScriptSubnet</c> works
/// correctly in polyglot (TypeScript) app hosts.
/// </summary>
public sealed class TypeScriptVnetSqlServerInfraDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task DeployTypeScriptVnetSqlServerInfrastructure()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployTypeScriptVnetSqlServerInfrastructureCore(cancellationToken);
    }

    private async Task DeployTypeScriptVnetSqlServerInfrastructureCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("ts-vnet-sql-l1");

        output.WriteLine($"Test: {nameof(DeployTypeScriptVnetSqlServerInfrastructure)}");
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
            // TypeScript apphosts need the full bundle because
            // the prebuilt AppHost server is required for aspire add to regenerate SDK code.
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

            // Step 3: Create TypeScript AppHost using aspire init
            output.WriteLine("Step 3: Creating TypeScript AppHost with aspire init...");

            var waitingForTemplateVersionPrompt = new CellPatternSearcher()
                .Find("NuGet.config");
            var waitingForAgentInitPrompt = new CellPatternSearcher()
                .Find("configure AI agent environments");
            var waitingForSuccessPrompt = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");

            await auto.TypeAsync("aspire init --language typescript");
            await auto.EnterAsync();

            var sawTemplateVersionPrompt = false;
            var sawAgentInitPrompt = false;
            await auto.WaitUntilAsync(s =>
            {
                if (waitingForTemplateVersionPrompt.Search(s).Count > 0)
                {
                    sawTemplateVersionPrompt = true;
                    return true;
                }

                if (waitingForAgentInitPrompt.Search(s).Count > 0)
                {
                    sawAgentInitPrompt = true;
                    return true;
                }

                return waitingForSuccessPrompt.Search(s).Count > 0;
            }, timeout: TimeSpan.FromMinutes(2), description: "template version prompt, agent init prompt, or init success prompt");

            if (sawTemplateVersionPrompt)
            {
                await auto.EnterAsync();

                await auto.WaitUntilAsync(s =>
                {
                    if (waitingForAgentInitPrompt.Search(s).Count > 0)
                    {
                        sawAgentInitPrompt = true;
                        return true;
                    }

                    return waitingForSuccessPrompt.Search(s).Count > 0;
                }, timeout: TimeSpan.FromMinutes(2), description: "agent init prompt or init success prompt");
            }

            if (sawAgentInitPrompt)
            {
                await auto.DeclineAgentInitPromptAsync(counter);
            }
            else
            {
                await auto.WaitForSuccessPromptAsync(counter);
            }

            // Step 4a: Add Aspire.Hosting.Azure.AppContainers
            output.WriteLine("Step 4a: Adding Azure Container Apps hosting package...");
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

            // Step 4b: Add Aspire.Hosting.Azure.Network
            output.WriteLine("Step 4b: Adding Azure Network hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Network");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitForAspireAddCompletionAsync(counter);
            }
            else
            {
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));
            }

            // Step 4c: Add Aspire.Hosting.Azure.Sql
            output.WriteLine("Step 4c: Adding Azure SQL hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Sql");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitForAspireAddCompletionAsync(counter);
            }
            else
            {
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));
            }

            // Step 5: Modify apphost.ts to add VNet + PE + SQL infrastructure
            {
                var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
                output.WriteLine($"Looking for apphost.ts at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);

                content = content.Replace(
                    "await builder.build().run();",
                    """
// Add Azure Container App Environment for deployment
const env = await builder.addAzureContainerAppEnvironment("env");

// VNet with delegated subnet for ACA and PE subnet
const vnet = await builder.addAzureVirtualNetwork("vnet");
const acaSubnet = await vnet.addSubnet("aca-subnet", "10.0.0.0/23");
const peSubnet = await vnet.addSubnet("pe-subnet", "10.0.2.0/24");
const aciSubnet = await vnet.addSubnet("aci-subnet", "10.0.3.0/29");

await env.withDelegatedSubnet(acaSubnet);

// SQL Server with Private Endpoint and explicit deployment script subnet
const sql = await builder.addAzureSqlServer("sql");
const db = await sql.addDatabase("db");
await peSubnet.addPrivateEndpoint(sql);
await sql.withAdminDeploymentScriptSubnet(aciSubnet);

await builder.build().run();
""");

                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified apphost.ts with VNet + SQL Server PE + deployment script subnet infrastructure");
                output.WriteLine($"New content:\n{content}");
            }

            // Step 6: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 7: Deploy to Azure
            output.WriteLine("Step 7: Starting Azure deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(25));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 8: Verify VNet infrastructure
            output.WriteLine("Step 8: Verifying VNet infrastructure...");
            await auto.TypeAsync($"az network vnet list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv | head -5 && " +
                      $"echo \"---PE---\" && az network private-endpoint list -g \"{resourceGroupName}\" --query \"[].{{name:name,state:provisioningState}}\" -o table && " +
                      $"echo \"---DNS---\" && az network private-dns zone list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 9: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployTypeScriptVnetSqlServerInfrastructure),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);

            output.WriteLine("✅ Test passed!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployTypeScriptVnetSqlServerInfrastructure),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
    }

    private void TriggerCleanupResourceGroup(string resourceGroupName)
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
