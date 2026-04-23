// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// L1 infrastructure verification test for AKS with VNet and per-pool subnets.
/// Deploys VNet + subnets + AKS cluster with system and worker pools on separate subnets,
/// then verifies infrastructure via az CLI.
/// </summary>
public sealed class AksVnetInfraDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployAksWithVnetInfrastructure()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployAksWithVnetInfrastructureCore(cancellationToken);
    }

    private async Task DeployAksWithVnetInfrastructureCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("aksvnet-l1");

        output.WriteLine($"Test: {nameof(DeployAksWithVnetInfrastructure)}");
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

            // Step 3: Create starter project using aspire new
            // (Using aspire new instead of aspire init to avoid pre-existing
            // AspireInitAsync helper issue with the *.dev.localhost prompt)
            var projectName = "AksVnet";
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5a: Add Aspire.Hosting.Azure.Kubernetes
            output.WriteLine("Step 5a: Adding Azure Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Kubernetes");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 5b: Add Aspire.Hosting.Azure.Network
            output.WriteLine("Step 5b: Adding Azure Network hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Network");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Modify AppHost.cs to add VNet + AKS infrastructure
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
                var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");
                output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);

                var buildRunPattern = "builder.Build().Run();";
                var replacement = """
#pragma warning disable ASPIREAZURE003

// VNet with separate subnets for system and worker node pools
var vnet = builder.AddAzureVirtualNetwork("vnet");
var systemSubnet = vnet.AddSubnet("system-subnet", "10.1.0.0/22");
var workerSubnet = vnet.AddSubnet("worker-subnet", "10.1.4.0/22");

// AKS environment with VNet integration and per-pool subnets
var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithSystemNodePool("Standard_D2as_v5")
    .WithSubnet(systemSubnet);

aks.AddNodePool("workers", "Standard_D2as_v5", 1, 3)
    .WithSubnet(workerSubnet);

#pragma warning restore ASPIREAZURE003

builder.Build().Run();
""";

                content = content.Replace(buildRunPattern, replacement);
                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified apphost.cs with VNet + AKS infrastructure");
                output.WriteLine($"New content:\n{content}");
            }

            // Step 7: Navigate to AppHost project directory
            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy to Azure
            output.WriteLine("Step 9: Starting Azure deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 8: Verify VNet infrastructure
            output.WriteLine("Step 8: Verifying VNet infrastructure...");
            await auto.TypeAsync($"az network vnet list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Verify subnets exist
            await auto.TypeAsync($"VNET_NAME=$(az network vnet list -g \"{resourceGroupName}\" --query \"[0].name\" -o tsv) && " +
                      $"az network vnet subnet list -g \"{resourceGroupName}\" --vnet-name $VNET_NAME --query \"[].{{name:name,addressPrefix:addressPrefix}}\" -o table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 9: Verify AKS cluster and node pools
            output.WriteLine("Step 9: Verifying AKS cluster and node pools...");
            await auto.TypeAsync($"AKS_NAME=$(az aks list -g {resourceGroupName} --query '[0].name' -o tsv) && " +
                      $"echo \"AKS Cluster: $AKS_NAME\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Verify node pools (should have system + workers)
            await auto.TypeAsync($"az aks nodepool list -g {resourceGroupName} --cluster-name $AKS_NAME --query \"[].{{name:name,vmSize:vmSize,mode:mode,count:count,vnetSubnetId:vnetSubnetId}}\" -o table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 10: Clean up Azure resources using aspire destroy
            output.WriteLine("Step 10: Destroying Azure deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 11: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployAksWithVnetInfrastructure),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Test failed: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployAksWithVnetInfrastructure),
                resourceGroupName,
                ex.Message);

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
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync();
                output.WriteLine($"Resource group deletion may have failed (exit code {process.ExitCode}): {error}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to cleanup resource group: {ex.Message}");
        }
    }
}
