// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying a TypeScript (Express/React) Aspire application to Azure Kubernetes Service (AKS)
/// using <c>aspire deploy</c> with the Kubernetes pipeline.
/// This validates that the <c>aspire deploy</c> pipeline works end-to-end with a TypeScript AppHost
/// targeting a real AKS cluster, including image building, pushing to ACR, chart generation, and Helm deployment.
/// </summary>
public sealed class AksDeployTypeScriptDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for AKS provisioning (~10-15 min) plus aspire deploy pipeline.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployTypeScriptTemplateToAksViaAspireDeploy()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployTypeScriptTemplateToAksViaAspireDeployCore(cancellationToken);
    }

    private async Task DeployTypeScriptTemplateToAksViaAspireDeployCore(CancellationToken cancellationToken)
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

        // Generate unique names for Azure resources
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("aksts");
        var clusterName = $"aks-{DeploymentE2ETestHelpers.GetRunId()}-{DeploymentE2ETestHelpers.GetRunAttempt()}";
        // ACR names must be alphanumeric only, 5-50 chars, globally unique
        var acrName = $"acrt{DeploymentE2ETestHelpers.GetRunId()}{DeploymentE2ETestHelpers.GetRunAttempt()}".ToLowerInvariant();
        acrName = new string(acrName.Where(char.IsLetterOrDigit).Take(50).ToArray());
        if (acrName.Length < 5)
        {
            acrName = $"acrtest{Guid.NewGuid():N}"[..24];
        }

        output.WriteLine($"Test: {nameof(DeployTypeScriptTemplateToAksViaAspireDeploy)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"AKS Cluster: {clusterName}");
        output.WriteLine($"ACR Name: {acrName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // Project name for the Aspire application
            var projectName = "AksTsDeploy";

            // ===== PHASE 1: Provision AKS Infrastructure =====

            // Step 1: Prepare environment
            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            // Step 2: Register required resource providers
            output.WriteLine("Step 2: Registering required resource providers...");
            await auto.TypeAsync("az provider register --namespace Microsoft.ContainerService --wait && " +
                  "az provider register --namespace Microsoft.ContainerRegistry --wait");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 3: Create resource group
            output.WriteLine("Step 3: Creating resource group...");
            await auto.TypeAsync($"az group create --name {resourceGroupName} --location westus3 --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 4: Create Azure Container Registry
            output.WriteLine("Step 4: Creating Azure Container Registry...");
            await auto.TypeAsync($"az acr create --resource-group {resourceGroupName} --name {acrName} --sku Basic --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Step 4b: Login to ACR immediately (before AKS creation which takes 10-15 min).
            // The OIDC federated token expires after ~5 minutes, so we must authenticate with
            // ACR while it's still fresh. Docker credentials persist in ~/.docker/config.json.
            output.WriteLine("Step 4b: Logging into Azure Container Registry (early, before token expires)...");
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 5: Create AKS cluster with ACR attached
            // Using minimal configuration: 1 node, Standard_D2s_v3 (widely available with quota)
            output.WriteLine("Step 5: Creating AKS cluster (this may take 10-15 minutes)...");
            await auto.TypeAsync($"az aks create " +
                  $"--resource-group {resourceGroupName} " +
                  $"--name {clusterName} " +
                  $"--node-count 1 " +
                  $"--node-vm-size Standard_D2s_v3 " +
                  $"--generate-ssh-keys " +
                  $"--attach-acr {acrName} " +
                  $"--enable-managed-identity " +
                  $"--output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(20));

            // Step 6: Ensure AKS can pull from ACR (update attachment to ensure role propagation)
            output.WriteLine("Step 6: Verifying AKS-ACR integration...");
            await auto.TypeAsync($"az aks update --resource-group {resourceGroupName} --name {clusterName} --attach-acr {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 7: Configure kubectl credentials
            output.WriteLine("Step 7: Configuring kubectl credentials...");
            await auto.TypeAsync($"az aks get-credentials --resource-group {resourceGroupName} --name {clusterName} --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 8: Verify kubectl connectivity
            output.WriteLine("Step 8: Verifying kubectl connectivity...");
            await auto.TypeAsync("kubectl get nodes");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 9: Verify cluster is healthy
            output.WriteLine("Step 9: Verifying cluster health...");
            await auto.TypeAsync("kubectl cluster-info");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // ===== PHASE 2: Create TypeScript Aspire Project with Kubernetes Deploy Configuration =====

            // Step 10: Set up CLI environment (in CI)
            // TypeScript apphosts need the full bundle (not just the CLI binary) because
            // the prebuilt AppHost server is required for aspire add to regenerate SDK code.
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                var prNumber = DeploymentE2ETestHelpers.GetPrNumber();
                if (prNumber > 0)
                {
                    output.WriteLine($"Step 10: Installing Aspire bundle from PR #{prNumber}...");
                    await auto.InstallAspireBundleFromPullRequestAsync(prNumber, counter);
                }
                await auto.SourceAspireBundleEnvironmentAsync(counter);
            }

            // Step 11: Create TypeScript Express/React project using aspire new
            output.WriteLine("Step 11: Creating TypeScript Express/React project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.ExpressReact);

            // Step 12: Navigate to project directory
            output.WriteLine("Step 12: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 13: Add Aspire.Hosting.Kubernetes package
            output.WriteLine("Step 13: Adding Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 14: Regenerate TypeScript SDK with Kubernetes types
            output.WriteLine("Step 14: Regenerating TypeScript SDK...");
            await auto.TypeAsync("aspire restore");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 15: Modify apphost.ts to use AddContainerRegistry + AddKubernetesEnvironment with Helm
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostFilePath = Path.Combine(projectDir, "apphost.ts");

            output.WriteLine($"Modifying apphost.ts at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Add Kubernetes environment with Helm configuration before build().run()
            content = content.Replace(
                "await builder.build().run();",
                """
// Add container registry for image push
const registryEndpoint = await builder.addParameter("registryendpoint");
await builder.addContainerRegistry("registry", registryEndpoint);

// Register parameters before using them in the Helm callback
const k8sNamespace = await builder.addParameter("namespace");
const chartVersion = await builder.addParameter("chartversion");

// Add Kubernetes environment with Helm deployment
const k8sEnv = await builder.addKubernetesEnvironment("env");
await k8sEnv.withHelm(async (helm) => {
    await helm.withNamespace(k8sNamespace);
    await helm.withChartVersion(chartVersion);
});

await builder.build().run();
""");

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified apphost.ts with Kubernetes environment configuration");

            // Step 16: Re-login to ACR after AKS creation to refresh Docker credentials.
            // The initial login (Step 4b) may have expired during the 10-15 min AKS provisioning.
            output.WriteLine("Step 16: Refreshing ACR login...");
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // ===== PHASE 3: Deploy using aspire deploy =====

            // Step 17: Run aspire deploy with interactive parameter prompts.
            // The deploy pipeline will:
            // 1. Build container images for project resources
            // 2. Push images to ACR
            // 3. Generate Helm chart
            // 4. Deploy via helm install
            //
            // Parameters are prompted in declaration order:
            // 1. registryendpoint - the ACR login server
            // 2. namespace - the K8s namespace to deploy to
            // 3. chartversion - the Helm chart version
            output.WriteLine("Step 17: Running aspire deploy (builds, pushes, generates charts, deploys)...");
            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("registryendpoint", $"{acrName}.azurecr.io"),
                    ("namespace", "default"),
                    ("chartversion", "0.1.0"),
                ],
                pipelineTimeout: TimeSpan.FromMinutes(15));

            // ===== PHASE 4: Verify Deployment =====

            // Step 18: Wait for pods to be ready
            output.WriteLine("Step 18: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all -n default --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Step 19: Verify pods are running
            output.WriteLine("Step 19: Verifying pods are running...");
            await auto.TypeAsync("kubectl get pods -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 20: Verify deployments are healthy
            output.WriteLine("Step 20: Verifying deployments...");
            await auto.TypeAsync("kubectl get deployments -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 21: List services for debugging and verify at least one exists
            output.WriteLine("Step 21: Listing services...");
            await auto.TypeAsync("kubectl get services -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 22: Verify the app service is serving traffic via port-forward
            // The Express/React template creates a single "app" resource that bundles both
            // the Express API and React frontend. The Helm chart exposes it as "app-service" on port 8000.
            output.WriteLine("Step 22: Verifying app service endpoint...");
            await auto.TypeAsync("kubectl port-forward svc/app-service 18080:8000 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18080/ -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 23: Clean up port-forwards
            output.WriteLine("Step 23: Cleaning up port-forwards...");
            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 24: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Full AKS TypeScript aspire deploy completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployTypeScriptTemplateToAksViaAspireDeploy),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["cluster"] = clusterName,
                    ["acr"] = acrName,
                    ["project"] = projectName
                },
                duration);

            output.WriteLine("✅ Test passed - TypeScript Aspire app deployed to AKS via aspire deploy!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployTypeScriptTemplateToAksViaAspireDeploy),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Clean up the resource group we created (includes AKS cluster and ACR)
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
