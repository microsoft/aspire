// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying a TypeScript Express/React Aspire application to AKS with a custom node pool.
/// Verifies that <c>addNodePool</c> from a TypeScript AppHost creates additional node pools in the AKS cluster.
/// </summary>
public sealed class TypeScriptAksNodePoolDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployTypeScriptExpressToAksWithNodePool()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployTypeScriptExpressToAksWithNodePoolCore(cancellationToken);
    }

    private async Task DeployTypeScriptExpressToAksWithNodePoolCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("ts-aks-np");
        var projectName = "TsAksNodePool";

        output.WriteLine($"Test: {nameof(DeployTypeScriptExpressToAksWithNodePool)}");
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
            // TypeScript apphosts need the full bundle (not just the CLI binary) because
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

            // Step 3: Create TypeScript Express/React project using aspire new
            output.WriteLine("Step 3: Creating TypeScript Express/React project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.ExpressReact);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Azure.Kubernetes package
            output.WriteLine("Step 5: Adding Azure Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Kubernetes");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitForAspireAddCompletionAsync(counter);
            }
            else
            {
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));
            }

            // Step 6: Modify apphost.ts to add AKS environment with custom node pool
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var appHostFilePath = Path.Combine(projectDir, "apphost.ts");

                output.WriteLine($"Looking for apphost.ts at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);
                var originalContent = content;

                // Add Azure Kubernetes Environment with a custom node pool before build().run()
                content = content.Replace(
                    "await builder.build().run();",
                    """
// Add Azure Kubernetes Environment with a custom node pool
const aks = await builder.addAzureKubernetesEnvironment("aks");
await aks.addNodePool("compute", "Standard_D4s_v5", 1, 3);

await builder.build().run();
""");

                if (content == originalContent)
                {
                    throw new InvalidOperationException("apphost.ts was not modified. Template may have changed.");
                }

                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified apphost.ts at: {appHostFilePath}");
            }

            // Step 7: Set environment for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Deploy to AKS using aspire deploy
            output.WriteLine("Step 8: Starting AKS deployment via aspire deploy...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 9: Get AKS credentials for kubectl verification
            output.WriteLine("Step 9: Getting AKS credentials...");
            await auto.TypeAsync($"AKS_NAME=$(az aks list --resource-group {resourceGroupName} --query '[0].name' -o tsv) && " +
                  $"echo \"AKS cluster: $AKS_NAME\" && " +
                  $"az aks get-credentials --resource-group {resourceGroupName} --name $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 10: Wait for pods to be ready
            output.WriteLine("Step 10: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all -n default --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Step 11: Verify pods are running
            output.WriteLine("Step 11: Verifying pods are running...");
            await auto.TypeAsync("kubectl get pods -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 12: Verify two node pools exist (system + compute)
            output.WriteLine("Step 12: Verifying node pools...");
            await auto.TypeAsync($"az aks nodepool list --resource-group {resourceGroupName} --cluster-name $AKS_NAME --query '[].{{name:name, mode:mode}}' -o table");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("compute", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 13: Verify service endpoints via port-forward
            output.WriteLine("Step 13: Verifying service endpoints...");
            await auto.TypeAsync("kubectl port-forward svc/api-service 18080:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18080/ -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 14: Clean up port-forwards
            output.WriteLine("Step 14: Cleaning up port-forwards...");
            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 15: Destroy Azure deployment
            output.WriteLine("Step 15: Destroying Azure deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 16: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"TypeScript AKS node pool deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployTypeScriptExpressToAksWithNodePool),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["project"] = projectName
                },
                duration);

            output.WriteLine("✅ Test passed - TypeScript Express app deployed to AKS with custom node pool!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployTypeScriptExpressToAksWithNodePool),
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
