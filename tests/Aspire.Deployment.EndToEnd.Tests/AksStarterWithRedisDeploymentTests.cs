// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire starter template with Redis to AKS.
/// This validates that the starter template with Redis cache works out-of-the-box on Kubernetes.
/// </summary>
public sealed class AksStarterWithRedisDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for AKS provisioning (~10-15 min) plus deployment.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployStarterTemplateWithRedisToAks()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterTemplateWithRedisToAksCore(cancellationToken);
    }

    private async Task DeployStarterTemplateWithRedisToAksCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("aksredis");
        var clusterName = $"aks-{DeploymentE2ETestHelpers.GetRunId()}-{DeploymentE2ETestHelpers.GetRunAttempt()}";
        var redisPassword = Guid.NewGuid().ToString("N");
        // ACR names must be alphanumeric only, 5-50 chars, globally unique
        var acrName = $"acrr{DeploymentE2ETestHelpers.GetRunId()}{DeploymentE2ETestHelpers.GetRunAttempt()}".ToLowerInvariant();
        acrName = new string(acrName.Where(char.IsLetterOrDigit).Take(50).ToArray());
        if (acrName.Length < 5)
        {
            acrName = $"acrtest{Guid.NewGuid():N}"[..24];
        }

        output.WriteLine($"Test: {nameof(DeployStarterTemplateWithRedisToAks)}");
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

            var projectName = "AksRedis";

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

            // Step 6: Ensure AKS can pull from ACR
            // ReconcilingAddons can take several minutes after role assignment updates
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

            // Step 9: Verify cluster health
            output.WriteLine("Step 9: Verifying cluster health...");
            await auto.TypeAsync("kubectl cluster-info");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // ===== PHASE 2: Create Aspire Project with Redis and Generate Helm Charts =====

            // Step 10: Set up CLI environment (in CI)
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                output.WriteLine("Step 10: Using pre-installed Aspire CLI from local build...");
                await auto.SourceAspireCliEnvironmentAsync(counter);
            }

            // Step 11: Create starter project with Redis enabled
            output.WriteLine("Step 11: Creating Aspire starter project with Redis...");
            await auto.AspireNewAsync(projectName, counter);

            // Step 12: Navigate to project directory
            output.WriteLine("Step 12: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 13: Add Aspire.Hosting.Kubernetes package
            output.WriteLine("Step 13: Adding Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync(); // select first version (PR build)
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 14: Modify AppHost.cs to add Kubernetes environment
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Modifying AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert the Kubernetes environment before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Add Kubernetes environment for deployment
builder.AddKubernetesEnvironment("k8s");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);

            // Add required pragma to suppress experimental warning
            if (!content.Contains("#pragma warning disable ASPIREPIPELINES001"))
            {
                content = "#pragma warning disable ASPIREPIPELINES001\n" + content;
            }

            File.WriteAllText(appHostFilePath, content);

            output.WriteLine("Modified AppHost.cs with AddKubernetesEnvironment");

            // Step 15: Navigate to AppHost project directory
            output.WriteLine("Step 15: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 16: Re-login to ACR after AKS creation to refresh Docker credentials.
            // The initial login (Step 4b) may have expired during the 10-15 min AKS provisioning
            // because OIDC federated tokens have a short lifetime (~5 min).
            output.WriteLine("Step 16: Refreshing ACR login...");
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 17: Build and push container images to ACR
            // Only project resources need to be built — Redis uses a public container image
            output.WriteLine("Step 17: Building and pushing container images to ACR...");
            await auto.TypeAsync($"cd .. && " +
                  $"dotnet publish {projectName}.Web/{projectName}.Web.csproj " +
                  $"/t:PublishContainer " +
                  $"/p:ContainerRegistry={acrName}.azurecr.io " +
                  $"/p:ContainerImageName=webfrontend " +
                  $"/p:ContainerImageTag=latest");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            await auto.TypeAsync($"dotnet publish {projectName}.ApiService/{projectName}.ApiService.csproj " +
                  $"/t:PublishContainer " +
                  $"/p:ContainerRegistry={acrName}.azurecr.io " +
                  $"/p:ContainerImageName=apiservice " +
                  $"/p:ContainerImageTag=latest");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Navigate back to AppHost directory
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 18: Run aspire publish to generate Helm charts
            output.WriteLine("Step 18: Running aspire publish to generate Helm charts...");
            await auto.TypeAsync($"aspire publish --output-path ../charts");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(10));

            // Step 19: Verify Helm chart was generated
            output.WriteLine("Step 19: Verifying Helm chart generation...");
            await auto.TypeAsync("ls -la ../charts && cat ../charts/Chart.yaml && cat ../charts/values.yaml");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // ===== PHASE 3: Deploy to AKS and Verify =====

            // Step 20: Verify ACR role assignment has propagated
            output.WriteLine("Step 20: Verifying AKS can pull from ACR...");
            await auto.TypeAsync($"az aks check-acr --resource-group {resourceGroupName} --name {clusterName} --acr {acrName}.azurecr.io");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Step 21: Deploy Helm chart to AKS with ACR image overrides
            // Only project resources need image overrides — Redis uses the public image from the chart
            // Note: Two K8s publisher Helm value bugs require workarounds:
            // 1. secrets.cache.cache_password: The Helm template expression uses the parameter name
            //    (cache_password from "cache-password") but values.yaml uses the env var key (REDIS_PASSWORD).
            //    We must set the parameter name path for the password to reach the K8s Secret.
            // 2. secrets.webfrontend.cache_password: Cross-resource secret references create Helm value
            //    paths under the consuming resource instead of the owning resource (issue #14370).
            output.WriteLine("Step 21: Deploying Helm chart to AKS...");
            await auto.TypeAsync($"helm install aksredis ../charts --namespace default --wait --timeout 10m " +
                  $"--set parameters.webfrontend.webfrontend_image={acrName}.azurecr.io/webfrontend:latest " +
                  $"--set parameters.apiservice.apiservice_image={acrName}.azurecr.io/apiservice:latest " +
                  $"--set secrets.cache.cache_password={redisPassword} " +
                  $"--set secrets.webfrontend.cache_password={redisPassword}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(12));

            // Step 22: Wait for all pods to be ready (including Redis cache)
            // Pods may need time to pull images from ACR and start the application
            output.WriteLine("Step 22: Waiting for all pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod -l app.kubernetes.io/component=apiservice --timeout=300s -n default && " +
                  "kubectl wait --for=condition=ready pod -l app.kubernetes.io/component=webfrontend --timeout=300s -n default && " +
                  "kubectl wait --for=condition=ready pod -l app.kubernetes.io/component=cache --timeout=300s -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(16));

            // Step 22b: Verify Redis container is running and stable (no restarts)
            output.WriteLine("Step 22b: Verifying Redis container is stable...");
            await auto.TypeAsync("kubectl get pod cache-statefulset-0 -o jsonpath='{.status.containerStatuses[0].ready} restarts:{.status.containerStatuses[0].restartCount}'");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 23: Verify all pods are running
            output.WriteLine("Step 23: Verifying pods are running...");
            await auto.TypeAsync("kubectl get pods -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 24: Verify deployments are healthy
            output.WriteLine("Step 24: Verifying deployments...");
            await auto.TypeAsync("kubectl get deployments -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 25: Verify services (should include cache-service for Redis)
            output.WriteLine("Step 25: Verifying services...");
            await auto.TypeAsync("kubectl get services -n default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 26: Verify apiservice endpoint via port-forward
            output.WriteLine("Step 26: Verifying apiservice /weatherforecast endpoint...");
            await auto.TypeAsync("kubectl port-forward svc/apiservice-service 18080:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18080/weatherforecast -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 27: Verify webfrontend root page via port-forward
            output.WriteLine("Step 27: Verifying webfrontend root page...");
            await auto.TypeAsync("kubectl port-forward svc/webfrontend-service 18081:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18081/ -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 28: Verify webfrontend /weather page (exercises webfrontend → apiservice → Redis pipeline)
            // The /weather page uses Blazor SSR streaming rendering which keeps the HTTP connection open.
            // We use -m 5 (max-time) to avoid curl hanging, and capture the status code in a variable
            // because --max-time causes curl to exit non-zero (code 28) even on HTTP 200.
            output.WriteLine("Step 28: Verifying webfrontend /weather page (exercises Redis cache)...");
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3; S=$(curl -so /dev/null -w '%{http_code}' -m 5 http://localhost:18081/weather); [ \"$S\" = \"200\" ] && echo \"$S OK\" && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Step 29: Clean up port-forwards
            output.WriteLine("Step 29: Cleaning up port-forwards...");
            await auto.TypeAsync("kill %1 %2 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 30: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Full AKS + Redis deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterTemplateWithRedisToAks),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["cluster"] = clusterName,
                    ["acr"] = acrName,
                    ["project"] = projectName
                },
                duration);

            output.WriteLine("✅ Test passed - Aspire app with Redis deployed to AKS via Helm!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterTemplateWithRedisToAks),
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
