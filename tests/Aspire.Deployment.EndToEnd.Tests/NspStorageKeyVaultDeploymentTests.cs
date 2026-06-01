// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// Deployment test for Azure Network Security Perimeter (NSP) with Storage and Key Vault.
/// Deploys a React starter app with Container Apps, Storage, Key Vault, and an NSP
/// that grants the current subscription inbound access to both PaaS resources.
/// Verifies the ASP.NET backend can connect to Storage and Key Vault through the NSP.
/// </summary>
public sealed class NspStorageKeyVaultDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task DeployReactTemplateWithNspStorageAndKeyVault()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployReactTemplateWithNspStorageAndKeyVaultCore(cancellationToken);
    }

    private async Task DeployReactTemplateWithNspStorageAndKeyVaultCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("nsp-react");
        var projectName = "NspReactApp";

        output.WriteLine($"Test: {nameof(DeployReactTemplateWithNspStorageAndKeyVault)}");
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

            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            // Step 3: Create React + ASP.NET Core project
            output.WriteLine("Step 3: Creating React + ASP.NET Core project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.JsReact, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5a: Add Aspire.Hosting.Azure.AppContainers
            output.WriteLine("Step 5a: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 5b: Add Aspire.Hosting.Azure.Network (for NSP)
            output.WriteLine("Step 5b: Adding Azure Network hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Network");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 5c: Add Aspire.Hosting.Azure.Storage
            output.WriteLine("Step 5c: Adding Azure Storage hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Storage");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 5d: Add Aspire.Hosting.Azure.KeyVault
            output.WriteLine("Step 5d: Adding Azure Key Vault hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.KeyVault");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6a: Add Storage Blob client package to the Server project
            output.WriteLine("Step 6a: Adding blob client package to Server project...");
            await auto.TypeAsync($"dotnet add {projectName}.Server package Aspire.Azure.Storage.Blobs --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Step 6b: Add Key Vault client package to the Server project
            output.WriteLine("Step 6b: Adding Key Vault client package to Server project...");
            await auto.TypeAsync($"dotnet add {projectName}.Server package Aspire.Azure.Security.KeyVault --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Step 7: Modify AppHost.cs to add Container App Env, Storage, KeyVault, and NSP
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
                var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

                output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);

                // Insert NSP, Storage, KeyVault, and Container App Environment code after builder creation
                content = content.Replace(
                    "var builder = DistributedApplication.CreateBuilder(args);",
                    $$"""
using Aspire.Hosting.Azure;
using Azure.Provisioning.Network;

var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIREAZURE003

// Azure Container App Environment
builder.AddAzureContainerAppEnvironment("env");

// Network Security Perimeter with subscription-level inbound access
var nsp = builder.AddNetworkSecurityPerimeter("nsp")
    .WithAccessRule(new AzureNspAccessRule
    {
        Name = "allow-subscription",
        Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
        Subscriptions = { "/subscriptions/{{subscriptionId}}" }
    });

// Azure Storage with Blobs
var storage = builder.AddAzureStorage("storage")
    .WithNetworkSecurityPerimeter(nsp);
var blobs = storage.AddBlobs("blobs");

// Azure Key Vault
var kv = builder.AddAzureKeyVault("kv")
    .WithNetworkSecurityPerimeter(nsp);

#pragma warning restore ASPIREAZURE003
""");

                // Add .WithReference(blobs).WithReference(kv) to the server
                // The server chain has .WithHttpHealthCheck("/health") followed by .WithExternalHttpEndpoints();
                content = content.Replace(
                    ".WithHttpHealthCheck(\"/health\")",
                    """
.WithHttpHealthCheck("/health")
    .WithReference(blobs)
    .WithReference(kv)
""");

                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified AppHost.cs with NSP + Storage + KeyVault + WithReference");
                output.WriteLine($"New content:\n{content}");
            }

            // Step 8: Modify Server Program.cs to register Storage Blob and Key Vault clients
            //         and add verification endpoints that exercise those resources
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var serverProgramPath = Path.Combine(projectDir, $"{projectName}.Server", "Program.cs");

                output.WriteLine($"Looking for Server Program.cs at: {serverProgramPath}");

                var content = File.ReadAllText(serverProgramPath);

                // Add using statements at the top
                content = "using Azure.Security.KeyVault.Secrets;\nusing Azure.Storage.Blobs;\n" + content;

                // Register the Aspire client integrations
                content = content.Replace(
                    "builder.AddServiceDefaults();",
                    """
builder.AddServiceDefaults();
builder.AddAzureBlobServiceClient("blobs");
builder.AddAzureKeyVaultClient("kv");
""");

                // Add verification endpoints before MapDefaultEndpoints
                content = content.Replace(
                    "app.MapDefaultEndpoints();",
                    """
// Endpoint to verify Azure Blob Storage connectivity through the NSP.
app.MapGet("/api/verify-blobs", async (BlobServiceClient blobServiceClient) =>
{
    var containerClient = blobServiceClient.GetBlobContainerClient("nsp-test");
    await containerClient.CreateIfNotExistsAsync();
    var blobName = $"test-{Guid.NewGuid():N}.txt";
    var blobClient = containerClient.GetBlobClient(blobName);
    var testContent = $"Hello from NSP test at {DateTime.UtcNow:O}";
    await blobClient.UploadAsync(BinaryData.FromString(testContent), overwrite: true);
    var download = await blobClient.DownloadContentAsync();
    var readBack = download.Value.Content.ToString();
    await blobClient.DeleteAsync();
    return Results.Ok(new { status = "ok", match = testContent == readBack });
});

// Endpoint to verify Azure Key Vault connectivity through the NSP.
// Lists secret properties to prove authentication and network access work.
app.MapGet("/api/verify-keyvault", async (SecretClient secretClient) =>
{
    var count = 0;
    await foreach (var secret in secretClient.GetPropertiesOfSecretsAsync())
    {
        count++;
    }
    return Results.Ok(new { status = "ok", secretCount = count });
});

app.MapDefaultEndpoints();
""");

                File.WriteAllText(serverProgramPath, content);

                output.WriteLine($"Modified Server Program.cs to add blob/key vault client registrations and verification endpoints");
            }

            // Step 9: Navigate to AppHost project directory
            output.WriteLine("Step 9: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 10: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 11: Deploy to Azure
            output.WriteLine("Step 11: Starting Azure deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 12: Verify deployed endpoints and resource connectivity through the NSP
            output.WriteLine("Step 12: Verifying deployed endpoints and resource connectivity...");
            await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
                      "urls=$(az containerapp list -g \"$RG_NAME\" --query \"[].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | grep -v '\\.internal\\.') && " +
                      "if [ -z \"$urls\" ]; then echo \"❌ No external container app endpoints found\"; exit 1; fi && " +
                      "failed=0 && " +
                      "for url in $urls; do " +
                      "echo \"Checking https://$url...\"; " +
                      "success=0; " +
                      "for i in $(seq 1 18); do " +
                      "STATUS=$(curl -s -o /dev/null -w \"%{http_code}\" \"https://$url\" --max-time 10 2>/dev/null); " +
                      "if [ \"$STATUS\" = \"200\" ] || [ \"$STATUS\" = \"302\" ]; then echo \"  ✅ $STATUS (attempt $i)\"; success=1; break; fi; " +
                      "echo \"  Attempt $i: $STATUS, retrying in 10s...\"; sleep 10; " +
                      "done; " +
                      "if [ \"$success\" -eq 0 ]; then echo \"  ❌ Failed after 18 attempts\"; failed=1; fi; " +
                      "done && " +
                      "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more endpoint checks failed\"; exit 1; fi && " +
                      "SERVER_FQDN=$(az containerapp list -g \"$RG_NAME\" --query \"[?contains(name,'server')].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | head -1) && " +
                      "if [ -z \"$SERVER_FQDN\" ]; then echo \"❌ Could not find server container app\"; exit 1; fi && " +
                      "echo \"Server FQDN: $SERVER_FQDN\" && " +
                      "echo \"Verifying Blob Storage connectivity...\" && " +
                      "BLOB_RESULT=\"\" && blob_ok=0 && " +
                      "for i in $(seq 1 12); do " +
                      "BLOB_RESULT=$(curl -s \"https://$SERVER_FQDN/api/verify-blobs\" --max-time 30 2>/dev/null); " +
                      "if echo \"$BLOB_RESULT\" | grep -q '\"status\":\"ok\"'; then echo \"  ✅ Blob Storage: $BLOB_RESULT\"; blob_ok=1; break; fi; " +
                      "echo \"  Attempt $i: $BLOB_RESULT, retrying in 10s...\"; sleep 10; " +
                      "done && " +
                      "echo \"Verifying Key Vault connectivity...\" && " +
                      "KV_RESULT=\"\" && kv_ok=0 && " +
                      "for i in $(seq 1 12); do " +
                      "KV_RESULT=$(curl -s \"https://$SERVER_FQDN/api/verify-keyvault\" --max-time 30 2>/dev/null); " +
                      "if echo \"$KV_RESULT\" | grep -q '\"status\":\"ok\"'; then echo \"  ✅ Key Vault: $KV_RESULT\"; kv_ok=1; break; fi; " +
                      "echo \"  Attempt $i: $KV_RESULT, retrying in 10s...\"; sleep 10; " +
                      "done && " +
                      "if [ \"$blob_ok\" -eq 0 ] || [ \"$kv_ok\" -eq 0 ]; then echo \"❌ Resource connectivity verification failed (blob=$blob_ok, kv=$kv_ok)\"; exit 1; fi && " +
                      "echo \"✅ All endpoint and resource connectivity checks passed\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(8));

            // Step 13: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployReactTemplateWithNspStorageAndKeyVault),
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
                nameof(DeployReactTemplateWithNspStorageAndKeyVault),
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
