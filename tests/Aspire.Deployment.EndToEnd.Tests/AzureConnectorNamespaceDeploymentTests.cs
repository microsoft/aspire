// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Azure Connector Namespace resources via Aspire.
/// </summary>
public sealed class AzureConnectorNamespaceDeploymentTests(ITestOutputHelper output)
{
    private const string EnableEnvironmentVariable = "ASPIRE_DEPLOYMENT_TEST_ENABLE_CONNECTOR_NAMESPACE";
    private const string LocationEnvironmentVariable = "ASPIRE_DEPLOYMENT_TEST_CONNECTOR_NAMESPACE_LOCATION";
    private const string PrincipalObjectIdEnvironmentVariable = "ASPIRE_DEPLOYMENT_TEST_CONNECTOR_NAMESPACE_PRINCIPAL_OBJECT_ID";
    private const string TenantIdEnvironmentVariable = "ASPIRE_DEPLOYMENT_TEST_CONNECTOR_NAMESPACE_TENANT_ID";
    private const string ApiVersion = "2026-05-01-preview";

    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployAzureConnectorNamespaceResourceGraph()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployAzureConnectorNamespaceResourceGraphCore(linkedCts.Token);
    }

    private async Task DeployAzureConnectorNamespaceResourceGraphCore(CancellationToken cancellationToken)
    {
        var enabled = string.Equals(
            Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (DeploymentE2ETestHelpers.IsRunningInCI && !enabled)
        {
            Assert.Skip(
                $"Azure Connector Namespace deployment tests require preview enrollment and are disabled for this deployment environment. " +
                $"Set {EnableEnvironmentVariable}=true only after the environment has Connector Namespace preview access.");
        }

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

            Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
        }

        var location = GetRequiredConfiguration(LocationEnvironmentVariable, enabled);
        var principalObjectId = GetRequiredGuidConfiguration(PrincipalObjectIdEnvironmentVariable, enabled);
        var tenantId = GetRequiredGuidConfiguration(TenantIdEnvironmentVariable, enabled);

        using var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("connectors");

        output.WriteLine($"Test: {nameof(DeployAzureConnectorNamespaceResourceGraph)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Location: {location}");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            output.WriteLine("Step 3: Creating single-file AppHost with aspire init...");
            await auto.AspireInitAsync(counter);

            output.WriteLine("Step 4: Adding Azure Connector Namespace hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.ConnectorNamespace");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            output.WriteLine("Step 5: Defining the Connector Namespace resource graph...");
            WriteAppHost(workspace, principalObjectId, tenantId);

            await auto.RunCommandAsync(
                $"unset ASPIRE_PLAYGROUND Azure__Location && " +
                $"export AZURE__SUBSCRIPTIONID={subscriptionId} && " +
                $"export AZURE__LOCATION={location} && " +
                $"export AZURE__RESOURCEGROUP={resourceGroupName} && " +
                $"export AZURE__TENANTID={tenantId:D}",
                counter);

            output.WriteLine("Step 6: Deploying the Connector Namespace resources...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 7: Verifying the deployed provider resource graph...");
            await auto.RunCommandAsync(
                GetResourceVerificationCommand(
                    subscriptionId,
                    resourceGroupName,
                    principalObjectId,
                    tenantId),
                counter,
                TimeSpan.FromMinutes(2));

            output.WriteLine("Step 8: Destroying the Azure deployment...");
            await auto.AspireDestroyAsync(counter, TimeSpan.FromMinutes(10));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployAzureConnectorNamespaceResourceGraph),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployAzureConnectorNamespaceResourceGraph),
                resourceGroupName,
                ex.Message);

            throw;
        }
        finally
        {
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            var (cleanupSucceeded, cleanupMessage) = await CleanupResourceGroupAsync(resourceGroupName, subscriptionId);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, cleanupSucceeded, cleanupMessage);
        }
    }

    private static string GetRequiredConfiguration(string environmentVariable, bool enabled)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (DeploymentE2ETestHelpers.IsRunningInCI && enabled)
        {
            Assert.Fail(
                $"Azure Connector Namespace deployment testing is enabled, but {environmentVariable} is not configured.");
        }

        Assert.Skip(
            $"Azure Connector Namespace deployment testing requires {environmentVariable}. " +
            $"Set {EnableEnvironmentVariable}=true and configure all Connector Namespace test settings.");
        return string.Empty;
    }

    private static Guid GetRequiredGuidConfiguration(string environmentVariable, bool enabled)
    {
        var value = GetRequiredConfiguration(environmentVariable, enabled);
        if (Guid.TryParse(value, out var result))
        {
            return result;
        }

        if (DeploymentE2ETestHelpers.IsRunningInCI && enabled)
        {
            Assert.Fail($"{environmentVariable} must be a valid GUID.");
        }

        Assert.Skip($"{environmentVariable} must be a valid GUID.");
        return Guid.Empty;
    }

    private static void WriteAppHost(TemporaryWorkspace workspace, Guid principalObjectId, Guid tenantId)
    {
        var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var content = File.ReadAllText(appHostFilePath);

        content = content.Replace(
            "builder.Build().Run();",
            $$"""
            var connectorNamespace = builder.AddAzureConnectorNamespace("connectors");

            var connection = connectorNamespace.AddConnection(
                "outlook",
                "office365",
                new AzureConnectorNamespaceConnectionOptions
                {
                    ConnectionName = "office365-outlook",
                    DisplayName = "Office 365 Outlook"
                });

            connection.WithAccessPolicy(
                "connection-caller",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "{{principalObjectId:D}}",
                    TenantId = "{{tenantId:D}}"
                });

            connectorNamespace.AddMcpServerConfig("outlook-mcp")
                .WithConnector(
                    "office365",
                    connection,
                    new AzureConnectorNamespaceMcpConnectorOptions
                    {
                        Operations =
                        [
                            new AzureConnectorNamespaceMcpOperationOptions
                            {
                                Name = "GetEmailsV3",
                                DisplayName = "Get emails"
                            }
                        ]
                    })
                .WithAccessPolicy(
                    "mcp-caller",
                    new AzureConnectorNamespaceMcpAccessPolicyOptions
                    {
                        ObjectId = "{{principalObjectId:D}}",
                        TenantId = "{{tenantId:D}}",
                        PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
                    });

            builder.Build().Run();
            """);

        File.WriteAllText(appHostFilePath, content);
    }

    private static string GetResourceVerificationCommand(
        string subscriptionId,
        string resourceGroupName,
        Guid principalObjectId,
        Guid tenantId)
    {
        var gatewayResourceType = "Microsoft.Web/connectorGateways";
        var resourceGroupPath =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Web/connectorGateways";

        // The provider exposes connections, managed MCP configurations, and both policy kinds as
        // nested ARM resources. Query each resource directly so the test validates the live provider
        // representation rather than only the generated Bicep document.
        return
            $"GATEWAY_NAME=$(az resource list --subscription {subscriptionId} --resource-group {resourceGroupName} " +
            $"--resource-type {gatewayResourceType} --query '[0].name' -o tsv) && " +
            "test -n \"$GATEWAY_NAME\" && " +
            $"BASE_URL=\"https://management.azure.com{resourceGroupPath}/$GATEWAY_NAME\" && " +
            $"az rest --method get --url \"$BASE_URL/connections/office365-outlook?api-version={ApiVersion}\" " +
            "--query properties.connectorName -o tsv | grep '^office365$' && " +
            $"az rest --method get --url \"$BASE_URL/connections/office365-outlook/accessPolicies/connection-caller?api-version={ApiVersion}\" " +
            $"--query properties.principal.identity.objectId -o tsv | grep -i '^{principalObjectId:D}$' && " +
            $"az rest --method get --url \"$BASE_URL/mcpserverConfigs/outlook-mcp?api-version={ApiVersion}\" " +
            "--query properties.connectors[0].operations[0].name -o tsv | grep '^GetEmailsV3$' && " +
            $"az rest --method get --url \"$BASE_URL/mcpserverConfigs/outlook-mcp/accessPolicies/{principalObjectId:D}?api-version={ApiVersion}\" " +
            "--query properties.principalType -o tsv | grep '^User$' && " +
            $"az rest --method get --url \"$BASE_URL/mcpserverConfigs/outlook-mcp/accessPolicies/{principalObjectId:D}?api-version={ApiVersion}\" " +
            $"--query properties.principal.identity.tenantId -o tsv | grep -i '^{tenantId:D}$'";
    }

    private async Task<(bool Succeeded, string Message)> CleanupResourceGroupAsync(
        string resourceGroupName,
        string subscriptionId)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = $"group delete --subscription {subscriptionId} --name {resourceGroupName} --yes",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                var terminated = false;
                try
                {
                    process.Kill(entireProcessTree: true);
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(killTimeout.Token);
                    terminated = true;
                }
                catch (Exception ex)
                {
                    output.WriteLine($"Failed to terminate timed-out cleanup process: {ex.Message}");
                }

                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    output.WriteLine($"Failed to drain timed-out cleanup process output: {ex.Message}");
                }

                var timeoutMessage = terminated
                    ? "Resource group deletion timed out after 15 minutes; process tree terminated."
                    : "Resource group deletion timed out after 15 minutes; process tree may still be running.";
                output.WriteLine(timeoutMessage);
                return (false, timeoutMessage);
            }

            _ = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                var message = $"Resource group deleted: {resourceGroupName}";
                output.WriteLine(message);
                return (true, "Deleted");
            }

            var failureMessage = string.IsNullOrWhiteSpace(stderr)
                ? $"Exit code {process.ExitCode}"
                : $"Exit code {process.ExitCode}: {stderr.Trim()}";
            output.WriteLine($"Resource group deletion may have failed ({failureMessage})");
            return (false, failureMessage);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to cleanup resource group: {ex.Message}");
            return (false, ex.Message);
        }
    }
}
