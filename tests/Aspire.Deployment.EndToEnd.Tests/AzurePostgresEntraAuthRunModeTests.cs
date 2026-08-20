// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for Azure Database for PostgreSQL Entra ID authentication under
/// <c>aspire start</c> (run mode), where the principal written into the flexible server's Entra
/// administrator is inferred from the ambient credential.
/// </summary>
/// <remarks>
/// This is the only place in the repository where a real Azure PostgreSQL flexible server is
/// provisioned in run mode — every playground that calls <c>AddAzurePostgresFlexibleServer</c> uses
/// <c>RunAsContainer()</c>, so the Entra administrator path has never been exercised live.
/// <para>
/// It matters because run mode is the only mode where <c>principalName</c> is a real Bicep parameter
/// fed from the access token. Publish mode binds it to the user-assigned managed identity's ARM
/// resource name instead, and <c>BicepProvisioner</c> refuses to infer principal parameters at all.
/// The generated template puts that value directly on the administrator resource:
/// </para>
/// <code>
/// resource postgres_admin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
///   name: principalId
///   properties: {
///     principalName: principalName
///     principalType: principalType
///   }
/// }
/// </code>
/// <para>
/// The test asserts both halves of that contract: that ARM accepts the inferred
/// <c>principalName</c> (control plane), and that a referencing service can then actually
/// authenticate against the server as that principal (data plane). The data-plane half is the
/// interesting one, because the hosting side and the client integration derive the principal name
/// from different claim chains — see the comment on the validation script below.
/// </para>
/// <para>
/// See https://github.com/microsoft/aspire/issues/19487.
/// </para>
/// </remarks>
public sealed class AzurePostgresEntraAuthRunModeTests(ITestOutputHelper output)
{
    // A flexible server takes considerably longer to provision than the storage account used by
    // AzureRoleAssignmentRunModeTests, so this budget is larger than the 30 minutes used there.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    // The service runs locally on the test machine in run mode, so a fixed loopback port lets the
    // test probe it with curl instead of discovering the assigned URL through the CLI.
    private const int ServiceHttpPort = 5199;

    [Fact]
    public async Task EntraAdministratorIsUsableByReferencingServiceInRunMode()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await EntraAdministratorIsUsableByReferencingServiceInRunModeCore(linkedCts.Token);
    }

    private async Task EntraAdministratorIsUsableByReferencingServiceInRunModeCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("pg-entra");
        var tenantId = AzureAuthenticationHelpers.GetTenantId();

        output.WriteLine($"Test: {nameof(EntraAdministratorIsUsableByReferencingServiceInRunMode)}");
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

            output.WriteLine("Step 4: Adding Azure PostgreSQL hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.PostgreSQL");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5: Writing the referencing service project...");
            WriteServiceProject(workspace.WorkspaceRoot.FullName);

            // The client integration resolves from the local package hive that the CLI install
            // seeds, so this exercises the current build rather than a released package.
            await auto.RunCommandAsync("dotnet add svc/svc.csproj package Aspire.Azure.Npgsql --prerelease", counter, TimeSpan.FromMinutes(5));

            output.WriteLine("Step 6: Modifying apphost.cs to add Azure PostgreSQL and the service...");
            WriteAppHost(workspace.WorkspaceRoot.FullName);

            WriteValidationScript(workspace.WorkspaceRoot.FullName);

            output.WriteLine("Step 7: Setting Azure run-mode context...");
            // When Azure:ResourceGroup is supplied explicitly, run mode treats it as an existing
            // group unless Azure:AllowResourceGroupCreation is enabled. This test owns a unique
            // group name, so allow provisioning to create it instead of waiting on a non-existent group.
            var contextCommand = $"unset ASPIRE_PLAYGROUND && export AZURE__SUBSCRIPTIONID={subscriptionId} && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName} && export AZURE__ALLOWRESOURCEGROUPCREATION=true";
            if (!string.IsNullOrEmpty(tenantId))
            {
                contextCommand += $" && export AZURE__TENANTID={tenantId}";
            }
            await auto.RunCommandAsync(contextCommand, counter);

            output.WriteLine("Step 8: Starting AppHost with live Azure provisioning...");
            await auto.RunCommandAsync("aspire start --non-interactive --format Json", counter, TimeSpan.FromMinutes(20));
            appHostStarted = true;

            output.WriteLine("Step 9: Waiting for the Entra administrator deployment to be running...");
            // A flexible server is materially slower to provision than a storage account, and the
            // administrator deployment is sequenced after it, so this wait carries the bulk of the
            // test's runtime. It fails fast rather than hanging because AzureProvisioningController
            // marks the roles resource terminal when ARM rejects the deployment.
            await auto.RunCommandAsync("aspire wait pg-roles --status up --timeout 2100 --non-interactive", counter, TimeSpan.FromMinutes(38));

            output.WriteLine("Step 10: Waiting for the PostgreSQL resource to be running...");
            await auto.RunCommandAsync("aspire wait pg --status up --timeout 600 --non-interactive", counter, TimeSpan.FromMinutes(12));

            output.WriteLine("Step 11: Waiting for the referencing service to be running...");
            await auto.RunCommandAsync("aspire wait svc --status up --timeout 600 --non-interactive", counter, TimeSpan.FromMinutes(12));

            output.WriteLine("Step 12: Probing the service's database check endpoint...");
            // The endpoint deliberately reports failures as a 200 with a diagnostic body, so a
            // data-plane problem surfaces as an assertion message naming the PG role and error
            // rather than as an opaque non-zero curl exit.
            await auto.RunCommandAsync($"curl -sS --max-time 60 http://localhost:{ServiceHttpPort}/dbcheck > dbcheck.json", counter, TimeSpan.FromMinutes(2));
            await auto.RunCommandAsync("cat dbcheck.json", counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 13: Reading back the administrator deployment with az...");
            // In run mode BicepProvisioner names the ARM deployment after the resource itself
            // (publish mode appends a timestamp), so the deployment is literally "pg-roles".
            await auto.RunCommandAsync($"az deployment group show --resource-group {resourceGroupName} --name pg-roles -o json > pg-roles.json", counter, TimeSpan.FromMinutes(2));
            await auto.RunCommandAsync("az account show -o json > az-account.json", counter, TimeSpan.FromMinutes(1));

            var requireServicePrincipal = DeploymentE2ETestHelpers.IsRunningInCI ? "true" : "false";
            await auto.RunCommandAsync($"python3 validate-postgres-entra.py pg-roles.json az-account.json dbcheck.json {requireServicePrincipal}", counter, TimeSpan.FromSeconds(30));

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"PostgreSQL Entra auth run-mode test completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(EntraAdministratorIsUsableByReferencingServiceInRunMode),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Test failed: {ex.Message}");

            // Runs here, before the finally block deletes the group, so the ARM error survives.
            await CaptureAzureFailureDiagnosticsAsync(resourceGroupName);

            DeploymentReporter.ReportDeploymentFailure(
                nameof(EntraAdministratorIsUsableByReferencingServiceInRunMode),
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

            output.WriteLine($"Cleaning up resource group: {resourceGroupName}");
            await CleanupResourceGroupAsync(resourceGroupName);
        }
    }

    /// <summary>
    /// Dumps the ARM failure detail for every deployment in the group before the group is deleted.
    /// </summary>
    /// <remarks>
    /// The CLI surfaces run-mode provisioning failures as a bare "Azure deployment failed" state and
    /// does not log the underlying ARM error, and the <c>finally</c> block deletes the resource group,
    /// so without this the cause of a failure is unrecoverable and needs a whole second Azure run to
    /// diagnose. This runs <c>az</c> directly rather than through the terminal automator because the
    /// terminal may already be wedged at a failed prompt by the time it is needed.
    /// </remarks>
    private async Task CaptureAzureFailureDiagnosticsAsync(string resourceGroupName)
    {
        // statusMessage carries the real subcode (quota, capacity, zone availability) behind the
        // generic "OperationFailed" that the deployment itself reports.
        string[] diagnostics =
        [
            $"deployment group list --resource-group {resourceGroupName} --query \"[].{{name:name,state:properties.provisioningState,error:properties.error}}\" -o json",
            $"deployment operation group list --resource-group {resourceGroupName} --name pg --query \"[?properties.provisioningState=='Failed'].properties.statusMessage\" -o json",
            $"deployment operation group list --resource-group {resourceGroupName} --name pg-roles --query \"[?properties.provisioningState=='Failed'].properties.statusMessage\" -o json",
        ];

        foreach (var arguments in diagnostics)
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "az",
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    }
                };

                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                output.WriteLine($"az {arguments}");
                output.WriteLine(await stdoutTask);

                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    output.WriteLine($"(stderr) {stderr}");
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"Failed to capture diagnostics for 'az {arguments}': {ex.Message}");
            }
        }
    }

    private static void WriteAppHost(string workspaceRoot)
    {
        var appHostFilePath = Path.Combine(workspaceRoot, "apphost.cs");
        var appHostContent = File.ReadAllText(appHostFilePath);

        appHostContent = appHostContent.Replace(
            "builder.Build().Run();",
            $$"""
            // No ClearDefaultRoleAssignments() and no WithPasswordAuthentication(): the server is left
            // on its Entra-only default (activeDirectoryAuth Enabled, passwordAuth Disabled), which is
            // what synthesizes the "pg-roles" deployment carrying the Entra administrator.
            var pg = builder.AddAzurePostgresFlexibleServer("pg");

            // AddDatabase is model-only for a flexible server in run mode — it emits no
            // Microsoft.DBforPostgreSQL/flexibleServers/databases resource — so this points at the
            // built-in "postgres" database that every flexible server is created with, rather than at
            // one that nothing would have provisioned.
            var db = pg.AddDatabase("pgdb", "postgres");

            // A fixed loopback port keeps the probe in the test a plain curl. The project has no
            // launch profile, so the endpoint has to be declared here.
            builder.AddProject("svc", "svc/svc.csproj", (string?)null)
                   .WithHttpEndpoint(port: {{ServiceHttpPort}}, name: "http")
                   .WithReference(db)
                   .WaitFor(db);

            builder.Build().Run();
            """);

        File.WriteAllText(appHostFilePath, appHostContent);
    }

    private static void WriteServiceProject(string workspaceRoot)
    {
        var serviceDirectory = Path.Combine(workspaceRoot, "svc");
        Directory.CreateDirectory(serviceDirectory);

        // Written by hand rather than scaffolded with `dotnet new web` so that no
        // Properties/launchSettings.json is produced; the AppHost declares the endpoint explicitly.
        File.WriteAllText(Path.Combine(serviceDirectory, "svc.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(serviceDirectory, "Program.cs"), """
            using Npgsql;

            var builder = WebApplication.CreateBuilder(args);

            // Resolves the Entra credential and, because the connection string carries no username
            // for an Entra-only server, tries to infer one from the access token.
            builder.AddAzureNpgsqlDataSource("pgdb");

            var app = builder.Build();

            app.MapGet("/dbcheck", async (NpgsqlDataSource dataSource) =>
            {
                // Always answer 200 with a diagnostic body. A failure here is the interesting result,
                // and the E2E needs the reason — the username the client inferred and the server's
                // error — rather than an opaque 500.
                string? username = null;

                try
                {
                    username = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Username;

                    await using var connection = await dataSource.OpenConnectionAsync();
                    await using var command = connection.CreateCommand();

                    // current_user reports the PG role the connection actually authenticated as, which
                    // is the value the Entra administrator was created with.
                    command.CommandText = "select current_user";
                    var currentUser = (string?)await command.ExecuteScalarAsync();

                    return Results.Json(new
                    {
                        ok = true,
                        username,
                        currentUser
                    });
                }
                catch (Exception ex)
                {
                    return Results.Json(new
                    {
                        ok = false,
                        username,
                        error = ex.GetType().Name,
                        message = ex.Message
                    });
                }
            });

            app.Run();
            """);
    }

    private static void WriteValidationScript(string workspaceRoot)
    {
        // The comparison is scripted rather than expressed as a shell one-liner because the terminal
        // automator types commands into an interactive prompt, where nested quoting is fragile.
        File.WriteAllText(Path.Combine(workspaceRoot, "validate-postgres-entra.py"), """
            import json
            import sys
            from pathlib import Path

            deployment = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
            account = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
            dbcheck = json.loads(Path(sys.argv[3]).read_text(encoding="utf-8"))
            require_service_principal = sys.argv[4] == "true"

            properties = deployment["properties"]
            parameters = properties["parameters"]

            # `az account show` reports the kind of the signed-in identity:
            #   { "user": { "name": "<appId or upn>", "type": "servicePrincipal" } }  # az login --service-principal
            #   { "user": { "name": "someone@example.com", "type": "user" } }         # interactive az login
            account_type = account["user"]["type"]

            # In CI the workflow logs in with `az login --service-principal --federated-token`, so a
            # user identity here means the credential has degraded and the test would silently stop
            # covering the app-only scenario it exists for.
            if require_service_principal:
                assert account_type == "servicePrincipal", f"expected an app-only credential, got {account_type!r}"

            principal_name = parameters["principalName"]["value"]
            principal_type = parameters["principalType"]["value"]
            print(f"pg-roles principalName={principal_name!r} principalType={principal_type!r}")

            # Control plane: ARM has to accept the inferred principal name on the administrator resource.
            state = properties["provisioningState"]
            assert state == "Succeeded", f"pg-roles deployment state was {state!r}"

            expected_type = "ServicePrincipal" if account_type == "servicePrincipal" else "User"
            assert principal_type == expected_type, f"principalType was {principal_type!r}, expected {expected_type!r}"

            # An empty principal name is what https://github.com/microsoft/aspire/issues/19487 is about:
            # the administrator resource would be created with a blank Entra role name.
            assert principal_name, "principalName was empty"

            # Data plane: the referencing service has to be able to authenticate as that administrator.
            #
            # This is the half that can legitimately fail, because the two sides derive the principal
            # name from different claim chains and nothing forces them to agree:
            #
            #   hosting (DefaultAzurePrincipalProvider) -> upn, email, app_displayname, oid
            #   client  (ManagedIdentityTokenCredentialHelpers) -> xms_mirid, upn, preferred_username, unique_name
            #
            # For an app-only service principal none of the client's four claims are present, so it
            # cannot infer a username at all and leaves it to Npgsql to fail.
            print(f"dbcheck: {json.dumps(dbcheck)}")
            assert dbcheck["ok"], (
                f"service could not authenticate to PostgreSQL as the Entra administrator "
                f"{principal_name!r}: inferred username={dbcheck.get('username')!r} "
                f"{dbcheck.get('error')}: {dbcheck.get('message')}"
            )

            current_user = dbcheck["currentUser"]
            assert current_user == principal_name, (
                f"connected as PG role {current_user!r} but the Entra administrator was created as "
                f"{principal_name!r}"
            )

            print(f"pg-roles succeeded and the service authenticated as {current_user!r}")
            """);
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
