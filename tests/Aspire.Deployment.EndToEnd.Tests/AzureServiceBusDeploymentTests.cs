// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Azure Service Bus resources via Aspire.
/// Tests the Aspire.Hosting.Azure.ServiceBus integration package.
/// </summary>
public sealed class AzureServiceBusDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 30 minutes for Azure resource provisioning.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task DeployAzureServiceBusResource()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployAzureServiceBusResourceCore(cancellationToken);
    }

    private async Task DeployAzureServiceBusResourceCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("servicebus");

        output.WriteLine($"Test: {nameof(DeployAzureServiceBusResource)}");
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

            // Step 2: Set up CLI environment (in CI)
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                output.WriteLine("Step 2: Using pre-installed Aspire CLI from local build...");
                await auto.SourceAspireCliEnvironmentAsync(counter);
            }

            // Step 3: Create single-file AppHost using aspire init
            output.WriteLine("Step 3: Creating single-file AppHost with aspire init...");
            await auto.AspireInitAsync(counter);

            // Step 4a: Add Aspire.Hosting.Azure.ContainerApps package (for managed identity support)
            // This command triggers TWO prompts in sequence:
            // 1. Integration selection prompt (because "ContainerApps" matches multiple Azure packages)
            // 2. Version selection prompt (in CI, to select package version)
            output.WriteLine("Step 4a: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.ContainerApps");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                // First, handle integration selection prompt
                await auto.WaitUntilTextAsync("Select an integration to add:", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();  // Select first integration (azure-appcontainers)
                // Then, handle version selection prompt
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();  // Select first version (PR build)
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 4b: Add Aspire.Hosting.Azure.ServiceBus package
            // This command may only show version selection prompt (unique match)
            output.WriteLine("Step 4b: Adding Azure Service Bus hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.ServiceBus");
            await auto.EnterAsync();

            // In CI, aspire add shows version selection prompt
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync(); // Select first version
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 5: Modify apphost.cs to add Azure Service Bus resource
            {
                var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");

                output.WriteLine($"Looking for apphost.cs at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);

                var buildRunPattern = "builder.Build().Run();";
                var replacement = """
// Add Azure Container App Environment for managed identity support
_ = builder.AddAzureContainerAppEnvironment("env");

// Add Azure Service Bus resource for deployment testing
builder.AddAzureServiceBus("messaging");

builder.Build().Run();
""";

                content = content.Replace(buildRunPattern, replacement);
                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified apphost.cs to add Azure Service Bus resource");
            }

            // Step 6: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 7: Deploy to Azure using aspire deploy
            output.WriteLine("Step 7: Starting Azure deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(20));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 8: Verify the Azure Service Bus namespace was created
            output.WriteLine("Step 8: Verifying Azure Service Bus namespace...");
            await auto.TypeAsync($"az servicebus namespace list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 9: Clean up Azure resources using aspire destroy
            output.WriteLine("Step 9: Destroying Azure deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 10: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployAzureServiceBusResource),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Test failed: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployAzureServiceBusResource),
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
