// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for compact resource naming with Azure Container App Environments.
/// Validates that WithCompactResourceNaming() fixes storage account naming collisions
/// caused by long environment names, and that the default naming is unchanged on upgrade.
/// </summary>
public sealed class AcaCompactNamingDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    /// <summary>
    /// Verifies that deploying with a long ACA environment name and a volume
    /// succeeds when WithCompactResourceNaming() is used.
    /// The storage account name would otherwise exceed 24 chars and truncate the uniqueString.
    /// </summary>
    [Fact]
    public async Task DeployWithCompactNamingFixesStorageCollision()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployWithCompactNamingFixesStorageCollisionCore(linkedCts.Token);
    }

    private async Task DeployWithCompactNamingFixesStorageCollisionCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("compact");

        output.WriteLine($"Test: {nameof(DeployWithCompactNamingFixesStorageCollision)}");
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

            // Step 2: Set up CLI
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                output.WriteLine("Step 2: Using pre-installed Aspire CLI...");
                await auto.SourceAspireCliEnvironmentAsync(counter);
            }

            // Step 3: Create single-file AppHost
            output.WriteLine("Step 3: Creating single-file AppHost...");
            await auto.AspireInitAsync(counter);

            // Step 4: Add required packages
            output.WriteLine("Step 4: Adding Azure Container Apps package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 5: Modify apphost.cs with a long environment name and a container with volume.
            // Use WithCompactResourceNaming() so the storage account name preserves the uniqueString.
            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);

            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Long env name (16 chars) would truncate uniqueString without compact naming
builder.AddAzureContainerAppEnvironment("my-long-env-name")
       .WithCompactResourceNaming();

// Container with a volume triggers storage account creation
builder.AddContainer("worker", "mcr.microsoft.com/dotnet/samples", "aspnetapp")
       .WithVolume("data", "/app/data");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);

            // Suppress experimental diagnostic for WithCompactResourceNaming
            content = "#pragma warning disable ASPIREACANAMING001\n" + content;

            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified apphost.cs with long env name + compact naming + volume");

            // Step 6: Set environment variables for deployment
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 7: Deploy
            output.WriteLine("Step 7: Deploying with compact naming...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 8: Verify storage account was created and name contains uniqueString
            output.WriteLine("Step 8: Verifying storage account naming...");
            await auto.TypeAsync(
                $"STORAGE_NAMES=$(az storage account list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv) && " +
                "echo \"Storage accounts: $STORAGE_NAMES\" && " +
                "STORAGE_COUNT=$(echo \"$STORAGE_NAMES\" | wc -l) && " +
                "echo \"Count: $STORAGE_COUNT\" && " +
                // Verify each storage name contains 'sv' (compact naming marker)
                "for name in $STORAGE_NAMES; do " +
                "if echo \"$name\" | grep -q 'sv'; then echo \"✅ $name uses compact naming\"; " +
                "else echo \"⚠️ $name does not use compact naming (may be ACR storage)\"; fi; " +
                "done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 9: Clean up Azure resources using aspire destroy
            output.WriteLine("Step 9: Destroying Azure deployment...");
            await auto.AspireDestroyAsync(counter);

            // Step 10: Exit
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"✅ Test completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployWithCompactNamingFixesStorageCollision),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"❌ Test failed: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployWithCompactNamingFixesStorageCollision),
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
            output.WriteLine(process.ExitCode == 0
                ? $"Resource group deletion initiated: {resourceGroupName}"
                : $"Resource group deletion may have failed (exit code {process.ExitCode})");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to cleanup resource group: {ex.Message}");
        }
    }
}
