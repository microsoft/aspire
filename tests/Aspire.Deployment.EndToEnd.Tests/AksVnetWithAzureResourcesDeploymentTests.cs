// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// Full-stack end-to-end test for deploying an Aspire application to AKS with VNet integration,
/// multiple node pools, Azure Key Vault with Private Endpoint, and Azure Storage.
/// Verifies workload identity connectivity from pods to Azure services.
/// </summary>
public sealed class AksVnetWithAzureResourcesDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for AKS + VNet + PE provisioning plus deployment.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployAksVnetWithAzureResources()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployAksVnetWithAzureResourcesCore(cancellationToken);
    }

    private async Task DeployAksVnetWithAzureResourcesCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("aksvnetres");
        var projectName = "AksVnetRes";

        output.WriteLine($"Test: {nameof(DeployAksVnetWithAzureResources)}");
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

            // Step 3: Create starter project (no Redis)
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add hosting packages
            output.WriteLine("Step 5a: Adding Azure Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5b: Adding Azure Network hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Network");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5c: Adding Azure Key Vault hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.KeyVault");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5d: Adding Azure Storage hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Storage");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Add client packages to ApiService
            output.WriteLine("Step 6a: Adding Key Vault client package to ApiService...");
            await auto.TypeAsync($"dotnet add {projectName}.ApiService package Aspire.Azure.Security.KeyVault --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            output.WriteLine("Step 6b: Adding Storage Blobs client package to ApiService...");
            await auto.TypeAsync($"dotnet add {projectName}.ApiService package Aspire.Azure.Storage.Blobs --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Step 7: Modify AppHost.cs to add VNet + AKS + Key Vault PE + Storage
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
                var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

                output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);

                // Insert full infrastructure AFTER CreateBuilder so variables
                // are available when apiservice references them.
                content = content.Replace(
                    "var builder = DistributedApplication.CreateBuilder(args);",
                    """
var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIREAZURE003

// VNet with subnets for AKS and Private Endpoints
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.1.0.0/16");
var aksSubnet = vnet.AddSubnet("aks-subnet", "10.1.0.0/22");
var peSubnet = vnet.AddSubnet("pe-subnet", "10.1.4.0/24");

// AKS environment with VNet integration
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithSystemNodePool("Standard_D2as_v5")
    .WithSubnet(aksSubnet);

var workerPool = aks.AddNodePool("workload", "Standard_D2as_v5", 1, 3);

// Azure resources with Private Endpoint
var vault = builder.AddAzureKeyVault("vault");
peSubnet.AddPrivateEndpoint(vault);

var storage = builder.AddAzureStorage("storage");
var blobs = storage.AddBlobs("blobs");

#pragma warning restore ASPIREAZURE003
""");

                // Add WithReference and WithNodePool to apiservice
                content = content.Replace(
                    "builder.AddProject<Projects." + projectName + "_ApiService>(\"apiservice\")",
                    "builder.AddProject<Projects." + projectName + "_ApiService>(\"apiservice\")\n    .WithReference(vault)\n    .WithReference(blobs)\n    .WithNodePool(workerPool)");

                // Add required using directive for WithNodePool
                content = "using Aspire.Hosting.Kubernetes.Extensions;\n" + content;

                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");
                output.WriteLine($"New content:\n{content}");
            }

            // Step 8: Modify ApiService Program.cs to register Key Vault and Storage clients
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var apiProgramPath = Path.Combine(projectDir, $"{projectName}.ApiService", "Program.cs");

                output.WriteLine($"Looking for ApiService Program.cs at: {apiProgramPath}");

                var apiContent = File.ReadAllText(apiProgramPath);

                apiContent = "using Azure.Security.KeyVault.Secrets;\nusing Azure.Storage.Blobs;\n" + apiContent;

                apiContent = apiContent.Replace(
                    "builder.AddServiceDefaults();",
                    """
builder.AddServiceDefaults();
builder.AddAzureKeyVaultClient("vault");
builder.AddAzureBlobServiceClient("blobs");
""");

                apiContent = apiContent.Replace(
                    "app.Run();",
                    """
app.MapGet("/test-keyvault", async (SecretClient client) =>
{
    await foreach (var _ in client.GetPropertiesOfSecretsAsync()) { break; }
    return "ok";
});

app.MapGet("/test-storage", async (BlobServiceClient client) =>
{
    await foreach (var _ in client.GetBlobContainersAsync()) { break; }
    return "ok";
});

app.Run();
""");

                File.WriteAllText(apiProgramPath, apiContent);

                output.WriteLine($"Modified ApiService Program.cs at: {apiProgramPath}");
                output.WriteLine($"New content:\n{apiContent}");
            }

            // Step 9: Navigate to AppHost project directory
            output.WriteLine("Step 9: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 10: Set environment variables for deployment
            output.WriteLine("Step 10: Setting environment variables...");
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 11: Deploy using aspire deploy
            output.WriteLine("Step 11: Starting deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 12: Get AKS credentials for the provisioned cluster
            output.WriteLine("Step 12: Getting AKS credentials...");
            await auto.TypeAsync($"AKS_NAME=$(az aks list -g {resourceGroupName} --query '[0].name' -o tsv) && " +
                  $"az aks get-credentials -g {resourceGroupName} -n $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 13: Wait for all pods to be ready
            output.WriteLine("Step 13: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all --all-namespaces --timeout=300s 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Show pod and service status for debugging
            await auto.TypeAsync("kubectl get pods --all-namespaces && kubectl get svc --all-namespaces");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 14: Verify VNet and subnets
            output.WriteLine("Step 14: Verifying VNet and subnets...");
            await auto.TypeAsync($"az network vnet list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 15: Verify Private Endpoint and DNS zones
            output.WriteLine("Step 15: Verifying Private Endpoint infrastructure...");
            await auto.TypeAsync($"az network private-endpoint list -g \"{resourceGroupName}\" --query \"[].{{name:name,state:provisioningState}}\" -o table && " +
                      $"az network private-dns zone list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 16: Verify Azure resources (Key Vault and Storage)
            output.WriteLine("Step 16: Verifying Azure resources...");
            await auto.TypeAsync($"az keyvault list -g {resourceGroupName} --query \"[].name\" -o tsv && " +
                      $"az storage account list -g {resourceGroupName} --query \"[].name\" -o tsv");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 17: Verify connectivity via port-forward
            output.WriteLine("Step 17: Discovering deployment namespace...");
            await auto.TypeAsync("NS=$(kubectl get svc --all-namespaces -o jsonpath='{range .items[?(@.metadata.name==\"apiservice-service\")]}{.metadata.namespace}{end}') && echo \"Namespace: $NS\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 17: Port-forwarding to apiservice...");
            await auto.TypeAsync("kubectl port-forward svc/apiservice-service 18082:8080 -n $NS > /dev/null 2>&1 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Verify Key Vault connectivity via PE
            output.WriteLine("Step 17: Verifying Key Vault connectivity...");
            await auto.TypeAsync("OK=0; for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18082/test-keyvault -o /dev/null -w '%{http_code}' && echo ' OK' && OK=1 && break; done; [ \"$OK\" = \"1\" ] || { echo 'FAIL: /test-keyvault unreachable after 10 retries'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Verify Storage connectivity
            output.WriteLine("Step 17: Verifying Storage connectivity...");
            await auto.TypeAsync("OK=0; for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18082/test-storage -o /dev/null -w '%{http_code}' && echo ' OK' && OK=1 && break; done; [ \"$OK\" = \"1\" ] || { echo 'FAIL: /test-storage unreachable after 10 retries'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Kill port-forward
            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 18: Clean up using aspire destroy
            output.WriteLine("Step 18: Destroying deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 19: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployAksVnetWithAzureResources),
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
                nameof(DeployAksVnetWithAzureResources),
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
