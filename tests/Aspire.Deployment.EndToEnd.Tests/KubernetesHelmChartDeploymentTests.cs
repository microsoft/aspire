// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end test for deploying an Aspire application to a BYO AKS cluster with an
/// external Helm chart (podinfo) installed via <c>AddHelmChart</c>. Verifies that both
/// the Aspire application and the external chart are deployed successfully, and that
/// the podinfo pods are running with the configured replica count and values.
/// </summary>
public sealed class KubernetesHelmChartDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployStarterWithExternalHelmChartToKubernetes()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterWithExternalHelmChartCore(cancellationToken);
    }

    private async Task DeployStarterWithExternalHelmChartCore(CancellationToken cancellationToken)
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

        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("k8shelm");
        var clusterName = $"aks-{DeploymentE2ETestHelpers.GetRunId()}-{DeploymentE2ETestHelpers.GetRunAttempt()}";
        var acrName = $"acrh{DeploymentE2ETestHelpers.GetRunId()}{DeploymentE2ETestHelpers.GetRunAttempt()}".ToLowerInvariant();
        acrName = new string(acrName.Where(char.IsLetterOrDigit).Take(50).ToArray());
        if (acrName.Length < 5)
        {
            acrName = $"acrtest{Guid.NewGuid():N}"[..24];
        }

        var projectName = "K8sHelmChart";
        var k8sNamespace = "helmtest";

        output.WriteLine($"Test: {nameof(DeployStarterWithExternalHelmChartToKubernetes)}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"AKS Cluster: {clusterName}");
        output.WriteLine($"ACR Name: {acrName}");
        output.WriteLine($"K8s Namespace: {k8sNamespace}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // ===== PHASE 1: Provision AKS Infrastructure =====

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            output.WriteLine("Step 2: Registering resource providers...");
            await auto.TypeAsync(
                "az provider register --namespace Microsoft.ContainerService --wait && " +
                "az provider register --namespace Microsoft.ContainerRegistry --wait");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            output.WriteLine("Step 3: Creating resource group...");
            await auto.TypeAsync($"az group create --name {resourceGroupName} --location westus3 --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 4: Creating ACR...");
            await auto.TypeAsync($"az acr create --resource-group {resourceGroupName} --name {acrName} --sku Basic --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 5: Creating AKS cluster (10-15 minutes)...");
            await auto.TypeAsync(
                $"az aks create " +
                $"--resource-group {resourceGroupName} " +
                $"--name {clusterName} " +
                $"--node-count 1 " +
                $"--node-vm-size Standard_D2as_v5 " +
                $"--generate-ssh-keys " +
                $"--attach-acr {acrName} " +
                $"--enable-managed-identity " +
                $"--output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(20));

            output.WriteLine("Step 6: Configuring kubectl...");
            await auto.TypeAsync($"az aks get-credentials --resource-group {resourceGroupName} --name {clusterName} --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("kubectl get nodes");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // ===== PHASE 2: Create Aspire Project with AddHelmChart =====

            await auto.InstallCurrentBuildAspireCliAsync(counter, output, "Step 7");

            output.WriteLine("Step 8: Creating Aspire starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 9: Adding Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 10: Modify AppHost.cs
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Step 10: Modifying AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            var buildRunPattern = "builder.Build().Run();";
            var replacement = $$"""
// Kubernetes environment with external Helm chart
var registryEndpoint = builder.AddParameter("registryendpoint");
var registry = builder.AddContainerRegistry("registry", registryEndpoint);

var k8s = builder.AddKubernetesEnvironment("k8s")
    .WithHelm(helm =>
    {
        helm.WithNamespace(builder.AddParameter("namespace"));
        helm.WithChartVersion(builder.AddParameter("chartversion"));
    });

// Install podinfo as an external Helm chart
k8s.AddHelmChart("podinfo", "oci://ghcr.io/stefanprodan/charts/podinfo", "6.7.1")
    .WithHelmValue("replicaCount", "2")
    .WithHelmValue("ui.message", "Aspire AddHelmChart E2E test");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);

            var topOfFile = "#pragma warning disable ASPIREPIPELINES001\n#pragma warning disable ASPIRECOMPUTE003\nusing Aspire.Hosting.Kubernetes;\n";
            if (!content.Contains("#pragma warning disable ASPIREPIPELINES001"))
            {
                content = topOfFile + content;
            }

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified AppHost.cs with AddKubernetesEnvironment + AddHelmChart");

            // Navigate to AppHost dir
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // ===== PHASE 3: Deploy with aspire deploy =====

            output.WriteLine("Step 11: Refreshing ACR login...");
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 12: Setting deployment parameters...");
            await auto.TypeAsync(
                $"export Parameters__registryendpoint={acrName}.azurecr.io && " +
                $"export Parameters__namespace={k8sNamespace} && " +
                "export Parameters__chartversion=0.1.0");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 13: Running aspire deploy...");
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(15));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // ===== PHASE 4: Verify =====

            output.WriteLine("Step 14: Waiting for app pods...");
            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod --all -n {k8sNamespace} --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync($"kubectl get pods -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Verify podinfo pods are running in their own namespace
            output.WriteLine("Step 15: Verifying podinfo Helm chart deployment...");
            await auto.TypeAsync(
                "for i in $(seq 1 30); do " +
                "READY=$(kubectl get deploy -n podinfo -l app.kubernetes.io/name=podinfo " +
                "-o jsonpath='{.items[0].status.readyReplicas}' 2>/dev/null); " +
                "[ \"$READY\" = \"2\" ] && echo 'VERIFY_OK: podinfo has 2 ready replicas' && break; " +
                "echo \"Attempt $i: readyReplicas=$READY, waiting...\"; sleep 5; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Verify Helm release
            await auto.TypeAsync("helm list -n podinfo");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Verify podinfo is serving traffic
            output.WriteLine("Step 16: Verifying podinfo is serving traffic...");
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
                "[ \"$OK\" = \"1\" ] || echo 'WARN: podinfo not responding'");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // ===== PHASE 5: Cleanup =====

            output.WriteLine("Step 17: Destroying deployment...");
            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Helm chart deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterWithExternalHelmChartToKubernetes),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["cluster"] = clusterName,
                    ["acr"] = acrName,
                    ["project"] = projectName
                },
                duration);

            output.WriteLine("✅ Test passed - Aspire app with external Helm chart deployed to AKS!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterWithExternalHelmChartToKubernetes),
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

            if (process.ExitCode == 0)
            {
                output.WriteLine($"Resource group deletion initiated: {resourceGroupName}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Deletion initiated");
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync();
                output.WriteLine($"Resource group deletion may have failed (exit code {process.ExitCode}): {error}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, $"Exit code {process.ExitCode}: {error}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to cleanup resource group: {ex.Message}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, ex.Message);
        }
    }
}
