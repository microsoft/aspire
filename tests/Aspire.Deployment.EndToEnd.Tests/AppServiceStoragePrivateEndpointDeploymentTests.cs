// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for an Azure App Service workload that accesses Azure Blob Storage through a private endpoint.
/// </summary>
public sealed class AppServiceStoragePrivateEndpointDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(55);

    [Fact]
    public async Task DeployReactTemplateWithAppServiceVnetAndStoragePrivateEndpoint()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployReactTemplateWithAppServiceVnetAndStoragePrivateEndpointCore(linkedCts.Token);
    }

    private async Task DeployReactTemplateWithAppServiceVnetAndStoragePrivateEndpointCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("appservice-blob-pe");
        const string projectName = "AppServiceBlobPe";

        output.WriteLine($"Test: {nameof(DeployReactTemplateWithAppServiceVnetAndStoragePrivateEndpoint)}");
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

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            await auto.InstallCurrentBuildAspireCliAsync(counter, output, "Step 2");

            output.WriteLine("Step 3: Creating React + ASP.NET Core project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.JsReact, useRedisCache: false);

            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 5a: Adding Azure App Service hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppService");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5b: Adding Azure Virtual Network hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Network");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5c: Adding Azure Storage hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Storage");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 6: Adding Blob Storage client package to Server project...");
            await auto.TypeAsync($"dotnet add {projectName}.Server package Aspire.Azure.Storage.Blobs --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostFilePath = Path.Combine(projectDir, $"{projectName}.AppHost", "AppHost.cs");
            var serverProgramPath = Path.Combine(projectDir, $"{projectName}.Server", "Program.cs");

            output.WriteLine("Step 7: Configuring App Service VNet integration and Storage private endpoint...");
            var appHostContent = File.ReadAllText(appHostFilePath);
            const string builderCreation = "var builder = DistributedApplication.CreateBuilder(args);";
            const string infrastructure = """
var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIREAZURE003 // Azure Virtual Network APIs are experimental.

var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
var appServiceSubnet = vnet.AddSubnet("app-service-subnet", "10.0.0.0/24");
var privateEndpointSubnet = vnet.AddSubnet("private-endpoint-subnet", "10.0.1.0/24");

var storage = builder.AddAzureStorage("storage");
var blobs = storage.AddBlobs("blobs");
privateEndpointSubnet.AddPrivateEndpoint(blobs);

builder.AddAzureAppServiceEnvironment("infra")
    .WithDelegatedSubnet(appServiceSubnet);

#pragma warning restore ASPIREAZURE003
""";
            if (!appHostContent.Contains(builderCreation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Could not find '{builderCreation}' in the generated AppHost.");
            }

            appHostContent = appHostContent.Replace(builderCreation, infrastructure, StringComparison.Ordinal);

            const string healthCheck = ".WithHttpHealthCheck(\"/health\")";
            const string healthCheckWithStorage = """
.WithHttpHealthCheck("/health")
    .WithReference(blobs)
""";
            if (!appHostContent.Contains(healthCheck, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Could not find '{healthCheck}' in the generated AppHost.");
            }

            appHostContent = appHostContent.Replace(healthCheck, healthCheckWithStorage, StringComparison.Ordinal);
            File.WriteAllText(appHostFilePath, appHostContent);

            output.WriteLine("Step 8: Adding an application-level Blob Storage and DNS verification endpoint...");
            var serverProgramContent = File.ReadAllText(serverProgramPath);
            serverProgramContent = "using System.Net;\nusing Azure.Storage.Blobs;\n" + serverProgramContent;

            const string serviceDefaults = "builder.AddServiceDefaults();";
            const string serviceDefaultsWithBlobClient = """
builder.AddServiceDefaults();
builder.AddAzureBlobServiceClient("blobs");
""";
            if (!serverProgramContent.Contains(serviceDefaults, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Could not find '{serviceDefaults}' in the generated Server project.");
            }

            serverProgramContent = serverProgramContent.Replace(
                serviceDefaults,
                serviceDefaultsWithBlobClient,
                StringComparison.Ordinal);

            const string defaultEndpoints = "app.MapDefaultEndpoints();";
            const string storageProbe = """
// Use the normal Blob endpoint so private DNS resolution, rather than a special connection string,
// determines whether App Service reaches Storage through the private endpoint.
app.MapGet("/api/verify-blobs", async (BlobServiceClient blobServiceClient, CancellationToken cancellationToken) =>
{
    var containerClient = blobServiceClient.GetBlobContainerClient("private-link-test");
    await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

    var blobName = $"test-{Guid.NewGuid():N}.txt";
    var blobClient = containerClient.GetBlobClient(blobName);
    var expectedContent = $"Hello from the App Service private-link test at {DateTime.UtcNow:O}";

    await blobClient.UploadAsync(BinaryData.FromString(expectedContent), overwrite: true, cancellationToken: cancellationToken);
    var downloadedContent = await blobClient.DownloadContentAsync(cancellationToken);
    await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

    var resolvedAddresses = await Dns.GetHostAddressesAsync(blobServiceClient.Uri.Host, cancellationToken);
    return Results.Ok(new
    {
        status = "ok",
        contentMatches = expectedContent == downloadedContent.Value.Content.ToString(),
        resolvedAddresses = resolvedAddresses.Select(address => address.ToString()).ToArray()
    });
});

app.MapDefaultEndpoints();
""";
            if (!serverProgramContent.Contains(defaultEndpoints, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Could not find '{defaultEndpoints}' in the generated Server project.");
            }

            serverProgramContent = serverProgramContent.Replace(defaultEndpoints, storageProbe, StringComparison.Ordinal);
            File.WriteAllText(serverProgramPath, serverProgramContent);

            output.WriteLine("Step 9: Navigating to AppHost project directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 10: Configuring the Azure deployment...");
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 11: Deploying to Azure...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(35));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 12: Verifying the deployed network infrastructure...");
            await VerifyNetworkInfrastructureAsync(auto, counter, resourceGroupName);

            output.WriteLine("Step 13: Verifying Blob access and private DNS from App Service...");
            await VerifyBlobConnectivityAsync(auto, counter, resourceGroupName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployReactTemplateWithAppServiceVnetAndStoragePrivateEndpoint),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployReactTemplateWithAppServiceVnetAndStoragePrivateEndpoint),
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

    private static async Task VerifyNetworkInfrastructureAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string resourceGroupName)
    {
        await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
            "if ! vnet_name=$(az network vnet list -g \"$RG_NAME\" --query \"[?subnets[?name == 'app-service-subnet']].name | [0]\" -o tsv); then echo \"ERROR: Failed to query virtual network\"; exit 1; fi; " +
            "if [ -z \"$vnet_name\" ]; then echo \"ERROR: Virtual network was not found\"; exit 1; fi && " +
            "if ! app_service_subnet_id=$(az network vnet subnet show -g \"$RG_NAME\" --vnet-name \"$vnet_name\" --name app-service-subnet --query id -o tsv); then echo \"ERROR: Failed to query App Service subnet\"; exit 1; fi; " +
            "if ! private_endpoint_subnet_id=$(az network vnet subnet show -g \"$RG_NAME\" --vnet-name \"$vnet_name\" --name private-endpoint-subnet --query id -o tsv); then echo \"ERROR: Failed to query private endpoint subnet\"; exit 1; fi; " +
            "if [ -z \"$app_service_subnet_id\" ] || [ -z \"$private_endpoint_subnet_id\" ] || [ \"$app_service_subnet_id\" = \"$private_endpoint_subnet_id\" ]; then echo \"ERROR: Expected distinct App Service and private endpoint subnets\"; exit 1; fi && " +
            "if ! delegation=$(az network vnet subnet show --ids \"$app_service_subnet_id\" --query \"delegations[?serviceName == 'Microsoft.Web/serverFarms'].serviceName | [0]\" -o tsv); then echo \"ERROR: Failed to query App Service subnet delegation\"; exit 1; fi; " +
            "if [ \"$delegation\" != \"Microsoft.Web/serverFarms\" ]; then echo \"ERROR: App Service subnet is not delegated to Microsoft.Web/serverFarms\"; exit 1; fi && " +
            "if ! private_endpoint_delegation=$(az network vnet subnet show --ids \"$private_endpoint_subnet_id\" --query \"delegations[0].serviceName\" -o tsv); then echo \"ERROR: Failed to query private endpoint subnet delegation\"; exit 1; fi; " +
            "if [ -n \"$private_endpoint_delegation\" ]; then echo \"ERROR: Private endpoint subnet must not be delegated\"; exit 1; fi && " +
            "if ! server_app_name=$(az webapp list -g \"$RG_NAME\" --query \"[?contains(name, 'server')].name | [0]\" -o tsv); then echo \"ERROR: Failed to query App Service workload\"; exit 1; fi; " +
            "if [ -z \"$server_app_name\" ]; then echo \"ERROR: Server App Service workload was not found\"; exit 1; fi && " +
            "if ! server_subnet_id=$(az webapp show -g \"$RG_NAME\" -n \"$server_app_name\" --query virtualNetworkSubnetId -o tsv); then echo \"ERROR: Failed to query App Service VNet integration\"; exit 1; fi; " +
            "if [ \"$server_subnet_id\" != \"$app_service_subnet_id\" ]; then echo \"ERROR: App Service workload is not integrated with the delegated subnet\"; exit 1; fi && " +
            "if ! storage_name=$(az storage account list -g \"$RG_NAME\" --query \"[0].name\" -o tsv); then echo \"ERROR: Failed to query Storage account\"; exit 1; fi; " +
            "if [ -z \"$storage_name\" ]; then echo \"ERROR: Storage account was not found\"; exit 1; fi && " +
            "if ! public_network_access=$(az storage account show -g \"$RG_NAME\" -n \"$storage_name\" --query publicNetworkAccess -o tsv); then echo \"ERROR: Failed to query Storage public network access\"; exit 1; fi; " +
            "if [ \"$public_network_access\" != \"Disabled\" ]; then echo \"ERROR: Storage public network access must be disabled\"; exit 1; fi && " +
            "if ! private_endpoint_count=$(az network private-endpoint list -g \"$RG_NAME\" --query \"length([])\" -o tsv); then echo \"ERROR: Failed to query private endpoints\"; exit 1; fi; " +
            "if [ \"$private_endpoint_count\" != \"1\" ]; then echo \"ERROR: Expected exactly one private endpoint\"; exit 1; fi && " +
            "if ! private_endpoint_id=$(az network private-endpoint list -g \"$RG_NAME\" --query \"[0].id\" -o tsv); then echo \"ERROR: Failed to query private endpoint ID\"; exit 1; fi; " +
            "if ! private_endpoint_nic_id=$(az network private-endpoint show --ids \"$private_endpoint_id\" --query \"networkInterfaces[0].id\" -o tsv); then echo \"ERROR: Failed to query private endpoint NIC\"; exit 1; fi; " +
            "if ! private_endpoint_ip=$(az network nic show --ids \"$private_endpoint_nic_id\" --query \"ipConfigurations[0].privateIPAddress\" -o tsv); then echo \"ERROR: Failed to query private endpoint IP\"; exit 1; fi; " +
            "if [ -z \"$private_endpoint_ip\" ]; then echo \"ERROR: Private endpoint IP was not found\"; exit 1; fi && " +
            "if ! az network private-dns zone show -g \"$RG_NAME\" -n privatelink.blob.core.windows.net &>/dev/null; then echo \"ERROR: Blob private DNS zone was not found\"; exit 1; fi && " +
            "if ! dns_record_ip=$(az network private-dns record-set a show -g \"$RG_NAME\" -z privatelink.blob.core.windows.net -n \"$storage_name\" --query \"aRecords[0].ipv4Address\" -o tsv); then echo \"ERROR: Failed to query Blob private DNS record\"; exit 1; fi; " +
            "if [ \"$dns_record_ip\" != \"$private_endpoint_ip\" ]; then echo \"ERROR: Blob private DNS record does not point to the private endpoint\"; exit 1; fi && " +
            "echo \"Verified App Service VNet integration, Blob private endpoint, and private DNS infrastructure\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));
    }

    private static async Task VerifyBlobConnectivityAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string resourceGroupName)
    {
        await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\" && " +
            "if ! server_app_name=$(az webapp list -g \"$RG_NAME\" --query \"[?contains(name, 'server')].name | [0]\" -o tsv); then echo \"ERROR: Failed to query App Service workload\"; exit 1; fi; " +
            "if [ -z \"$server_app_name\" ]; then echo \"ERROR: Server App Service workload was not found\"; exit 1; fi && " +
            "if ! server_host_name=$(az webapp show -g \"$RG_NAME\" -n \"$server_app_name\" --query defaultHostName -o tsv); then echo \"ERROR: Failed to query App Service hostname\"; exit 1; fi; " +
            "if [ -z \"$server_host_name\" ]; then echo \"ERROR: App Service hostname was not found\"; exit 1; fi && " +
            "if ! private_endpoint_id=$(az network private-endpoint list -g \"$RG_NAME\" --query \"[0].id\" -o tsv); then echo \"ERROR: Failed to query private endpoint ID\"; exit 1; fi; " +
            "if ! private_endpoint_nic_id=$(az network private-endpoint show --ids \"$private_endpoint_id\" --query \"networkInterfaces[0].id\" -o tsv); then echo \"ERROR: Failed to query private endpoint NIC\"; exit 1; fi; " +
            "if ! private_endpoint_ip=$(az network nic show --ids \"$private_endpoint_nic_id\" --query \"ipConfigurations[0].privateIPAddress\" -o tsv); then echo \"ERROR: Failed to query private endpoint IP\"; exit 1; fi; " +
            "probe_succeeded=0 && " +
            "for attempt in $(seq 1 18); do " +
            "probe_result=$(curl -sS \"https://$server_host_name/api/verify-blobs\" --max-time 60 2>&1) || probe_result=\"\"; " +
            "if echo \"$probe_result\" | grep -q '\"status\":\"ok\"' && echo \"$probe_result\" | grep -q '\"contentMatches\":true' && echo \"$probe_result\" | grep -Fq \"$private_endpoint_ip\"; then echo \"Verified Blob access through private endpoint on attempt $attempt\"; probe_succeeded=1; break; fi; " +
            "echo \"Blob probe attempt $attempt did not succeed; retrying in 10 seconds...\"; sleep 10; " +
            "done && " +
            "if [ \"$probe_succeeded\" -ne 1 ]; then echo \"ERROR: Blob probe did not report private endpoint connectivity\"; exit 1; fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(8));
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
