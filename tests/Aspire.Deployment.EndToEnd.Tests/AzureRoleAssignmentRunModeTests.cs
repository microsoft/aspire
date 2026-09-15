// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Azure role assignments created by <c>aspire start</c> (run mode).
/// </summary>
/// <remarks>
/// Run mode is the only execution mode where the <c>principalType</c> of a role assignment is
/// inferred from the ambient credential rather than being statically known. In publish mode the
/// assignment targets a user-assigned managed identity, so <c>AzureResourcePreparer</c> hardcodes
/// <c>ServicePrincipal</c> and <c>BicepProvisioner</c> refuses to infer principal parameters at all.
/// That makes every <c>aspire deploy</c> test in this project blind to the run-mode inference path,
/// which is where https://github.com/microsoft/aspire/issues/13933 regressed.
/// <para>
/// This test exists to keep that path covered under the CI service principal, which is app-only.
/// See https://github.com/microsoft/aspire/issues/19487.
/// </para>
/// </remarks>
public sealed class AzureRoleAssignmentRunModeTests(ITestOutputHelper output)
{
    // Timeout set to 30 minutes for Azure resource provisioning.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task RoleAssignmentsSucceedForAmbientCredentialInRunMode()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await RoleAssignmentsSucceedForAmbientCredentialInRunModeCore(cancellationToken);
    }

    private async Task RoleAssignmentsSucceedForAmbientCredentialInRunModeCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("roles-run");
        var tenantId = AzureAuthenticationHelpers.GetTenantId();

        output.WriteLine($"Test: {nameof(RoleAssignmentsSucceedForAmbientCredentialInRunMode)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(cancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var appHostStarted = false;

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
                // Deliberately no ClearDefaultRoleAssignments() here — that is the entire point of this
                // test. In run mode an Azure resource that no compute resource references still gets its
                // default role assignments applied to the ambient deployment principal, which synthesizes
                // a "storage-roles" resource and a matching ARM deployment. That deployment stamps a
                // principalType inferred from the signed-in credential, so it is the only live coverage
                // of the inference path. See https://github.com/microsoft/aspire/issues/19487.
                builder.AddAzureStorage("storage");

                builder.Build().Run();
                """);
            File.WriteAllText(appHostFilePath, appHostContent);

            // The comparison is scripted rather than expressed as a shell one-liner because the terminal
            // automator types commands into an interactive prompt, where nested quoting is fragile.
            var validateScriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, "validate-role-assignment.py");
            File.WriteAllText(validateScriptPath, """
                import json
                import sys
                from pathlib import Path

                deployment = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
                account = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
                require_service_principal = sys.argv[3] == "true"

                properties = deployment["properties"]
                state = properties["provisioningState"]
                assert state == "Succeeded", f"storage-roles deployment state was {state!r}"

                # `az account show` reports the kind of the signed-in identity:
                #   { "user": { "name": "<appId or upn>", "type": "servicePrincipal" } }  # az login --service-principal
                #   { "user": { "name": "someone@example.com", "type": "user" } }         # interactive az login
                # This is a valid oracle for the AppHost's principal only because the run-mode context
                # pins AZURE__CREDENTIALSOURCE=AzureCli, so both sides read the same `az` identity.
                account_type = account["user"]["type"]

                # In CI the workflow logs in with `az login --service-principal --federated-token`, so a
                # user identity here means the credential has degraded and the test would silently stop
                # covering the app-only scenario it exists for.
                if require_service_principal:
                    assert account_type == "servicePrincipal", f"expected an app-only credential, got {account_type!r}"

                expected = "ServicePrincipal" if account_type == "servicePrincipal" else "User"
                actual = properties["parameters"]["principalType"]["value"]
                assert actual == expected, f"principalType was {actual!r}, expected {expected!r}"

                print(f"storage-roles succeeded with principalType={actual}")
                """);

            output.WriteLine("Step 6: Setting Azure run-mode context...");
            // When Azure:ResourceGroup is supplied explicitly, run mode treats it as an existing
            // group unless Azure:AllowResourceGroupCreation is enabled. This test owns a unique
            // group name, so allow provisioning to create it instead of waiting on a non-existent group.
            // Pin the credential source to AzureCli so the principal the AppHost provisions with is the
            // same one `az account show` reports below. Left at the default, run mode builds a
            // DefaultAzureCredential whose chain tries EnvironmentCredential first, so a developer with
            // AZURE_CLIENT_ID/AZURE_CLIENT_SECRET exported *and* an interactive `az login` would provision
            // as a service principal while the oracle read a user, failing on a correct principalType.
            // CI already resolves to AzureCliCredential (it authenticates with `az login --service-principal`
            // and exports no client secret), so this pins existing behavior rather than changing it.
            var contextCommand = $"unset ASPIRE_PLAYGROUND && export AZURE__SUBSCRIPTIONID={subscriptionId} && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName} && export AZURE__ALLOWRESOURCEGROUPCREATION=true && export AZURE__CREDENTIALSOURCE=AzureCli";
            if (!string.IsNullOrEmpty(tenantId))
            {
                contextCommand += $" && export AZURE__TENANTID={tenantId}";
            }
            await auto.RunCommandAsync(contextCommand, counter);

            output.WriteLine("Step 7: Starting AppHost with live Azure provisioning...");
            // Set before starting, not after: `aspire start` detaches the AppHost before it finishes
            // waiting for startup, so a failure here can still leave a live AppHost provisioning into
            // the resource group that `finally` is about to delete. StopAppHostAsync swallows and logs
            // its own failures, so claiming a session that was never created is harmless.
            appHostStarted = true;
            await auto.RunCommandAsync("aspire start --non-interactive --format Json", counter, TimeSpan.FromMinutes(20));

            output.WriteLine("Step 8: Waiting for the role assignment resource to be running...");
            // `aspire start` returns once the AppHost is detached; run-mode Azure provisioning continues
            // inside it. The roles resource is a first-class resource in the model, so it can be waited on
            // directly — and it fails fast rather than hanging, because AzureProvisioningController marks
            // it terminal when ARM rejects the deployment.
            await auto.RunCommandAsync("aspire wait storage-roles --status up --timeout 1500 --non-interactive", counter, TimeSpan.FromMinutes(26));

            output.WriteLine("Step 9: Waiting for the storage resource to be running...");
            // The target resource only reaches Running after its role assignments provision; on failure it
            // is published as "Failed to Provision Roles", so this catches a roles failure that somehow did
            // not surface on the roles resource itself.
            await auto.RunCommandAsync("aspire wait storage --status up --timeout 1500 --non-interactive", counter, TimeSpan.FromMinutes(26));

            output.WriteLine("Step 10: Verifying the role assignment deployment with az...");
            // In run mode BicepProvisioner names the ARM deployment after the resource itself (publish mode
            // appends a timestamp), so the deployment is literally "storage-roles".
            // Local Azure CLI users can have a different default subscription than the one configured for
            // the AppHost, so scope verification commands to the provisioning subscription explicitly.
            await auto.RunCommandAsync($"az deployment group show --subscription {subscriptionId} --resource-group {resourceGroupName} --name storage-roles -o json > roles-deployment.json", counter, TimeSpan.FromMinutes(2));
            await auto.RunCommandAsync($"az account show --subscription {subscriptionId} -o json > az-account.json", counter, TimeSpan.FromMinutes(1));

            var requireServicePrincipal = DeploymentE2ETestHelpers.IsRunningInCI ? "true" : "false";
            await auto.RunCommandAsync($"python3 validate-role-assignment.py roles-deployment.json az-account.json {requireServicePrincipal}", counter, TimeSpan.FromSeconds(30));

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Run-mode role assignment test completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(RoleAssignmentsSucceedForAmbientCredentialInRunMode),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Test failed: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(RoleAssignmentsSucceedForAmbientCredentialInRunMode),
                resourceGroupName,
                ex.Message);

            throw;
        }
        finally
        {
            if (appHostStarted)
            {
                output.WriteLine("Stopping AppHost...");
                await DeploymentE2ETestHelpers.StopAppHostAsync(workspace.WorkspaceRoot.FullName, output.WriteLine);
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

            output.WriteLine($"Cleaning up resource group: {resourceGroupName}");
            await CleanupResourceGroupAsync(resourceGroupName, subscriptionId);
        }
    }

    private async Task CleanupResourceGroupAsync(string resourceGroupName, string subscriptionId)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "az",
                    // The AppHost provisions into AZURE__SUBSCRIPTIONID, which can differ from the
                    // local Azure CLI default. Scope cleanup explicitly so failed local runs do not
                    // leave billable resources in the configured test subscription.
                    Arguments = $"group delete --subscription {subscriptionId} --name {resourceGroupName} --yes --no-wait",
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
