// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for routing an Azure Container App through Azure API Management.
/// </summary>
public sealed class ApiManagementDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(90);

    [Fact]
    public async Task DeployStarterTemplateWithApiManagement()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployStarterTemplateWithApiManagementCore(linkedCts.Token);
    }

    private async Task DeployStarterTemplateWithApiManagementCore(CancellationToken cancellationToken)
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

            Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
        }

        const string projectName = "ApiManagementApp";
        var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("apim");
        var deploymentUrls = new Dictionary<string, string>();
        var startTime = DateTime.UtcNow;

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));

            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.ApiManagement");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));

            var appHostFilePath = Path.Combine(
                workspace.WorkspaceRoot.FullName,
                projectName,
                $"{projectName}.AppHost",
                "AppHost.cs");
            var content = File.ReadAllText(appHostFilePath);
            Assert.Contains("builder.Build().Run();", content);
            content = "using Aspire.Hosting.Azure;\nusing Azure.Provisioning.Network;\n" + content;
            content = content.Replace(
                "builder.Build().Run();",
                """
#pragma warning disable ASPIREAZURE003, ASPIREAPIM001

var vnet = builder.AddAzureVirtualNetwork("vnet");
var containerAppsSubnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
var apiManagementSubnet = vnet.AddSubnet("apim-subnet", "10.0.2.0/24")
    .AllowInbound(port: "3443", from: "ApiManagement", protocol: SecurityRuleProtocol.Tcp)
    .AllowInbound(port: "6390", from: AzureServiceTags.AzureLoadBalancer, protocol: SecurityRuleProtocol.Tcp)
    .AllowInbound(port: "443", from: AzureServiceTags.Internet, protocol: SecurityRuleProtocol.Tcp);

var environment = builder.AddAzureContainerAppEnvironment("aca")
    .WithDelegatedSubnet(containerAppsSubnet)
    .WithInternalLoadBalancer(vnet);

apiService
    .WithComputeEnvironment(environment)
    .WithExternalHttpEndpoints();

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    Sku = AzureApiManagementSku.Developer,
}).WithClassicVirtualNetwork(
    apiManagementSubnet,
    AzureApiManagementVirtualNetworkMode.External);

apim.AddApi(
    "weather-api",
    apiService,
    path: "api",
    subscriptionRequired: false);

#pragma warning restore ASPIREAZURE003, ASPIREAPIM001

builder.Build().Run();
""");
            File.WriteAllText(appHostFilePath, content);

            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Fail promptly on AppHost build errors, which occur before the pipeline starts.
            // Classic VNet-injected APIM provisioning can take over an hour.
            await auto.RunCommandAsync("aspire deploy --clear-cache", counter, TimeSpan.FromMinutes(70));

            // DNS resolution alone does not prove public reachability. Verify that the backend's
            // environment has an internal load balancer. Azure API versions expose its ID as
            // either "environmentId" or the older "managedEnvironmentId".
            await auto.TypeAsync(
                $"GATEWAY=$(az apim list -g \"{resourceGroupName}\" --subscription \"{subscriptionId}\" --query \"[0].gatewayUrl\" -o tsv) && " +
                "[ -n \"$GATEWAY\" ] && " +
                $"ENVIRONMENT_ID=$(az containerapp show -g \"{resourceGroupName}\" -n apiservice --subscription \"{subscriptionId}\" " +
                "--query \"properties.environmentId || properties.managedEnvironmentId\" -o tsv) && " +
                "[ -n \"$ENVIRONMENT_ID\" ] && " +
                $"INTERNAL=$(az containerapp env show --ids \"$ENVIRONMENT_ID\" --subscription \"{subscriptionId}\" " +
                "--query properties.vnetConfiguration.internal -o json) && " +
                "[ \"$INTERNAL\" = \"true\" ] && " +
                "{ OK=0; for i in $(seq 1 24); do " +
                "STATUS=$(curl -s -o .aspire-apim-response.json -w \"%{http_code}\" \"$GATEWAY/api/weatherforecast\" --max-time 30); " +
                "if [ \"$STATUS\" = \"200\" ]; then " +
                "if jq -e 'type == \"array\" and length == 5 and all(.[]; has(\"date\") and has(\"temperatureC\") and has(\"summary\"))' .aspire-apim-response.json; then OK=1; fi; break; fi; " +
                "echo \"Attempt $i returned $STATUS; retrying in 10s\"; sleep 10; " +
                "done; [ \"$OK\" = \"1\" ]; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(12));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterTemplateWithApiManagement),
                resourceGroupName,
                deploymentUrls,
                DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterTemplateWithApiManagement),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);
            throw;
        }
        finally
        {
            await TriggerCleanupResourceGroupAsync(resourceGroupName, subscriptionId);
        }
    }

    private static async Task TriggerCleanupResourceGroupAsync(string resourceGroupName, string subscriptionId)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("group");
        process.StartInfo.ArgumentList.Add("delete");
        process.StartInfo.ArgumentList.Add("--name");
        process.StartInfo.ArgumentList.Add(resourceGroupName);
        process.StartInfo.ArgumentList.Add("--subscription");
        process.StartInfo.ArgumentList.Add(subscriptionId);
        process.StartInfo.ArgumentList.Add("--yes");
        process.StartInfo.ArgumentList.Add("--no-wait");

        if (!process.Start())
        {
            const string error = "Azure CLI process did not start.";
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, error);
            throw new InvalidOperationException(error);
        }
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            // A failure before provisioning leaves no group to delete. Azure CLI reports:
            // "ERROR: (ResourceGroupNotFound) Resource group '...' could not be found."
            if (error.Contains("ERROR: (ResourceGroupNotFound)", StringComparison.Ordinal))
            {
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Resource group was already absent");
                return;
            }

            DeploymentReporter.ReportCleanupStatus(
                resourceGroupName,
                success: false,
                $"Azure CLI exited with code {process.ExitCode}: {error}");
            throw new InvalidOperationException(
                $"Failed to request deletion of resource group '{resourceGroupName}': {error}");
        }

        DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Deletion request accepted");
    }
}
