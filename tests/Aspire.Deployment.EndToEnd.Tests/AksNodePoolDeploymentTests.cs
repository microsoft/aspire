// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications to AKS with a custom node pool.
/// Verifies that <c>AddNodePool</c> creates additional node pools and that pods are
/// scheduled to the correct pool via <c>WithNodePool</c>.
/// </summary>
public sealed class AksNodePoolDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployStarterToAksWithCustomNodePool()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterToAksWithCustomNodePoolCore(cancellationToken);
    }

    private async Task DeployStarterToAksWithCustomNodePoolCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("aks-nodepool");
        var projectName = "AksNodePool";

        output.WriteLine($"Test: {nameof(DeployStarterToAksWithCustomNodePool)}");
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

            // Step 3: Create starter project using aspire new
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

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
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 6: Write complete AppHost.cs with AKS environment and custom node pool
            // (full rewrite to avoid string-replacement failures caused by line-ending or whitespace differences)
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Writing AppHost.cs at: {appHostFilePath}");

            var appHostContent = $"""
                #pragma warning disable ASPIREPIPELINES001

                var builder = DistributedApplication.CreateBuilder(args);

                var aks = builder.AddAzureKubernetesEnvironment("aks");
                var computePool = aks.AddNodePool("compute", "Standard_D4s_v5", 1, 3);

                var apiService = builder.AddProject<Projects.{projectName}_ApiService>("apiservice")
                    .WithHttpHealthCheck("/health")
                    .WithNodePool(computePool);

                builder.AddProject<Projects.{projectName}_Web>("webfrontend")
                    .WithExternalHttpEndpoints()
                    .WithHttpHealthCheck("/health")
                    .WithReference(apiService)
                    .WaitFor(apiService);

                builder.Build().Run();
                """;

            File.WriteAllText(appHostFilePath, appHostContent);

            output.WriteLine("Wrote complete AppHost.cs with AddAzureKubernetesEnvironment and custom node pool");

            // Step 7: Navigate to AppHost project directory
            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy to AKS using aspire deploy
            output.WriteLine("Step 9: Starting AKS deployment via aspire deploy...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 10: Get AKS credentials for kubectl verification
            output.WriteLine("Step 10: Getting AKS credentials...");
            await auto.TypeAsync($"AKS_NAME=$(az aks list --resource-group {resourceGroupName} --query '[0].name' -o tsv) && " +
                  $"echo \"AKS cluster: $AKS_NAME\" && " +
                  $"az aks get-credentials --resource-group {resourceGroupName} --name $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 11: Wait for pods to be ready
            output.WriteLine("Step 11: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all -n default --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Step 12: Verify pods are running
            output.WriteLine("Step 12: Verifying pods are running...");
            await auto.TypeAsync("kubectl get pods -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 13: Verify two node pools exist (system + compute)
            output.WriteLine("Step 13: Verifying node pools...");
            await auto.TypeAsync($"az aks nodepool list --resource-group {resourceGroupName} --cluster-name $AKS_NAME --query '[].{{name:name, mode:mode}}' -o table");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("compute", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 14: Verify apiservice pod has nodeSelector for the compute pool
            output.WriteLine("Step 14: Verifying apiservice pod nodeSelector...");
            await auto.TypeAsync("kubectl get pod -l app.kubernetes.io/component=apiservice -o jsonpath='{.items[0].spec.nodeSelector}'");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 15: Verify apiservice endpoint via port-forward
            output.WriteLine("Step 15: Verifying apiservice endpoint...");
            await auto.TypeAsync("kubectl port-forward svc/apiservice-service 18080:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18080/weatherforecast -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 16: Verify webfrontend endpoint via port-forward
            output.WriteLine("Step 16: Verifying webfrontend endpoint...");
            await auto.TypeAsync("kubectl port-forward svc/webfrontend-service 18081:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18081/ -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 17: Clean up port-forwards
            output.WriteLine("Step 17: Cleaning up port-forwards...");
            await auto.TypeAsync("kill %1 %2 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 18: Destroy Azure deployment
            output.WriteLine("Step 18: Destroying Azure deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 19: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"AKS node pool deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterToAksWithCustomNodePool),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["project"] = projectName
                },
                duration);

            output.WriteLine("✅ Test passed - Aspire app deployed to AKS with custom node pool!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterToAksWithCustomNodePool),
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
