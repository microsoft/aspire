// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AppServiceDotnetProjectDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeployDotnetProjectWithStaticFrontendToAzureAppService()
    {
        var subscriptionId = DotnetProjectDeploymentHelpers.GetSubscriptionId();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(45));
        using var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("appservice-v2");
        var startTime = DateTime.UtcNow;
        const string projectName = "DotnetAppService";
        var marker = $"appservice-{Guid.NewGuid():N}";

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cts.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.JsReact, useRedisCache: false);
            await auto.RunCommandAsync($"cd {projectName}", counter);
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppService");
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Dotnet");

            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostFile = Path.Combine(projectDir, $"{projectName}.AppHost", "AppHost.cs");
            var content = DotnetProjectDeploymentHelpers.ReplaceExactlyOnce(File.ReadAllText(appHostFile),
                $"AddProject<Projects.{projectName}_Server>(\"server\")",
                $"AddDotnetProject(\"server\", \"../{projectName}.Server/{projectName}.Server.csproj\")");
            Assert.Contains("server.PublishWithContainerFiles(webfrontend, \"wwwroot\");", content, StringComparison.Ordinal);
            content = DotnetProjectDeploymentHelpers.ReplaceExactlyOnce(content, "builder.Build().Run();",
                "builder.AddAzureAppServiceEnvironment(\"infra\");\nbuilder.Build().Run();");
            File.WriteAllText(appHostFile, "#pragma warning disable ASPIREDOTNETPROJECT001\n" + content);
            DotnetProjectDeploymentHelpers.ReplaceInFile(Path.Combine(projectDir, $"{projectName}.Server", "Program.cs"),
                "app.Run();", $$"""
                app.MapGet("/api/deployment-marker", () => "{{marker}}");
                app.Run();
                """);
            // Vite copies public files to its build output; this must reach the .NET image through
            // PublishWithContainerFiles, not a separate frontend site or an empty deployment slot.
            var publicDir = Directory.CreateDirectory(Path.Combine(projectDir, "frontend", "public"));
            File.WriteAllText(Path.Combine(publicDir.FullName, "deployment-marker.txt"), $"static:{marker}");

            await auto.RunCommandAsync($"cd {projectName}.AppHost", counter);
            await auto.RunCommandAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP={resourceGroupName} AZURE__SUBSCRIPTIONID={subscriptionId}", counter);
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(TimeSpan.FromMinutes(30), counter: counter);
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            var urlFile = Path.Combine(projectDir, "server-url.txt");
            await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, $$"""
                set -euo pipefail
                az() { command az "$@" --subscription {{subscriptionId}}; }
                site=$(az webapp list -g {{resourceGroupName}} --query "[?starts_with(name, 'server')].name" -o tsv)
                [ -n "$site" ] && [ "$(printf '%s\n' "$site" | wc -l)" -eq 1 ]
                [ "$(az webapp deployment slot list -g {{resourceGroupName}} -n "$site" --query 'length(@)' -o tsv)" -eq 0 ]
                [ "$(az webapp config show -g {{resourceGroupName}} -n "$site" --query linuxFxVersion -o tsv)" = SITECONTAINERS ]
                id=$(az webapp show -g {{resourceGroupName}} -n "$site" --query id -o tsv)
                image=$(az rest --method get --url "https://management.azure.com$id/sitecontainers/main?api-version=2024-11-01" --query properties.image -o tsv)
                case "$image" in *.azurecr.io/*) ;; *) echo "Unexpected server image: $image"; exit 1;; esac
                echo "$site: $image"
                az webapp show -g {{resourceGroupName}} -n "$site" --query defaultHostName -o tsv > {{AspireCliShellCommandHelpers.QuoteBashArg(urlFile)}}
                """, TimeSpan.FromMinutes(3));
            var url = $"https://{File.ReadAllText(urlFile).Trim()}";
            await Task.WhenAll(
                DotnetProjectDeploymentHelpers.VerifyResponseAsync($"{url}/api/deployment-marker", marker, cts.Token),
                DotnetProjectDeploymentHelpers.VerifyResponseAsync($"{url}/deployment-marker.txt", $"static:{marker}", cts.Token));
            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;
            DeploymentReporter.ReportDeploymentSuccess(nameof(DeployDotnetProjectWithStaticFrontendToAzureAppService),
                resourceGroupName, new Dictionary<string, string> { ["server"] = url }, DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(nameof(DeployDotnetProjectWithStaticFrontendToAzureAppService),
                resourceGroupName, ex.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            await DotnetProjectDeploymentHelpers.CleanupAsync(resourceGroupName, subscriptionId, output);
        }
    }
}
