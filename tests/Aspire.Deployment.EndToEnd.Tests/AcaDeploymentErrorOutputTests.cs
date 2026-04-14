// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that verify Azure deployment error output is clean and user-friendly.
/// Uses an invalid Azure location to deliberately induce a deployment failure, then asserts
/// that the error output does not contain verbose HTTP details from RequestFailedException.
/// </summary>
/// <remarks>
/// See https://github.com/microsoft/aspire/issues/12303
/// </remarks>
public sealed class AcaDeploymentErrorOutputTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    /// <summary>
    /// Deploys with an invalid Azure location ('invalidlocation') to induce a provisioning failure,
    /// then verifies the error output is clean without verbose HTTP headers or status details.
    /// </summary>
    [Fact]
    public async Task DeployWithInvalidLocation_ErrorOutputIsClean()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployWithInvalidLocation_ErrorOutputIsCleanCore(linkedCts.Token);
    }

    private async Task DeployWithInvalidLocation_ErrorOutputIsCleanCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("errout");
        var deployOutputFile = Path.Combine(workspace.WorkspaceRoot.FullName, "deploy-output.txt");

        output.WriteLine($"Test: {nameof(DeployWithInvalidLocation_ErrorOutputIsClean)}");
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

            // Step 4: Add Azure Container Apps package
            output.WriteLine("Step 4: Adding Azure Container Apps package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
                await auto.EnterAsync();
            }

            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

            // Step 5: Modify apphost.cs to add Azure Container App Environment
            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);

            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
builder.AddAzureContainerAppEnvironment("infra");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified apphost.cs with Azure Container App Environment");

            // Step 6: Set environment variables with an INVALID Azure location to induce failure.
            // 'invalidlocation' is not a real Azure region, so provisioning will fail with
            // LocationNotAvailableForResourceType or similar error.
            // Note: Unset both Azure__Location and AZURE__LOCATION because the CI workflow
            // sets Azure__Location=westus3 at the job level, and on Linux env vars are
            // case-sensitive. Then set the invalid location with both casings to be safe.
            output.WriteLine("Step 6: Setting invalid Azure location to induce failure...");
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && unset Azure__Location && export AZURE__LOCATION=invalidlocation && export Azure__Location=invalidlocation && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 7: Deploy (expecting failure) and capture output to a file
            output.WriteLine("Step 7: Starting deployment with invalid location (expecting failure)...");
            await auto.TypeAsync($"aspire deploy --clear-cache 2>&1 | tee {deployOutputFile}");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineFailed, timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 8: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            // Step 9: Read captured output and verify error messages are clean
            output.WriteLine("Step 9: Verifying error output is clean...");
            Assert.True(File.Exists(deployOutputFile), $"Deploy output file not found at {deployOutputFile}");

            var deployOutput = File.ReadAllText(deployOutputFile);
            output.WriteLine($"Captured {deployOutput.Length} characters of deploy output");
            output.WriteLine("--- Deploy output (last 2000 chars) ---");
            output.WriteLine(deployOutput.Length > 2000 ? deployOutput[^2000..] : deployOutput);
            output.WriteLine("--- End deploy output ---");

            // Verify the output does NOT contain verbose HTTP details from RequestFailedException
            Assert.DoesNotContain("Headers:", deployOutput);
            Assert.DoesNotContain("Cache-Control:", deployOutput);
            Assert.DoesNotContain("x-ms-failure-cause:", deployOutput);
            Assert.DoesNotContain("x-ms-request-id:", deployOutput);
            Assert.DoesNotContain("Content-Type: application/json", deployOutput);
            Assert.DoesNotContain("Status: 400", deployOutput);
            Assert.DoesNotContain("Status: 404", deployOutput);

            // Verify the pipeline DID fail (sanity check)
            Assert.Contains(ConsoleActivityLoggerStrings.PipelineFailed, deployOutput);

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"✅ Test completed in {duration} - error output is clean");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployWithInvalidLocation_ErrorOutputIsClean),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Cleanup: resource group may not have been created if provisioning failed early,
            // but attempt cleanup just in case.
            output.WriteLine($"Cleaning up resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName);
        }
    }

    private void TriggerCleanupResourceGroup(string resourceGroupName)
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
