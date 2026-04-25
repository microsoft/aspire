// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying an Aspire starter application to AKS with multiple node pools
/// and verifying that pods are scheduled on the correct pool using <c>WithNodePool</c>.
/// AKS cluster, ACR, and resource group are all provisioned automatically by the pipeline.
/// </summary>
public sealed class AksMultipleNodePoolsDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for AKS provisioning (~10-15 min) plus deployment.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployAksWithMultipleNodePools()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployAksWithMultipleNodePoolsCore(cancellationToken);
    }

    private async Task DeployAksWithMultipleNodePoolsCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("akspools");
        var projectName = "AksPools";

        output.WriteLine($"Test: {nameof(DeployAksWithMultipleNodePools)}");
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
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            // Step 3: Create starter project without Redis
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

            // aspire add may show a version selection prompt
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Modify AppHost.cs to add Azure Kubernetes Environment with multiple node pools
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Add using directive for WithNodePool extension method
            content = "using Aspire.Hosting.Kubernetes.Extensions;\n" + content;

            // Insert the Azure Kubernetes Environment with multiple node pools AFTER CreateBuilder
            // so workerPool variable is available when apiservice references it.
            content = content.Replace(
                "var builder = DistributedApplication.CreateBuilder(args);",
                """
var builder = DistributedApplication.CreateBuilder(args);

// AKS environment with multiple node pools
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithSystemNodePool("Standard_D2as_v5", minCount: 1, maxCount: 2);

var workerPool = aks.AddNodePool("workers", "Standard_D2as_v5", 1, 3);
""");

            // Pin apiservice to the worker node pool
            content = content.Replace(
                "builder.AddProject<Projects." + projectName + "_ApiService>(\"apiservice\")",
                "builder.AddProject<Projects." + projectName + "_ApiService>(\"apiservice\")\n    .WithNodePool(workerPool)");

            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

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
            output.WriteLine("Step 9: Starting AKS deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            // Wait for pipeline to complete successfully
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 10: Get AKS credentials for the provisioned cluster
            output.WriteLine("Step 10: Getting AKS credentials...");
            await auto.TypeAsync($"AKS_NAME=$(az aks list -g {resourceGroupName} --query '[0].name' -o tsv) && " +
                  $"az aks get-credentials -g {resourceGroupName} -n $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 11: Wait for all pods to be ready
            output.WriteLine("Step 11: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all --all-namespaces --timeout=300s 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Step 12: Discover deployment namespace and wait for app pods
            output.WriteLine("Step 12: Waiting for application pods...");
            await auto.TypeAsync("NS=$(kubectl get svc --all-namespaces -o jsonpath='{range .items[?(@.metadata.name==\"apiservice-service\")]}{.metadata.namespace}{end}') && " +
                      "echo \"Namespace: $NS\" && " +
                      "kubectl wait --for=condition=ready pod -l app.kubernetes.io/component=apiservice -n $NS --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Step 13: Verify node pools exist (system + workers)
            output.WriteLine("Step 13: Verifying node pools...");
            await auto.TypeAsync($"az aks nodepool list -g {resourceGroupName} --cluster-name $AKS_NAME --query \"[].{{name:name,vmSize:vmSize,mode:mode,count:count}}\" -o table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 14: Show pods with node info
            output.WriteLine("Step 14: Showing pods with node info...");
            await auto.TypeAsync("kubectl get pods --all-namespaces -o wide");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 15: Verify API pod is on a worker pool node
            output.WriteLine("Step 15: Verifying API pod scheduling on workers pool...");
            await auto.TypeAsync("[ -n \"$NS\" ] || { echo 'ERROR: namespace not set'; exit 1; }; " +
                      "POD_NODE=$(kubectl get pods -n \"$NS\" -l app.kubernetes.io/component=apiservice -o jsonpath='{.items[0].spec.nodeName}') && " +
                      "[ -n \"$POD_NODE\" ] || { echo 'ERROR: apiservice pod node not found'; exit 1; }; " +
                      "NODE_POOL=$(kubectl get node \"$POD_NODE\" -o jsonpath='{.metadata.labels.agentpool}') && " +
                      "[ -n \"$NODE_POOL\" ] || { echo 'ERROR: node pool label not found'; exit 1; }; " +
                      "echo \"API pod node: $POD_NODE, pool: $NODE_POOL\" && " +
                      "if [ \"$NODE_POOL\" = \"workers\" ]; then echo 'PASS: API pod on workers pool'; else echo 'ERROR: API pod not on expected pool'; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 16: Verify apiservice is reachable via port-forward
            output.WriteLine("Step 16: Verifying apiservice endpoint via port-forward...");
            await auto.TypeAsync("kubectl port-forward svc/apiservice-service 18082:8080 -n $NS > /dev/null 2>&1 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Verify /weatherforecast endpoint (default starter API)
            await auto.TypeAsync("OK=0; for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18082/weatherforecast -o /dev/null -w '%{http_code}' && echo ' OK' && OK=1 && break; done; [ \"$OK\" = \"1\" ] || { echo 'FAIL: apiservice unreachable after 10 retries'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Clean up port-forward
            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 17: Clean up using aspire destroy
            output.WriteLine("Step 17: Destroying deployment...");            await auto.AspireDestroyAsync(counter);

            // Step 18: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployAksWithMultipleNodePools),
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
                nameof(DeployAksWithMultipleNodePools),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Clean up the resource group created by the pipeline
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName);
        }
    }

    /// <summary>
    /// Triggers cleanup of a specific resource group.
    /// This is fire-and-forget — the hourly cleanup workflow handles any missed resources.
    /// </summary>
    private void TriggerCleanupResourceGroup(string resourceGroupName)
    {
        using var process = new System.Diagnostics.Process
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
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to trigger cleanup: {ex.Message}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, ex.Message);
        }
    }
}
