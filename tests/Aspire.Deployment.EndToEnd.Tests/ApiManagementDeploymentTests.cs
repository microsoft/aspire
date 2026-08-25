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
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

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
            content = "using Aspire.Hosting.Azure;\n" + content;
            content = content.Replace(
                "builder.Build().Run();",
                """
apiService.WithExternalHttpEndpoints();

builder.AddAzureContainerAppEnvironment("aca");

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    Sku = AzureApiManagementSku.StandardV2,
});

apim.AddApi(
    "weather-api",
    apiService,
    path: "api",
    subscriptionRequired: false);

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

            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(35));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync(
                $"GATEWAY=$(az apim list -g \"{resourceGroupName}\" --query \"[0].gatewayUrl\" -o tsv) && " +
                "[ -n \"$GATEWAY\" ] && " +
                "OK=0; for i in $(seq 1 24); do " +
                "STATUS=$(curl -s -o /tmp/apim-response.json -w \"%{http_code}\" \"$GATEWAY/api/weatherforecast\" --max-time 30); " +
                "if [ \"$STATUS\" = \"200\" ]; then cat /tmp/apim-response.json; OK=1; break; fi; " +
                "echo \"Attempt $i returned $STATUS; retrying in 10s\"; sleep 10; " +
                "done; [ \"$OK\" = \"1\" ]");
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
            TriggerCleanupResourceGroup(resourceGroupName);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
    }

    private static void TriggerCleanupResourceGroup(string resourceGroupName)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
    }
}
