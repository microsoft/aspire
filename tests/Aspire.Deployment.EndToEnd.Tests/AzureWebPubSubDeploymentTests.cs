// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureWebPubSubDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public Task DeployWebPubSubCallbackToContainerApps() =>
        DeployAsync(useAppService: false, nameof(DeployWebPubSubCallbackToContainerApps));

    [Fact]
    public Task DeployWebPubSubCallbackToAppService() =>
        DeployAsync(useAppService: true, nameof(DeployWebPubSubCallbackToAppService));

    private async Task DeployAsync(bool useAppService, string testName)
    {
        var subscriptionId = DotnetProjectDeploymentHelpers.GetSubscriptionId();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(45));
        using var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName(useAppService ? "wps-appservice" : "wps-aca");
        var startTime = DateTime.UtcNow;
        const string projectName = "WebPubSubCallback";
        var marker = $"webpubsub-{Guid.NewGuid():N}";

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal(testName: testName);
            var pendingRun = terminal.RunAsync(cts.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);
            await auto.RunCommandAsync($"cd {projectName}", counter);
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter,
                useAppService ? "Aspire.Hosting.Azure.AppService" : "Aspire.Hosting.Azure.AppContainers");
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.WebPubSub");
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Dotnet");
            await auto.RunCommandAsync("dotnet new web -n Callback --no-restore", counter, TimeSpan.FromMinutes(2));

            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostFile = Path.Combine(projectDir, "apphost.cs");
            var environment = useAppService
                ? "builder.AddAzureAppServiceEnvironment(\"infra\");"
                : "builder.AddAzureContainerAppEnvironment(\"infra\");";
            DotnetProjectDeploymentHelpers.ReplaceInFile(appHostFile, "builder.Build().Run();", $$$"""
                {{{environment}}}
                var pubsub = builder.AddAzureWebPubSub("pubsub").WithParameter("sku", "Standard_S1");
                var hub = pubsub.AddHub("chat");
                var callback = builder.AddDotnetProject("callback", "Callback/Callback.csproj")
                    .WithHttpEndpoint(targetPort: 8080)
                    .WithExternalHttpEndpoints()
                    .WithReference(hub)
                    .WithEnvironment("PUBSUB_ENDPOINT", pubsub.Resource.ConnectionStringExpression);
                hub.AddEventHandler($"{callback.GetEndpoint("http")}/callbacks/{{hub}}/{{event}}?source=deployment",
                    systemEvents: ["connected"]);
                builder.Build().Run();
                """);
            File.Delete(Path.Combine(projectDir, "Callback", "Properties", "launchSettings.json"));
            File.WriteAllText(Path.Combine(projectDir, "Callback", "Program.cs"), $$"""
                var builder = WebApplication.CreateBuilder(args);
                var endpoint = builder.Configuration["PUBSUB_ENDPOINT"]
                    ?? throw new InvalidOperationException("The Web PubSub endpoint is missing.");
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                {
                    throw new InvalidOperationException("The Web PubSub endpoint is invalid.");
                }
                var app = builder.Build();
                // Web PubSub validates its upstream using the CloudEvents webhook abuse-protection handshake:
                // https://learn.microsoft.com/azure/azure-web-pubsub/reference-cloud-events#upstream-and-validation
                app.MapMethods("/callbacks/{hub}/{event}", ["OPTIONS"], (HttpContext context) =>
                {
                    context.Response.Headers["WebHook-Allowed-Origin"] = context.Request.Headers["WebHook-Request-Origin"];
                    return Results.Ok();
                });
                app.MapPost("/callbacks/{hub}/{event}", () => Results.Ok());
                app.MapGet("/deployment-marker", () => "{{marker}}");
                app.Run();
                """);

            await auto.RunCommandAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP={resourceGroupName} AZURE__SUBSCRIPTIONID={subscriptionId}", counter);
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(TimeSpan.FromMinutes(30), counter: counter);
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            var urlFile = Path.Combine(projectDir, "callback-url.txt");
            var hostCommand = useAppService
                ? $"az webapp list -g {resourceGroupName} --query \"[?starts_with(name, 'callback')].defaultHostName | [0]\" -o tsv"
                : $"az containerapp list -g {resourceGroupName} --query \"[?starts_with(name, 'callback')].properties.configuration.ingress.fqdn | [0]\" -o tsv";
            await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, $$"""
                set -euo pipefail
                az() { command az "$@" --subscription {{subscriptionId}}; }
                host=$({{hostCommand}})
                [ -n "$host" ]
                pubsub_id=$(az resource list -g {{resourceGroupName}} --resource-type Microsoft.SignalRService/WebPubSub --query '[0].id' -o tsv)
                [ -n "$pubsub_id" ]
                actual=$(az rest --method get --url "https://management.azure.com$pubsub_id/hubs/chat?api-version=2024-03-01" --query 'properties.eventHandlers[0].urlTemplate' -o tsv)
                expected="https://$host/callbacks/{hub}/{event}?source=deployment"
                if [ "$actual" != "$expected" ]; then
                    printf 'Expected callback: %s\nActual callback: %s\n' "$expected" "$actual"
                    exit 1
                fi
                printf 'https://%s' "$host" > {{AspireCliShellCommandHelpers.QuoteBashArg(urlFile)}}
                """, TimeSpan.FromMinutes(3));
            var url = File.ReadAllText(urlFile);
            await DotnetProjectDeploymentHelpers.VerifyResponseAsync($"{url}/deployment-marker", marker, cts.Token);
            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            DeploymentReporter.ReportDeploymentSuccess(testName, resourceGroupName,
                new Dictionary<string, string> { ["callback"] = url }, DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(testName, resourceGroupName, ex.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            await DotnetProjectDeploymentHelpers.CleanupAsync(resourceGroupName, subscriptionId, output);
        }
    }
}
