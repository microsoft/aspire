// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Azure resources that target explicit Azure scopes.
/// </summary>
public sealed class AzureResourceScopeDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task DeployExistingServiceBusWithResourceGroupAndSubscriptionScope()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployExistingServiceBusWithResourceGroupAndSubscriptionScopeCore(cancellationToken);
    }

    private async Task DeployExistingServiceBusWithResourceGroupAndSubscriptionScopeCore(CancellationToken cancellationToken)
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
        var deploymentResourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("scope-deploy");
        var existingResourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("scope-existing");
        var serviceBusNamespaceName = $"sb{Guid.NewGuid():N}"[..20];
        const string queueName = "scopedqueue";

        output.WriteLine($"Test: {nameof(DeployExistingServiceBusWithResourceGroupAndSubscriptionScope)}");
        output.WriteLine($"Deployment Resource Group: {deploymentResourceGroupName}");
        output.WriteLine($"Existing Resource Group: {existingResourceGroupName}");
        output.WriteLine($"Service Bus Namespace: {serviceBusNamespaceName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
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

            output.WriteLine("Step 4: Creating existing Service Bus namespace in scoped resource group...");
            await auto.TypeAsync(
                $"az group create --name \"{existingResourceGroupName}\" --location westus3 --output none && " +
                $"az servicebus namespace create --resource-group \"{existingResourceGroupName}\" --name \"{serviceBusNamespaceName}\" --location westus3 --sku Standard --disable-local-auth true --output none");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(10));

            output.WriteLine("Step 5: Modifying apphost.cs to use explicit resource group and subscription scope...");
            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
            output.WriteLine($"Looking for apphost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);
            var packageVersion = GetPackageVersionFromAppHostSdkDirective(content);

            content = InsertAfterFileDirectives(content, $$"""
#:package Aspire.Hosting.Azure.AppContainers@{{packageVersion}}
#:package Aspire.Hosting.Azure.ServiceBus@{{packageVersion}}
""");

            content = InsertAfterFileDirectives(content, "using Aspire.Hosting;\n\n");

            var buildRunPattern = "builder.Build().Run();";
            var replacement = $$"""
// Add Azure Container App Environment for managed identity support.
_ = builder.AddAzureContainerAppEnvironment("env");

// Use the resource-group + subscription overload with the same subscription used by the test.
// The separate resource group makes this exercise the explicit scoped deployment path.
var messaging = builder.AddAzureServiceBus("messaging")
    .PublishAsExistingInResourceGroup("{{serviceBusNamespaceName}}", "{{existingResourceGroupName}}", "{{subscriptionId}}");
messaging.AddServiceBusQueue("{{queueName}}");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified apphost.cs with scoped existing Service Bus resource");
            output.WriteLine($"New content:\n{content}");

            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={deploymentResourceGroupName} && export AZURE__SUBSCRIPTIONID={subscriptionId} && export AZURE__TENANTID=$(az account show --query tenantId -o tsv)");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 6: Starting Azure deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(25));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 7: Verifying queue was deployed into scoped Service Bus namespace...");
            await auto.TypeAsync(
                $"az servicebus queue show --resource-group \"{existingResourceGroupName}\" --namespace-name \"{serviceBusNamespaceName}\" --name \"{queueName}\" --query \"{{name:name,status:status}}\" -o table && " +
                $"az servicebus namespace show --resource-group \"{existingResourceGroupName}\" --name \"{serviceBusNamespaceName}\" --query \"{{name:name,resourceGroup:resourceGroup}}\" -o table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 8: Destroying Azure deployment...");
            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployExistingServiceBusWithResourceGroupAndSubscriptionScope),
                deploymentResourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Test failed: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployExistingServiceBusWithResourceGroupAndSubscriptionScope),
                deploymentResourceGroupName,
                ex.Message);

            throw;
        }
        finally
        {
            output.WriteLine($"Cleaning up deployment resource group: {deploymentResourceGroupName}");
            await CleanupResourceGroupAsync(deploymentResourceGroupName);

            output.WriteLine($"Cleaning up existing resource group: {existingResourceGroupName}");
            await CleanupResourceGroupAsync(existingResourceGroupName);
        }
    }

    private static string GetPackageVersionFromAppHostSdkDirective(string appHostContent)
    {
        const string sdkDirectivePrefix = "#:sdk Aspire.AppHost.Sdk@";

        foreach (var line in appHostContent.Split('\n'))
        {
            if (!line.StartsWith(sdkDirectivePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var version = line[sdkDirectivePrefix.Length..].Trim();
            var buildMetadataIndex = version.IndexOf('+', StringComparison.Ordinal);
            return buildMetadataIndex >= 0 ? version[..buildMetadataIndex] : version;
        }

        throw new InvalidOperationException("Expected apphost.cs to contain an Aspire.AppHost.Sdk directive.");
    }

    private static string InsertAfterFileDirectives(string content, string text)
    {
        var insertionIndex = 0;
        while (content.AsSpan(insertionIndex).StartsWith("#:", StringComparison.Ordinal))
        {
            var lineEndIndex = content.IndexOf('\n', insertionIndex);
            if (lineEndIndex < 0)
            {
                insertionIndex = content.Length;
                break;
            }

            insertionIndex = lineEndIndex + 1;
        }

        return content.Insert(insertionIndex, text);
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
