// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable OPENAI001
#pragma warning disable AAIP001

using System.ClientModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class FoundryDotnetProjectDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeployEchoDotnetProjectAsFoundryHostedAgent()
    {
        var subscriptionId = DotnetProjectDeploymentHelpers.GetSubscriptionId();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(55));
        using var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("foundry-echo-v2");
        var startTime = DateTime.UtcNow;
        const string projectName = "FoundryEcho";
        const string agentName = "echo-ha";
        var marker = Guid.NewGuid().ToString("N");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cts.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);
            await auto.RunCommandAsync($"cd {projectName}", counter);
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Foundry");
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Dotnet");

            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostFile = Path.Combine(projectDir, "apphost.cs");
            var directives = string.Join(Environment.NewLine,
                File.ReadLines(appHostFile).Where(line => line.StartsWith("#:", StringComparison.Ordinal)));
            File.WriteAllText(appHostFile, $$"""
                {{directives}}
                #pragma warning disable ASPIREDOTNETPROJECT001
                using Aspire.Hosting.Foundry;

                var builder = DistributedApplication.CreateBuilder(args);
                var project = builder.AddFoundry("foundry").AddProject("project");
                builder.AddDotnetProject("echo", Path.Combine("EchoAgent", "EchoAgent.csproj"))
                    .AsHostedAgent(project, HostedAgentProtocol.Responses, "2.0.0");
                builder.Build().Run();
                """);
            FoundryEchoTestApp.Write(Path.Combine(projectDir, "EchoAgent"), marker);

            await auto.RunCommandAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=swedencentral AZURE__RESOURCEGROUP={resourceGroupName} AZURE__SUBSCRIPTIONID={subscriptionId}" +
                " && export AZURE__TENANTID=$(az account show --query tenantId -o tsv)", counter);
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(TimeSpan.FromMinutes(35), counter: counter);
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            var endpoint = await GetProjectEndpointAsync(subscriptionId, resourceGroupName, cts.Token);
            var client = new AIProjectClient(endpoint, AzureAuthenticationHelpers.GetAzureCredential());
            var versions = new List<ProjectsAgentVersion>();
            await foreach (var version in client.AgentAdministrationClient.GetAgentVersionsAsync(agentName, cancellationToken: cts.Token))
            {
                versions.Add(version);
            }

            // This is a new resource group and model-free project: precisely one version must have
            // been produced by this deploy, and it must reference the newly published registry image.
            var deployedVersion = Assert.Single(versions);
            Assert.Equal(agentName, deployedVersion.Name);
            Assert.True(deployedVersion.CreatedAt >= new DateTimeOffset(startTime).AddMinutes(-5),
                "The hosted version must have been created during this deployment.");
            var definition = Assert.IsType<HostedAgentDefinition>(deployedVersion.Definition);
            var container = Assert.IsType<ContainerConfiguration>(definition.ContainerConfiguration);
            var image = container.Image;
            Assert.Contains(".azurecr.io/", image, StringComparison.Ordinal);
            Assert.Collection(definition.ProtocolVersions, protocol =>
            {
                Assert.Equal(ProjectsAgentProtocol.Responses, protocol.Protocol);
                Assert.Equal("2.0.0", protocol.Version);
            });
            var agent = await client.AgentAdministrationClient.GetAgentAsync(agentName, cancellationToken: cts.Token);
            Assert.NotNull(agent.Value.AgentEndpoint);
            Assert.Contains(AgentEndpointProtocol.Responses, agent.Value.AgentEndpoint.Protocols);
            output.WriteLine($"Hosted agent {agentName}, version {deployedVersion.Version}, image {image}");

            await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, $$"""
                set -euo pipefail
                az() { command az "$@" --subscription {{subscriptionId}}; }
                image={{AspireCliShellCommandHelpers.QuoteBashArg(image)}}
                registry=$(az acr list -g {{resourceGroupName}} --query '[0].name' -o tsv)
                login=$(az acr show -g {{resourceGroupName}} -n "$registry" --query loginServer -o tsv)
                [ "${image%%/*}" = "$login" ]
                digest=$(az acr manifest show-metadata -r "$registry" -n "${image#*/}" --query digest -o tsv)
                case "$digest" in sha256:*) ;; *) exit 1;; esac
                echo "Published echo image: $image ($digest)"
                """, TimeSpan.FromMinutes(3));

            var nonce = $"{marker}:{Guid.NewGuid():N}";
            // The new agent has exactly one version, so its default route selects this deployment.
            // Invoke that hosted agent, not a prompt agent or a dashboard health URL.
            // Hosting can cold-start after version creation. The bounded retry is not a fallback
            // to another route: incorrect protocol/auth/routing must fail the deployment scenario.
            var responses = client.ProjectOpenAIClient.GetProjectResponsesClientForAgent(
                new AgentReference(name: agentName));
            string? lastFailure = null;
            var succeeded = false;
            for (var attempt = 0; attempt < 18; attempt++)
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                requestCts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var response = await responses.CreateResponseAsync(nonce, cancellationToken: requestCts.Token);
                    Assert.Equal($"Echo: {nonce}", response.Value.GetOutputText());
                    succeeded = true;
                    break;
                }
                catch (Exception ex) when (ex is ClientResultException || ex is OperationCanceledException && !cts.IsCancellationRequested)
                {
                    lastFailure = ex.Message;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            }

            Assert.True(succeeded, $"The deployed hosted version did not answer its authenticated Responses request: {lastFailure}");
            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;
            DeploymentReporter.ReportDeploymentSuccess(nameof(DeployEchoDotnetProjectAsFoundryHostedAgent),
                resourceGroupName, new Dictionary<string, string> { [agentName] = endpoint.ToString() }, DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(nameof(DeployEchoDotnetProjectAsFoundryHostedAgent),
                resourceGroupName, ex.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            await DotnetProjectDeploymentHelpers.CleanupAsync(resourceGroupName, subscriptionId, output);
        }
    }

    private static async Task<Uri> GetProjectEndpointAsync(string subscriptionId, string resourceGroupName, CancellationToken cancellationToken)
    {
        var token = await AzureAuthenticationHelpers.GetAzureCredential().GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var resourcesResponse = await http.GetAsync(
            $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/resources?api-version=2021-04-01", cancellationToken);
        resourcesResponse.EnsureSuccessStatusCode();
        using var resources = JsonDocument.Parse(await resourcesResponse.Content.ReadAsStringAsync(cancellationToken));
        var project = resources.RootElement.GetProperty("value").EnumerateArray().Single(resource =>
            string.Equals(resource.GetProperty("type").GetString(), "Microsoft.CognitiveServices/accounts/projects", StringComparison.OrdinalIgnoreCase));
        var id = project.GetProperty("id").GetString();
        using var projectResponse = await http.GetAsync($"https://management.azure.com{id}?api-version=2025-06-01", cancellationToken);
        projectResponse.EnsureSuccessStatusCode();
        using var projectDocument = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync(cancellationToken));
        return new Uri(projectDocument.RootElement.GetProperty("properties").GetProperty("endpoints").GetProperty("AI Foundry API").GetString()
            ?? throw new InvalidOperationException("The deployed Foundry project has no endpoint."));
    }
}
