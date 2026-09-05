// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// L1 infrastructure verification test for Azure API Management.
/// Deploys a Consumption service and representative child resources, then verifies them through Azure CLI.
/// </summary>
public sealed class ApiManagementInfraDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task DeployApiManagementInfrastructure()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployApiManagementInfrastructureCore(linkedCts.Token);
    }

    private async Task DeployApiManagementInfrastructureCore(CancellationToken cancellationToken)
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

        var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("apim-l1");
        var startTime = DateTime.UtcNow;

        output.WriteLine($"Test: {nameof(DeployApiManagementInfrastructure)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            output.WriteLine("Step 3: Creating single-file AppHost...");
            await auto.AspireInitAsync(counter);

            output.WriteLine("Step 4: Adding Azure API Management hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.ApiManagement");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);
            content = content.Replace(
                "builder.Build().Run();",
                """
#pragma warning disable ASPIREAPIM001

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    PublisherName = "Aspire infrastructure tests",
    Sku = Aspire.Hosting.Azure.AzureApiManagementSku.Consumption,
});

apim.AddNamedValue("environment", "infra-test");

var catalogApi = apim.AddApi(
    "catalog-api",
    path: "catalog",
    displayName: "Catalog API");
catalogApi.AddOperation(
    "get-items",
    method: "GET",
    urlTemplate: "/items",
    displayName: "Get items");

apim.AddProduct("catalog-product", "Catalog")
    .WithApi(catalogApi)
    .AddSubscription("catalog-client", "Catalog client");

#pragma warning restore ASPIREAPIM001

builder.Build().Run();
""");
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified apphost.cs with API Management infrastructure:\n{content}");

            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 6: Deploying API Management infrastructure...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(20));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 7: Verifying API Management infrastructure...");
            await auto.TypeAsync(
                $"SERVICE=$(az apim list -g \"{resourceGroupName}\" --subscription \"{subscriptionId}\" " +
                "--query \"[?sku.name == 'Consumption' && provisioningState == 'Succeeded'].name | [0]\" -o tsv) && " +
                "[ -n \"$SERVICE\" ] && " +
                $"[ \"$(az apim api show -g \"{resourceGroupName}\" -n \"$SERVICE\" --subscription \"{subscriptionId}\" " +
                "--api-id catalog-api --query path -o tsv)\" = \"catalog\" ] && " +
                $"[ \"$(az apim api operation show -g \"{resourceGroupName}\" -n \"$SERVICE\" --subscription \"{subscriptionId}\" " +
                "--api-id catalog-api --operation-id get-items --query method -o tsv)\" = \"GET\" ] && " +
                $"[ \"$(az apim nv show -g \"{resourceGroupName}\" -n \"$SERVICE\" --subscription \"{subscriptionId}\" " +
                "--named-value-id environment --query value -o tsv)\" = \"infra-test\" ] && " +
                $"[ \"$(az apim product show -g \"{resourceGroupName}\" -n \"$SERVICE\" --subscription \"{subscriptionId}\" " +
                "--product-id catalog-product --query state -o tsv)\" = \"published\" ] && " +
                $"az apim product api check -g \"{resourceGroupName}\" -n \"$SERVICE\" --subscription \"{subscriptionId}\" " +
                "--product-id catalog-product --api-id catalog-api --output none && " +
                $"SERVICE_ID=$(az apim show -g \"{resourceGroupName}\" -n \"$SERVICE\" --subscription \"{subscriptionId}\" --query id -o tsv) && " +
                "SUBSCRIPTION_SCOPE=$(az rest --method get " +
                "--url \"https://management.azure.com${SERVICE_ID}/subscriptions/catalog-client?api-version=2024-05-01\" " +
                "--query properties.scope -o tsv) && " +
                "[[ \"$SUBSCRIPTION_SCOPE\" == */products/catalog-product ]]");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployApiManagementInfrastructure),
                resourceGroupName,
                new Dictionary<string, string>(),
                DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployApiManagementInfrastructure),
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
