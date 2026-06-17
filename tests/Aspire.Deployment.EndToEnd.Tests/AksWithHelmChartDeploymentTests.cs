// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end test for deploying an Aspire starter app to AKS using
/// <c>AddAzureKubernetesEnvironment</c> and installing an external Helm chart
/// (podinfo) via <c>aks.AddHelmChart(...).WithDestroy()</c>.
///
/// Verifies that:
/// 1. The chart is installed alongside the application Helm deploy step.
/// 2. The podinfo pods come up with the configured replica count.
/// 3. <c>WithDestroy()</c> causes the chart to be uninstalled when
///    <c>aspire destroy</c> tears down the environment.
/// </summary>
public sealed class AksWithHelmChartDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for AKS provisioning (~10-15 min) plus deployment.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployAksWithHelmChart()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployAksWithHelmChartCore(cancellationToken);
    }

    private async Task DeployAksWithHelmChartCore(CancellationToken cancellationToken)
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
        var deploymentUrls = new Dictionary<string, string>();
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("akshelm");
        var projectName = "AksHelmChart";

        output.WriteLine($"Test: {nameof(DeployAksWithHelmChart)}");
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

            // Step 2: Install current build of the Aspire CLI
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            // Step 3: Create starter project (no Redis cache).
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
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Modify AppHost.cs to add AKS env + an external Helm chart
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
                var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

                output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);

                content = content.Replace(
                    "var builder = DistributedApplication.CreateBuilder(args);",
                    """
var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIREAZURE003

// AKS environment provisioned by Aspire (Azure Kubernetes Service).
// Pin both the system and workload pools to DASv5 SKUs; the default workload pool
// uses DSv5 SKUs which routinely hit vCPU quota in westus3.
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithSystemNodePool("Standard_D2as_v5");
aks.AddNodePool("workload", "Standard_D2as_v5", 1, 3);

// Install podinfo as an external Helm chart and opt in to destroy-time uninstall.
aks.AddHelmChart("podinfo", "oci://ghcr.io/stefanprodan/charts/podinfo", "6.7.1")
    .WithHelmValue("replicaCount", "2")
    .WithHelmValue("ui.message", "Aspire AKS AddHelmChart E2E test")
    .WithDestroy();

#pragma warning restore ASPIREAZURE003
""");

                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified AppHost.cs with AKS + external Helm chart");
                output.WriteLine($"New content:\n{content}");
            }

            // Step 7: Navigate to AppHost project directory
            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Set env vars for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy to AKS
            output.WriteLine("Step 9: Starting AKS deployment with external Helm chart...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 10: Get AKS credentials so kubectl/helm can talk to the cluster
            output.WriteLine("Step 10: Getting AKS credentials...");
            await auto.TypeAsync($"AKS_NAME=$(az aks list -g {resourceGroupName} --query '[0].name' -o tsv) && " +
                  $"az aks get-credentials -g {resourceGroupName} -n $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 11: Wait for app pods to be ready
            output.WriteLine("Step 11: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all --all-namespaces --timeout=300s 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync("kubectl get pods --all-namespaces && kubectl get svc --all-namespaces");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 12: Verify podinfo pods come up with the configured replica count.
            output.WriteLine("Step 12: Verifying podinfo Helm chart deployment...");
            await auto.TypeAsync(
                "for i in $(seq 1 30); do " +
                "READY=$(kubectl get deploy -n podinfo -l app.kubernetes.io/name=podinfo " +
                "-o jsonpath='{.items[0].status.readyReplicas}' 2>/dev/null); " +
                "[ \"$READY\" = \"2\" ] && echo 'VERIFY_OK: podinfo has 2 ready replicas' && break; " +
                "echo \"Attempt $i: readyReplicas=$READY, waiting...\"; sleep 5; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 13: Verify the Helm release exists.
            output.WriteLine("Step 13: Verifying podinfo Helm release...");
            await auto.TypeAsync("helm list -n podinfo");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 14: Verify podinfo is serving traffic.
            output.WriteLine("Step 14: Verifying podinfo is serving traffic...");
            await auto.TypeAsync("kubectl port-forward svc/podinfo 19898:9898 -n podinfo > /dev/null 2>&1 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            await auto.TypeAsync("sleep 3");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 10); do sleep 3; " +
                "S=$(curl -so /dev/null -w '%{http_code}' http://localhost:19898/ 2>/dev/null); " +
                "[ \"$S\" = \"200\" ] && echo \"VERIFY_OK: podinfo HTTP $S\" && OK=1 && break; " +
                "echo \"Attempt $i: HTTP $S\"; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: podinfo not responding'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromSeconds(120));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 15: Destroy and verify the external Helm chart was uninstalled too.
            output.WriteLine("Step 15: Destroying deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 16: Verify the podinfo release is gone (this is the WithDestroy() contract).
            output.WriteLine("Step 16: Verifying podinfo Helm release was uninstalled by aspire destroy...");
            await auto.TypeAsync(
                "RELEASES=$(helm list -n podinfo -q 2>/dev/null); " +
                "if [ -z \"$RELEASES\" ]; then echo 'VERIFY_OK: podinfo release was uninstalled'; " +
                "else echo \"FAIL: podinfo release still exists: $RELEASES\"; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 17: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployAksWithHelmChart),
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
                nameof(DeployAksWithHelmChart),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName);
        }
    }

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
