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

        using var workspace = TemporaryWorkspace.Create(output);
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
            await auto.RunCommandAsync(
                BuildNetworkInfrastructureVerificationCommand(resourceGroupName),
                counter,
                TimeSpan.FromMinutes(4));

            output.WriteLine("Step 13: Verifying Blob access and private DNS from App Service...");
            await auto.RunCommandAsync(
                BuildBlobConnectivityVerificationCommand(resourceGroupName),
                counter,
                TimeSpan.FromMinutes(8));

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
            await TriggerCleanupResourceGroupAsync(resourceGroupName, output);
        }
    }

    private static string BuildNetworkInfrastructureVerificationCommand(string resourceGroupName)
    {
        return BuildBashCommand($$"""
            set -euo pipefail

            fail() {
                echo "ERROR: $1"
                exit 1
            }

            require_value() {
                [ -n "$1" ] || fail "$2"
            }

            require_equal() {
                [ "$1" = "$2" ] || fail "$3"
            }

            resource_group={{AspireCliShellCommandHelpers.QuoteBashArg(resourceGroupName)}}

            vnet_name=$(az network vnet list -g "$resource_group" --query "[?subnets[?name == 'app-service-subnet']].name | [0]" -o tsv) ||
                fail "Failed to query virtual network"
            require_value "$vnet_name" "Virtual network was not found"

            app_service_subnet_id=$(az network vnet subnet show -g "$resource_group" --vnet-name "$vnet_name" --name app-service-subnet --query id -o tsv) ||
                fail "Failed to query App Service subnet"
            private_endpoint_subnet_id=$(az network vnet subnet show -g "$resource_group" --vnet-name "$vnet_name" --name private-endpoint-subnet --query id -o tsv) ||
                fail "Failed to query private endpoint subnet"
            require_value "$app_service_subnet_id" "App Service subnet was not found"
            require_value "$private_endpoint_subnet_id" "Private endpoint subnet was not found"
            [ "$app_service_subnet_id" != "$private_endpoint_subnet_id" ] ||
                fail "Expected distinct App Service and private endpoint subnets"

            delegation=$(az network vnet subnet show --ids "$app_service_subnet_id" --query "delegations[?serviceName == 'Microsoft.Web/serverFarms'].serviceName | [0]" -o tsv) ||
                fail "Failed to query App Service subnet delegation"
            require_equal "$delegation" "Microsoft.Web/serverFarms" "App Service subnet is not delegated to Microsoft.Web/serverFarms"

            private_endpoint_delegation=$(az network vnet subnet show --ids "$private_endpoint_subnet_id" --query "delegations[0].serviceName" -o tsv) ||
                fail "Failed to query private endpoint subnet delegation"
            [ -z "$private_endpoint_delegation" ] ||
                fail "Private endpoint subnet must not be delegated"

            server_app_name=$(az webapp list -g "$resource_group" --query "[?contains(name, 'server')].name | [0]" -o tsv) ||
                fail "Failed to query App Service workload"
            require_value "$server_app_name" "Server App Service workload was not found"
            server_subnet_id=$(az webapp show -g "$resource_group" -n "$server_app_name" --query virtualNetworkSubnetId -o tsv) ||
                fail "Failed to query App Service VNet integration"
            require_equal "$server_subnet_id" "$app_service_subnet_id" "App Service workload is not integrated with the delegated subnet"

            storage_name=$(az storage account list -g "$resource_group" --query "[0].name" -o tsv) ||
                fail "Failed to query Storage account"
            require_value "$storage_name" "Storage account was not found"
            public_network_access=$(az storage account show -g "$resource_group" -n "$storage_name" --query publicNetworkAccess -o tsv) ||
                fail "Failed to query Storage public network access"
            require_equal "$public_network_access" "Disabled" "Storage public network access must be disabled"

            private_endpoint_count=$(az network private-endpoint list -g "$resource_group" --query "length([])" -o tsv) ||
                fail "Failed to query private endpoints"
            require_equal "$private_endpoint_count" "1" "Expected exactly one private endpoint"
            private_endpoint_id=$(az network private-endpoint list -g "$resource_group" --query "[0].id" -o tsv) ||
                fail "Failed to query private endpoint ID"
            private_endpoint_nic_id=$(az network private-endpoint show --ids "$private_endpoint_id" --query "networkInterfaces[0].id" -o tsv) ||
                fail "Failed to query private endpoint NIC"
            private_endpoint_ip=$(az network nic show --ids "$private_endpoint_nic_id" --query "ipConfigurations[0].privateIPAddress" -o tsv) ||
                fail "Failed to query private endpoint IP"
            require_value "$private_endpoint_ip" "Private endpoint IP was not found"

            az network private-dns zone show -g "$resource_group" -n privatelink.blob.core.windows.net >/dev/null ||
                fail "Blob private DNS zone was not found"
            dns_record_ip=$(az network private-dns record-set a show -g "$resource_group" -z privatelink.blob.core.windows.net -n "$storage_name" --query "aRecords[0].ipv4Address" -o tsv) ||
                fail "Failed to query Blob private DNS record"
            require_equal "$dns_record_ip" "$private_endpoint_ip" "Blob private DNS record does not point to the private endpoint"

            echo "Verified App Service VNet integration, Blob private endpoint, and private DNS infrastructure"
            """);
    }

    private static string BuildBlobConnectivityVerificationCommand(string resourceGroupName)
    {
        return BuildBashCommand($$"""
            set -euo pipefail

            fail() {
                echo "ERROR: $1"
                exit 1
            }

            require_value() {
                [ -n "$1" ] || fail "$2"
            }

            resource_group={{AspireCliShellCommandHelpers.QuoteBashArg(resourceGroupName)}}

            server_app_name=$(az webapp list -g "$resource_group" --query "[?contains(name, 'server')].name | [0]" -o tsv) ||
                fail "Failed to query App Service workload"
            require_value "$server_app_name" "Server App Service workload was not found"
            server_host_name=$(az webapp show -g "$resource_group" -n "$server_app_name" --query defaultHostName -o tsv) ||
                fail "Failed to query App Service hostname"
            require_value "$server_host_name" "App Service hostname was not found"

            private_endpoint_id=$(az network private-endpoint list -g "$resource_group" --query "[0].id" -o tsv) ||
                fail "Failed to query private endpoint ID"
            private_endpoint_nic_id=$(az network private-endpoint show --ids "$private_endpoint_id" --query "networkInterfaces[0].id" -o tsv) ||
                fail "Failed to query private endpoint NIC"
            private_endpoint_ip=$(az network nic show --ids "$private_endpoint_nic_id" --query "ipConfigurations[0].privateIPAddress" -o tsv) ||
                fail "Failed to query private endpoint IP"

            for attempt in $(seq 1 18)
            do
                probe_result=$(curl -sS "https://$server_host_name/api/verify-blobs" --max-time 10 2>&1) || probe_result=""
                if echo "$probe_result" | grep -q '"status":"ok"' &&
                   echo "$probe_result" | grep -q '"contentMatches":true' &&
                   echo "$probe_result" | grep -Fq "$private_endpoint_ip"
                then
                    echo "Verified Blob access through private endpoint on attempt $attempt"
                    exit 0
                fi

                echo "Blob probe attempt $attempt did not succeed; retrying in 10 seconds..."
                sleep 10
            done

            fail "Blob probe did not report private endpoint connectivity"
            """);
    }

    private static string BuildBashCommand(string script)
    {
        return $"bash -c {AspireCliShellCommandHelpers.QuoteBashArg(script)}";
    }

    private static async Task TriggerCleanupResourceGroupAsync(string resourceGroupName, ITestOutputHelper output)
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

        if (!process.Start())
        {
            const string message = "Azure CLI did not start the resource group deletion request.";
            output.WriteLine(message);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, message);
            return;
        }

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), standardOutputTask, standardErrorTask);

        if (process.ExitCode == 0)
        {
            output.WriteLine($"Resource group deletion initiated: {resourceGroupName}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Deletion initiated");
            return;
        }

        var standardError = await standardErrorTask;
        var standardOutput = await standardOutputTask;
        var error = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        output.WriteLine($"Resource group deletion may have failed (exit code {process.ExitCode}): {error}");
        DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, $"Exit code {process.ExitCode}: {error}");
    }
}
