// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications to Azure Container Apps sandboxes.
/// </summary>
public sealed class AzureSandboxesDeploymentTests(ITestOutputHelper output)
{
    private const string EnableSandboxesEnvironmentVariable = "ASPIRE_DEPLOYMENT_TEST_ENABLE_SANDBOXES";
    private const string ExpectedResponseText = "Sandbox TS AppHost service is running.";
    private const string ExpectedDotNetResponseText = "Sandbox .NET service is running.";

    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(90);

    [Fact]
    public async Task RedeployProjectToAzureSandboxRetainsPreviousPublicUrl()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await RedeployProjectToAzureSandboxRetainsPreviousPublicUrlCore(linkedCts.Token);
    }

    [Fact]
    public async Task DeployDotNetProjectWithHttpAndHttpsEndpointsToAzureSandbox()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployDotNetProjectWithHttpAndHttpsEndpointsToAzureSandboxCore(linkedCts.Token);
    }

    private async Task DeployDotNetProjectWithHttpAndHttpsEndpointsToAzureSandboxCore(CancellationToken cancellationToken)
    {
        var subscriptionId = GetSandboxDeploymentSubscriptionId();
        const string projectName = "SandboxDotNet";
        const string serviceName = "SandboxWeb";
        using var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("sandbox-dotnet");
        var deploymentUrls = new Dictionary<string, string>();
        var urlFile = Path.Combine(workspace.WorkspaceRoot.FullName, "dotnet-url.txt");
        var stateMarkerFile = Path.Combine(workspace.WorkspaceRoot.FullName, "dotnet-state-marker");

        output.WriteLine($"Test: {nameof(DeployDotNetProjectWithHttpAndHttpsEndpointsToAzureSandbox)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        Hex1bTerminal? terminal = null;
        Task? pendingRun = null;
        Hex1bTerminalAutomator? auto = null;
        SequenceCounter? counter = null;
        var appHostReady = false;
        var destroyCompleted = false;
        var terminalExited = false;

        try
        {
            terminal = DeploymentE2ETestHelpers.CreateTestTerminal(width: 320, height: 60);
            pendingRun = terminal.RunAsync(cancellationToken);
            counter = new SequenceCounter();
            auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);

            output.WriteLine("Step 2: Creating an empty .NET AppHost...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);
            await auto.RunCommandAsync($"cd {projectName}", counter);

            output.WriteLine("Step 3: Adding the Azure sandboxes hosting package...");
            await AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.Sandboxes");

            output.WriteLine("Step 4: Creating the .NET web service...");
            await auto.RunCommandAsync($"dotnet new web -n {serviceName} --no-restore", counter, TimeSpan.FromMinutes(2));
            WriteDotNetSandboxAppHost(workspace, projectName, serviceName);

            await auto.RunCommandAsync($"touch {BashQuote(stateMarkerFile)}", counter);
            await auto.RunCommandAsync(
                $"unset ASPIRE_PLAYGROUND && " +
                $"export AZURE__LOCATION=westus3 && " +
                $"export Azure__Location=westus3 && " +
                $"export AZURE__RESOURCEGROUP={resourceGroupName} && " +
                $"export Azure__ResourceGroup={resourceGroupName} && " +
                "export COLUMNS=320",
                counter);

            appHostReady = true;

            output.WriteLine("Step 5: Deploying the .NET service to Azure Sandbox...");
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.RunCommandAsync(
                CaptureSandboxUrlFromStateCommand(stateMarkerFile, urlFile, "frontend"),
                counter,
                TimeSpan.FromSeconds(30));

            output.WriteLine("Step 6: Verifying TLS termination and the shared HTTP container port...");
            await auto.RunCommandAsync(
                VerifyDotNetSandboxDeploymentCommand(stateMarkerFile, urlFile),
                counter,
                TimeSpan.FromMinutes(4));

            deploymentUrls["frontend"] = File.ReadAllText(urlFile).Trim();

            output.WriteLine("Step 7: Destroying the Azure sandbox deployment...");
            await auto.AspireDestroyAsync(counter, TimeSpan.FromMinutes(10));
            destroyCompleted = true;

            await ExitTerminalAsync(auto, pendingRun);
            terminalExited = true;

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployDotNetProjectWithHttpAndHttpsEndpointsToAzureSandbox),
                resourceGroupName,
                deploymentUrls,
                DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployDotNetProjectWithHttpAndHttpsEndpointsToAzureSandbox),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);
            throw;
        }
        finally
        {
            if (!destroyCompleted && appHostReady && auto is not null && counter is not null && !terminalExited)
            {
                try
                {
                    output.WriteLine("Attempting best-effort aspire destroy after failure...");
                    await auto.AspireDestroyAsync(counter, TimeSpan.FromMinutes(10));
                }
                catch (Exception ex)
                {
                    output.WriteLine($"Best-effort aspire destroy failed: {ex.Message}");
                }
            }

            if (!terminalExited && auto is not null && pendingRun is not null)
            {
                try
                {
                    await ExitTerminalAsync(auto, pendingRun);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"Failed to exit terminal cleanly: {ex.Message}");
                }
            }

            terminal?.Dispose();

            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            var (cleanupSucceeded, cleanupMessage) = await CleanupResourceGroupAsync(resourceGroupName, subscriptionId);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, cleanupSucceeded, cleanupMessage);
        }
    }

    private async Task RedeployProjectToAzureSandboxRetainsPreviousPublicUrlCore(CancellationToken cancellationToken)
    {
        var subscriptionId = GetSandboxDeploymentSubscriptionId();

        var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("sandboxes");
        var deploymentUrls = new Dictionary<string, string>();

        var firstDeployOutputFile = Path.Combine(workspace.WorkspaceRoot.FullName, "first-deploy-output.txt");
        var secondDeployOutputFile = Path.Combine(workspace.WorkspaceRoot.FullName, "second-deploy-output.txt");
        var firstUrlFile = Path.Combine(workspace.WorkspaceRoot.FullName, "first-url.txt");
        var secondUrlFile = Path.Combine(workspace.WorkspaceRoot.FullName, "second-url.txt");
        var stateMarkerFile = Path.Combine(workspace.WorkspaceRoot.FullName, "state-marker");

        output.WriteLine($"Test: {nameof(RedeployProjectToAzureSandboxRetainsPreviousPublicUrl)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        Hex1bTerminal? terminal = null;
        Task? pendingRun = null;
        Hex1bTerminalAutomator? auto = null;
        SequenceCounter? counter = null;
        var appHostReady = false;
        var destroyCompleted = false;
        var terminalExited = false;

        try
        {
            terminal = DeploymentE2ETestHelpers.CreateTestTerminal(width: 320, height: 60);
            pendingRun = terminal.RunAsync(cancellationToken);

            counter = new SequenceCounter();
            auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);

            output.WriteLine("Step 3: Creating TypeScript AppHost...");
            await auto.RunCommandAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 4: Adding Azure sandboxes hosting package...");
            await AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.Sandboxes");

            output.WriteLine("Step 5: Publishing a Dockerfile-backed app as an Azure sandbox from TypeScript...");
            WriteSandboxAppHost(workspace);

            await auto.TypeAsync($"touch {BashQuote(stateMarkerFile)}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && " +
                $"export AZURE__LOCATION=westus3 && " +
                $"export Azure__Location=westus3 && " +
                $"export AZURE__RESOURCEGROUP={resourceGroupName} && " +
                $"export Azure__ResourceGroup={resourceGroupName} && " +
                "export COLUMNS=320");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            appHostReady = true;

            output.WriteLine("Step 6: Deploying the sandbox app...");
            await auto.TypeAsync($"aspire deploy 2>&1 | tee {BashQuote(firstDeployOutputFile)}");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync(CaptureSandboxUrlFromStateCommand(stateMarkerFile, firstUrlFile, "site"));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 7: Verifying the first sandbox URL...");
            await auto.TypeAsync(VerifySandboxUrlCommand(firstUrlFile));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            output.WriteLine("Step 8: Redeploying the sandbox app...");
            await auto.TypeAsync($"aspire deploy 2>&1 | tee {BashQuote(secondDeployOutputFile)}");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync(CaptureSandboxUrlFromStateCommand(stateMarkerFile, secondUrlFile, "site"));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 9: Verifying the redeploy summary and both sandbox URLs...");
            await auto.TypeAsync(VerifyRetainedUrlSummaryCommand(firstUrlFile, secondUrlFile, secondDeployOutputFile));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync(VerifySandboxUrlCommand(firstUrlFile));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            await auto.TypeAsync(VerifySandboxUrlCommand(secondUrlFile));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            deploymentUrls["first-retained"] = File.ReadAllText(firstUrlFile).Trim();
            deploymentUrls["second-current"] = File.ReadAllText(secondUrlFile).Trim();

            output.WriteLine("Step 10: Destroying the Azure sandbox deployment...");
            await auto.AspireDestroyAsync(counter, TimeSpan.FromMinutes(10));
            destroyCompleted = true;

            await ExitTerminalAsync(auto, pendingRun);
            terminalExited = true;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(RedeployProjectToAzureSandboxRetainsPreviousPublicUrl),
                resourceGroupName,
                deploymentUrls,
                duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(RedeployProjectToAzureSandboxRetainsPreviousPublicUrl),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            if (!destroyCompleted && appHostReady && auto is not null && counter is not null && !terminalExited)
            {
                try
                {
                    output.WriteLine("Attempting best-effort aspire destroy after failure...");
                    await auto.AspireDestroyAsync(counter, TimeSpan.FromMinutes(10));
                }
                catch (Exception ex)
                {
                    output.WriteLine($"Best-effort aspire destroy failed: {ex.Message}");
                }
            }

            if (!terminalExited && auto is not null && pendingRun is not null)
            {
                try
                {
                    await ExitTerminalAsync(auto, pendingRun);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"Failed to exit terminal cleanly: {ex.Message}");
                }
            }

            terminal?.Dispose();

            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            var (cleanupSucceeded, cleanupMessage) = await CleanupResourceGroupAsync(resourceGroupName, subscriptionId);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, cleanupSucceeded, cleanupMessage);
        }
    }

    private static async Task AddPackageAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string packageName)
    {
        await auto.TypeAsync($"aspire add {packageName}");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
    }

    private static void WriteSandboxAppHost(TemporaryWorkspace workspace)
    {
        var siteDir = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "site"));
        File.WriteAllText(Path.Combine(siteDir.FullName, "Dockerfile"), """
            FROM nginx:1.31-alpine
            COPY index.html /usr/share/nginx/html/index.html
            EXPOSE 80
            """);
        File.WriteAllText(Path.Combine(siteDir.FullName, "index.html"), $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="UTF-8"><title>Sandbox TS AppHost</title></head>
            <body>{{ExpectedResponseText}}</body>
            </html>
            """);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"), """
            import { AzureSandboxTier, createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            await builder.addAzureSandboxGroup('sandboxes');

            await builder.addDockerfile('site', './site')
                .withHttpEndpoint({ name: 'http', targetPort: 80 })
                .withExternalHttpEndpoints()
                .publishAsAzureSandbox({
                    tier: AzureSandboxTier.Medium,
                    endpoints: [
                        {
                            name: 'http',
                            anonymous: true
                        }
                    ]
                });

            await builder.build().run();
            """);
    }

    private static void WriteDotNetSandboxAppHost(TemporaryWorkspace workspace, string projectName, string serviceName)
    {
        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var appHostFilePath = Path.Combine(projectDir, "apphost.cs");
        var serviceDir = Path.Combine(projectDir, serviceName);
        var propertiesDir = Directory.CreateDirectory(Path.Combine(serviceDir, "Properties"));

        File.WriteAllText(Path.Combine(serviceDir, "Program.cs"), $$"""
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            app.MapGet("/", () => "{{ExpectedDotNetResponseText}}");

            app.Run();
            """);
        File.WriteAllText(Path.Combine(propertiesDir.FullName, "launchSettings.json"), """
            {
              "$schema": "http://json.schemastore.org/launchsettings.json",
              "profiles": {
                "https": {
                  "commandName": "Project",
                  "dotnetRunMessages": true,
                  "launchBrowser": false,
                  "applicationUrl": "https://localhost:7001;http://localhost:5001",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development"
                  }
                }
              }
            }
            """);
        File.WriteAllText(appHostFilePath, $$"""
            #pragma warning disable ASPIREAZURE001

            using Aspire.Hosting.Azure;

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddAzureSandboxGroup("env");

            builder.AddProject("frontend", "{{serviceName}}/{{serviceName}}.csproj")
                .WithExternalHttpEndpoints();

            builder.Build().Run();
            """);
    }

    private static string CaptureSandboxUrlFromStateCommand(string stateMarkerFile, string outputFile, string resourceName)
    {
        var sandboxStateUrlKey = $"\"Azure:Sandboxes:{resourceName}-sandbox-container:Ports:0:Url\"";
        return
            $"STATE_FILE=$(find \"$HOME/.aspire/deployments\" -name '*.json' -newer {BashQuote(stateMarkerFile)} -exec grep -l '{sandboxStateUrlKey}' {{}} + | head -n 1) && " +
            "if [ -z \"$STATE_FILE\" ]; then echo \"Sandbox deployment state file not found\"; find \"$HOME/.aspire/deployments\" -name '*.json' -newer " + BashQuote(stateMarkerFile) + " -print; exit 1; fi && " +
            $"URL=$(grep -Eo '\"Azure:Sandboxes:{resourceName}-sandbox-container:Ports:0:Url\"[[:space:]]*:[[:space:]]*\"[^\"]+\"' \"$STATE_FILE\" | head -n 1 | sed -E 's/^.*\"([^\"]+)\".*$/\\1/') && " +
            "if [ -z \"$URL\" ]; then echo \"Sandbox URL not found in $STATE_FILE\"; cat \"$STATE_FILE\"; exit 1; fi && " +
            $"printf '%s\\n' \"$URL\" > {BashQuote(outputFile)} && " +
            "echo \"Sandbox URL from state: $URL\"";
    }

    private static string VerifyDotNetSandboxDeploymentCommand(string stateMarkerFile, string urlFile)
    {
        const string statePrefix = "Azure:Sandboxes:frontend-sandbox-container:Ports";
        return
            $"STATE_FILE=$(find \"$HOME/.aspire/deployments\" -name '*.json' -newer {BashQuote(stateMarkerFile)} -exec grep -l '\"{statePrefix}:0:Url\"' {{}} + | head -n 1) && " +
            $"URL=$(cat {BashQuote(urlFile)}) && " +
            "case \"$URL\" in https://*) ;; *) echo \"Expected TLS-terminated HTTPS sandbox URL, got $URL\"; exit 1;; esac && " +
            $"PORT_URL_COUNT=$(grep -Ec '\"{statePrefix}:[0-9]+:Url\"' \"$STATE_FILE\") && " +
            "if [ \"$PORT_URL_COUNT\" -ne 1 ]; then echo \"Expected one shared sandbox port, found $PORT_URL_COUNT\"; cat \"$STATE_FILE\"; exit 1; fi && " +
            $"grep -Eq '\"{statePrefix}:0:Port\"[[:space:]]*:[[:space:]]*8080' \"$STATE_FILE\" || {{ echo \"Expected sandbox target port 8080\"; cat \"$STATE_FILE\"; exit 1; }} && " +
            $"grep -Eq '\"{statePrefix}:0:Protocol\"[[:space:]]*:[[:space:]]*\"Http\"' \"$STATE_FILE\" || {{ echo \"Expected HTTP protocol behind sandbox TLS termination\"; cat \"$STATE_FILE\"; exit 1; }} && " +
            $"grep -Eq '\"{statePrefix}:0:Anonymous\"[[:space:]]*:[[:space:]]*false' \"$STATE_FILE\" || {{ echo \"Expected authenticated sandbox endpoint\"; cat \"$STATE_FILE\"; exit 1; }} && " +
            "success=0 && " +
            "for i in $(seq 1 18); do " +
            "STATUS=$(curl -sS -o /dev/null -w '%{http_code}' \"$URL\" --max-time 10 2>/tmp/aspire-sandbox-dotnet-curl.err) && " +
            "case \"$STATUS\" in 2??|3??|401|403) echo \"  Authenticated endpoint responded with HTTP $STATUS (attempt $i)\"; success=1; break;; esac; " +
            "echo \"  Attempt $i failed; retrying in 10s...\"; sleep 10; " +
            "done; " +
            "if [ \"$success\" -ne 1 ]; then echo \"Sandbox URL check failed for $URL\"; cat /tmp/aspire-sandbox-dotnet-curl.err 2>/dev/null || true; exit 1; fi";
    }

    private static string VerifySandboxUrlCommand(string urlFile)
    {
        return
            $"URL=$(cat {BashQuote(urlFile)}) && " +
            "echo \"Checking $URL\" && " +
            "success=0 && " +
            "for i in $(seq 1 18); do " +
            $"BODY=$(curl -fsS \"$URL\" --max-time 10 2>/tmp/aspire-sandbox-curl.err) && echo \"$BODY\" | grep -Fq {BashQuote(ExpectedResponseText)} && {{ echo \"  OK (attempt $i)\"; success=1; break; }}; " +
            "echo \"  Attempt $i failed; retrying in 10s...\"; " +
            "sleep 10; " +
            "done; " +
            "if [ \"$success\" -ne 1 ]; then echo \"Sandbox URL check failed for $URL\"; cat /tmp/aspire-sandbox-curl.err 2>/dev/null || true; exit 1; fi";
    }

    private static string VerifyRetainedUrlSummaryCommand(string firstUrlFile, string secondUrlFile, string secondDeployOutputFile)
    {
        return
            $"FIRST_URL=$(cat {BashQuote(firstUrlFile)}) && " +
            $"SECOND_URL=$(cat {BashQuote(secondUrlFile)}) && " +
            "if [ \"$FIRST_URL\" = \"$SECOND_URL\" ]; then echo \"Expected redeploy to produce a new sandbox URL, but both were $FIRST_URL\"; exit 1; fi && " +
            $"NORMALIZED=$(tr -d '\\r' < {BashQuote(secondDeployOutputFile)} | tr '\\n' ' ' | sed -E 's/[[:space:]]+/ /g') && " +
            "case \"$NORMALIZED\" in *\"retained for references configured before sandbox deployment\"*) ;; *) echo \"Retained URL summary text not found\"; exit 1;; esac && " +
            "case \"$NORMALIZED\" in *\"$FIRST_URL\"*) ;; *) echo \"First URL was not reported as retained\"; exit 1;; esac && " +
            "echo \"Redeploy changed URL from $FIRST_URL to $SECOND_URL and reported the retained URL\"";
    }

    private static async Task ExitTerminalAsync(Hex1bTerminalAutomator auto, Task pendingRun)
    {
        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }

    private static string GetSandboxDeploymentSubscriptionId()
    {
        if (DeploymentE2ETestHelpers.IsRunningInCI &&
            !string.Equals(Environment.GetEnvironmentVariable(EnableSandboxesEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip($"Azure sandboxes deployment tests require preview enrollment and are disabled for this deployment environment. Set {EnableSandboxesEnvironmentVariable}=true only after the environment has the required sandbox preview access and role assignments.");
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

        return subscriptionId;
    }

    private async Task<(bool Success, string Message)> CleanupResourceGroupAsync(
        string resourceGroupName,
        string subscriptionId)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add("group");
            startInfo.ArgumentList.Add("delete");
            startInfo.ArgumentList.Add("--name");
            startInfo.ArgumentList.Add(resourceGroupName);
            startInfo.ArgumentList.Add("--subscription");
            startInfo.ArgumentList.Add(subscriptionId);
            startInfo.ArgumentList.Add("--yes");
            startInfo.ArgumentList.Add("--no-wait");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                var message = $"Failed to start cleanup for resource group: {resourceGroupName}";
                output.WriteLine(message);
                return (false, message);
            }

            // Read both streams concurrently so Azure CLI cannot block while waiting for a full pipe buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            _ = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode == 0)
            {
                var message = $"Resource group deletion initiated: {resourceGroupName}";
                output.WriteLine(message);
                return (true, "Deletion initiated");
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

    private static string BashQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
