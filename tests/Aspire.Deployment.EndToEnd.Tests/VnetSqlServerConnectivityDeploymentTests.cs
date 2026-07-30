// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// L2+L3 connectivity verification test for Azure SQL Server with VNet and Private Endpoint.
/// Deploys a starter app with VNet + PE + Aspire SQL client, then curls a probe endpoint that opens a
/// real connection to the database over the private endpoint. That proves three things at once: the
/// private endpoint and DNS resolve, the deployment script created a contained user matching the app's
/// managed identity, and that user was granted db_owner.
/// </summary>
public sealed class VnetSqlServerConnectivityDeploymentTests(ITestOutputHelper output)
{
    // The inner step waits (deploy alone allows 30 minutes) sum to more than 40 minutes under CI
    // contention, and the SQL probe below adds another 8, so the outer budget has to exceed their sum.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(50);

    [Fact]
    public async Task DeployStarterTemplateWithSqlServerPrivateEndpoint()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterTemplateWithSqlServerPrivateEndpointCore(cancellationToken);
    }

    private async Task DeployStarterTemplateWithSqlServerPrivateEndpointCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("vnet-sql-l23");
        var projectName = "VnetSqlApp";

        output.WriteLine($"Test: {nameof(DeployStarterTemplateWithSqlServerPrivateEndpoint)}");
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

            // Step 3: Create starter project using aspire new
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5a: Add Aspire.Hosting.Azure.AppContainers
            output.WriteLine("Step 5a: Adding Azure Container Apps hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 5b: Add Aspire.Hosting.Azure.Network
            output.WriteLine("Step 5b: Adding Azure Network hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Network");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 5c: Add Aspire.Hosting.Azure.Sql
            output.WriteLine("Step 5c: Adding Azure SQL hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Sql");
            await auto.EnterAsync();

            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Add Aspire client package to the Web project
            output.WriteLine("Step 6: Adding SQL client package to Web project...");
            await auto.TypeAsync($"dotnet add {projectName}.Web package Aspire.Microsoft.Data.SqlClient --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Step 7: Modify AppHost.cs to add VNet + PE + WithReference
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
                var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

                output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

                var content = File.ReadAllText(appHostFilePath);

                content = content.Replace(
                    "var builder = DistributedApplication.CreateBuilder(args);",
                    """
var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIREAZURE003

// VNet with delegated subnet for ACA and PE subnet
var vnet = builder.AddAzureVirtualNetwork("vnet");
var acaSubnet = vnet.AddSubnet("aca-subnet", "10.0.0.0/23");
var peSubnet = vnet.AddSubnet("pe-subnet", "10.0.2.0/24");

builder.AddAzureContainerAppEnvironment("env")
    .WithDelegatedSubnet(acaSubnet);

// SQL Server with Private Endpoint
var sql = builder.AddAzureSqlServer("sql");
var db = sql.AddDatabase("db");
peSubnet.AddPrivateEndpoint(sql);

#pragma warning restore ASPIREAZURE003
""");

                content = content.Replace(
                    ".WithExternalHttpEndpoints()",
                    ".WithExternalHttpEndpoints()\n    .WithReference(db)");

                File.WriteAllText(appHostFilePath, content);

                output.WriteLine($"Modified AppHost.cs with VNet + SQL Server PE + WithReference");
                output.WriteLine($"New content:\n{content}");
            }

            // Step 8: Modify Web project Program.cs to register the SQL client and expose a probe endpoint
            {
                var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
                var webProgramPath = Path.Combine(projectDir, $"{projectName}.Web", "Program.cs");

                output.WriteLine($"Looking for Web Program.cs at: {webProgramPath}");

                var content = File.ReadAllText(webProgramPath);

                content = content.Replace(
                    "builder.AddServiceDefaults();",
                    """
builder.AddServiceDefaults();
builder.AddSqlServerClient("db");
""");

                // The starter template's home page never touches SQL, so serving it only proves the container
                // started. This probe endpoint opens a real connection instead. Aspire's Azure SQL connection
                // string uses Authentication="Active Directory Default", so merely opening it exercises the
                // Entra token login path - if the provisioning deployment script had not created a contained
                // user whose SID matches this app's managed identity, the login would be rejected outright.
                // IS_ROLEMEMBER then confirms the ALTER ROLE in that same script took effect.
                content = content.Replace(
                    "app.MapDefaultEndpoints();",
                    """
app.MapGet("/sqlcheck", async (HttpContext http) =>
{
    try
    {
        var connection = http.RequestServices.GetRequiredService<Microsoft.Data.SqlClient.SqlConnection>();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT USER_NAME(), IS_ROLEMEMBER('db_owner')";

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return Results.Text("SQLCHECK-FAIL no rows returned", statusCode: 500);
        }

        var userName = reader.GetString(0);
        var isDbOwner = reader.IsDBNull(1) ? "null" : reader.GetInt32(1).ToString();
        return Results.Text($"SQLCHECK-OK user={userName} dbowner={isDbOwner}");
    }
    catch (Exception ex)
    {
        // Returned as the response body (rather than thrown) so the failure reason reaches the CI log.
        // UseExceptionHandler would otherwise replace it with a generic error page outside Development.
        return Results.Text($"SQLCHECK-FAIL {ex.GetType().Name}: {ex.Message}", statusCode: 500);
    }
});

app.MapDefaultEndpoints();
""");

                File.WriteAllText(webProgramPath, content);

                output.WriteLine("Modified Web Program.cs to add SQL client registration and /sqlcheck probe");
            }

            // Step 9: Navigate to AppHost project directory
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

            // Step 12: Verify PE infrastructure
            output.WriteLine("Step 12: Verifying PE infrastructure...");
            await auto.TypeAsync($"az network private-endpoint list -g \"{resourceGroupName}\" --query \"[].{{name:name,state:provisioningState}}\" -o table && " +
                      $"az network private-dns zone list -g \"{resourceGroupName}\" --query \"[].name\" -o tsv");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 13: Verify deployed endpoints with retry
            output.WriteLine("Step 13: Verifying deployed endpoints...");
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
                      "if [ \"$failed\" -ne 0 ]; then echo \"❌ One or more endpoint checks failed\"; exit 1; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 14: Prove the managed identity can actually use the database. This is the assertion that
            // covers the provisioning deployment script end to end - a successful deployment only proves the
            // script exited zero, not that the user it created can log in or that the role grant took effect.
            output.WriteLine("Step 14: Verifying managed identity SQL access...");
            await auto.TypeAsync($"RG_NAME=\"{resourceGroupName}\"; " +
                      "url=$(az containerapp list -g \"$RG_NAME\" --query \"[].properties.configuration.ingress.fqdn\" -o tsv 2>/dev/null | grep -v '\\.internal\\.' | head -1); " +
                      "ok=0; " +
                      "if [ -z \"$url\" ]; then echo \"❌ No external container app endpoint found\"; else " +
                      "echo \"Probing https://$url/sqlcheck...\"; " +
                      // Retries cover the short window after startup where the app's managed identity token
                      // is not yet available and the freshly created SQL principal has not fully propagated.
                      "for i in $(seq 1 12); do " +
                      "body=$(curl -s \"https://$url/sqlcheck\" --max-time 20 2>/dev/null); " +
                      "echo \"  Attempt $i: $body\"; " +
                      "if echo \"$body\" | grep -q \"SQLCHECK-OK\" && echo \"$body\" | grep -q \"dbowner=1\"; then ok=1; break; fi; " +
                      "sleep 10; " +
                      "done; fi; " +
                      "if [ \"$ok\" -eq 1 ]; then echo \"✅ Managed identity connected over the private endpoint and holds db_owner\"; else echo \"❌ Managed identity SQL check failed\"; false; fi");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(8));

            // Step 15: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterTemplateWithSqlServerPrivateEndpoint),
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
                nameof(DeployStarterTemplateWithSqlServerPrivateEndpoint),
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
