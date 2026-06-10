// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for live Azure resources provisioned by <c>aspire start</c>.
/// </summary>
public sealed class AzureStorageRunModeTests(ITestOutputHelper output)
{
    // Timeout set to 30 minutes for Azure resource provisioning.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task ResourceCommandReturnsLiveAzureResourceInfo()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await ResourceCommandReturnsLiveAzureResourceInfoCore(cancellationToken);
    }

    private async Task ResourceCommandReturnsLiveAzureResourceInfoCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("storage-run");
        var tenantId = AzureAuthenticationHelpers.GetTenantId();

        output.WriteLine($"Test: {nameof(ResourceCommandReturnsLiveAzureResourceInfo)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(cancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var appHostStarted = false;
        var cleanupCommandSucceeded = false;

        try
        {
            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            output.WriteLine("Step 3: Creating single-file AppHost with aspire init...");
            await auto.AspireInitAsync(counter);

            output.WriteLine("Step 4: Adding Azure Storage hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Storage");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5: Modifying apphost.cs to add Azure Storage resource...");
            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
            var appHostContent = File.ReadAllText(appHostFilePath);
            appHostContent = appHostContent.Replace(
                "builder.Build().Run();",
                """
                // This test verifies resource command metadata for a standalone storage account.
                // No app consumes the account, so skip default RBAC to avoid testing role assignment propagation.
                builder.AddAzureStorage("storage")
                    .ClearDefaultRoleAssignments();

                builder.Build().Run();
                """);
            File.WriteAllText(appHostFilePath, appHostContent);

            var validateScriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, "validate-get-resource.py");
            File.WriteAllText(validateScriptPath, """
                import json
                import sys
                from pathlib import Path

                text = Path(sys.argv[1]).read_text(encoding="utf-8")
                payload = None

                # The CLI writes command result JSON to stdout, but local runs can include
                # launch/build preamble text before the payload:
                #   Building...
                #   { "success": true, "command": "get-azure-resource", ... }
                # Parse from the first JSON object so the check works in both CI and local runs.
                json_start = text.find("{")
                if json_start >= 0:
                    payload = json.JSONDecoder().raw_decode(text[json_start:])[0]

                if payload is None:
                    raise AssertionError("get-azure-resource did not emit a JSON payload")

                assert payload["success"] is True, payload
                assert payload["command"] == "get-azure-resource", payload
                assert payload["resourceName"] == "storage", payload

                deployment = payload["deployment"]
                live = payload["live"]
                assert deployment["hasState"] is True, payload
                assert live["checked"] is True, payload
                assert live["exists"] is True, payload

                resource_id = deployment["resourceId"]
                deployment_id = deployment["deploymentId"]
                assert resource_id, payload
                assert deployment_id, payload

                Path(sys.argv[2]).write_text(resource_id, encoding="utf-8")
                Path(sys.argv[3]).write_text(deployment_id, encoding="utf-8")
                """);

            output.WriteLine("Step 6: Setting Azure run-mode context...");
            // When Azure:ResourceGroup is supplied explicitly, run mode treats it as an existing
            // group unless Azure:AllowResourceGroupCreation is enabled. This test owns a unique
            // group name, so allow provisioning to create it instead of waiting on a non-existent group.
            var contextCommand = $"unset ASPIRE_PLAYGROUND && export AZURE__SUBSCRIPTIONID={subscriptionId} && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName} && export AZURE__ALLOWRESOURCEGROUPCREATION=true";
            if (!string.IsNullOrEmpty(tenantId))
            {
                contextCommand += $" && export AZURE__TENANTID={tenantId}";
            }
            await auto.RunCommandAsync(contextCommand, counter);

            output.WriteLine("Step 7: Starting AppHost with live Azure provisioning...");
            await auto.RunCommandAsync("aspire start --non-interactive --format Json", counter, TimeSpan.FromMinutes(20));
            appHostStarted = true;

            output.WriteLine("Step 8: Waiting for Azure Storage resource to be running...");
            // `aspire start` returns after the AppHost is detached. Run-mode Azure provisioning
            // continues inside that AppHost, so wait for the resource state before invoking commands.
            await auto.RunCommandAsync("aspire wait storage --status up --timeout 1500 --non-interactive", counter, TimeSpan.FromMinutes(26));

            output.WriteLine("Step 9: Running get-azure-resource command...");
            await auto.RunCommandAsync("aspire resource storage get-azure-resource --non-interactive > get-resource.json", counter, TimeSpan.FromMinutes(2));
            await auto.RunCommandAsync("python3 validate-get-resource.py get-resource.json storage-resource-id.txt storage-deployment-id.txt", counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 10: Verifying emitted Azure resource IDs with az...");
            await auto.RunCommandAsync("az resource show --ids \"$(cat storage-resource-id.txt)\" --query \"properties.provisioningState\" -o tsv | grep '^Succeeded$'", counter, TimeSpan.FromMinutes(1));
            await auto.RunCommandAsync("az resource show --ids \"$(cat storage-deployment-id.txt)\" --query \"properties.provisioningState\" -o tsv | grep '^Succeeded$'", counter, TimeSpan.FromMinutes(1));

            output.WriteLine("Step 11: Deleting live Azure resources through the visible Azure control resource...");
            await auto.RunCommandAsync("aspire resource azure-environment delete-azure-resources --non-interactive", counter, TimeSpan.FromMinutes(10));
            cleanupCommandSucceeded = true;
            await auto.RunCommandAsync($"az group exists --name {resourceGroupName} -o tsv | grep '^false$'", counter, TimeSpan.FromSeconds(30));

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Run-mode Azure resource command test completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(ResourceCommandReturnsLiveAzureResourceInfo),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Test failed: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(ResourceCommandReturnsLiveAzureResourceInfo),
                resourceGroupName,
                ex.Message);

            throw;
        }
        finally
        {
            if (appHostStarted)
            {
                try
                {
                    output.WriteLine("Stopping AppHost...");
                    await auto.RunCommandAsync("aspire stop --non-interactive 2>/dev/null || true", counter, TimeSpan.FromMinutes(2));
                }
                catch (Exception ex)
                {
                    output.WriteLine($"Failed to stop AppHost: {ex.Message}");
                }
            }

            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch (Exception ex)
            {
                output.WriteLine($"Failed to exit terminal cleanly: {ex.Message}");
            }

            if (!cleanupCommandSucceeded)
            {
                output.WriteLine($"Cleaning up resource group: {resourceGroupName}");
                await CleanupResourceGroupAsync(resourceGroupName);
            }
        }
    }

    private async Task CleanupResourceGroupAsync(string resourceGroupName)
    {
        try
        {
            using var process = new System.Diagnostics.Process
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
