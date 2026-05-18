// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying TypeScript AppHosts that use Aspire.Hosting.JavaScript publish APIs.
/// </summary>
public sealed class TypeScriptJavaScriptHostingDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task DeployTypeScriptStaticWebsiteWithNodeApiToAzureContainerApps()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployTypeScriptStaticWebsiteWithNodeApiToAzureContainerAppsCore(cancellationToken);
    }

    private async Task DeployTypeScriptStaticWebsiteWithNodeApiToAzureContainerAppsCore(CancellationToken cancellationToken)
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

        using var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("ts-js-hosting");

        output.WriteLine($"Test: {nameof(DeployTypeScriptStaticWebsiteWithNodeApiToAzureContainerApps)}");
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
            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);

            await auto.RunCommandFailFastAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));

            await AddPackageAsync(auto, counter, "Aspire.Hosting.JavaScript");
            await AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppContainers");

            WriteStaticWebsiteWithNodeApiAppHost(workspace);

            await auto.RunCommandFailFastAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}", counter);

            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.RunCommandFailFastAsync(BuildEndpointVerificationCommand(resourceGroupName), counter, TimeSpan.FromMinutes(10));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployTypeScriptStaticWebsiteWithNodeApiToAzureContainerApps),
                resourceGroupName,
                new Dictionary<string, string>(),
                duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployTypeScriptStaticWebsiteWithNodeApiToAzureContainerApps),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName, output);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, errorMessage: "Cleanup triggered (fire-and-forget)");
        }
    }

    private static async Task AddPackageAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string packageName)
    {
        await auto.TypeAsync($"aspire add {packageName}");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
    }

    private static void WriteStaticWebsiteWithNodeApiAppHost(TemporaryWorkspace workspace)
    {
        var apiDir = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "api"));
        File.WriteAllText(Path.Combine(apiDir.FullName, "package.json"), """
            {
              "name": "api",
              "version": "1.0.0",
              "private": true,
              "scripts": {
                "build": "echo 'no build needed'"
              }
            }
            """);
        File.WriteAllText(Path.Combine(apiDir.FullName, "server.js"), """
            const http = require('http');
            const port = process.env.PORT || 3000;

            http.createServer((req, res) => {
              res.writeHead(200, { 'Content-Type': 'application/json' });
              res.end(JSON.stringify([{ temperatureC: 22, summary: 'Warm' }]));
            }).listen(port, '0.0.0.0');
            """);

        var staticSiteDir = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "staticsite"));
        File.WriteAllText(Path.Combine(staticSiteDir.FullName, "package.json"), """
            {
              "name": "staticsite",
              "version": "1.0.0",
              "private": true,
              "scripts": {
                "build": "mkdir -p dist && cp index.html dist/index.html"
              }
            }
            """);
        File.WriteAllText(Path.Combine(staticSiteDir.FullName, "index.html"), """
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="UTF-8"><title>Static Site</title></head>
            <body><h1>Weather</h1></body>
            </html>
            """);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            // This environment selects Azure Container Apps as the deployment target for these app resources.
            await builder.addAzureContainerAppEnvironment('env');

            const api = await builder.addNodeApp('api', './api', 'server.js')
                .withHttpEndpoint({ name: 'http', env: 'PORT' });

            await builder.addJavaScriptApp('staticsite', './staticsite')
                .withHttpEndpoint({ name: 'http', targetPort: 5000 })
                .publishAsStaticWebsite({ apiPath: '/api', apiTarget: api })
                .withExternalHttpEndpoints();

            await builder.build().run();
            """);
    }

    private static string BuildEndpointVerificationCommand(string resourceGroupName)
    {
        return
            $"static_host=$(az containerapp list -g \"{resourceGroupName}\" --query \"[?contains(name, 'staticsite') && properties.configuration.ingress.external == \\`true\\`].properties.configuration.ingress.fqdn | [0]\" -o tsv) && " +
            $"if [ -z \"$static_host\" ]; then echo \"No external staticsite endpoint found\"; az containerapp list -g \"{resourceGroupName}\" --query \"[].{{name:name,external:properties.configuration.ingress.external,fqdn:properties.configuration.ingress.fqdn}}\" -o table; exit 1; fi && " +
            "echo \"Checking https://$static_host\" && " +
            "ok=0 && " +
            "for i in $(seq 1 30); do " +
            "if curl -sf --max-time 5 \"https://$static_host/index.html\" | grep -q Weather && " +
            "curl -sf --max-time 5 \"https://$static_host/api/weather\" | grep -q temperatureC; then " +
            "ok=1; break; " +
            "fi; " +
            "sleep 5; " +
            "done && " +
            "if [ \"$ok\" -ne 1 ]; then echo \"Endpoint verification failed\"; exit 1; fi";
    }

    private static void TriggerCleanupResourceGroup(string resourceGroupName, ITestOutputHelper output)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
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
