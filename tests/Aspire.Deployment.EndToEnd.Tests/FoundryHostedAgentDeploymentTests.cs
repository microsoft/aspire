// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications with Foundry Hosted Agents.
/// </summary>
public sealed class FoundryHostedAgentDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for Azure AI Foundry provisioning and model deployment.
    // Foundry deployments can take longer than standard ACA due to AI resource provisioning.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/16330")]
    public async Task DeployFoundryHostedAgentToAzure()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployFoundryHostedAgentToAzureCore(cancellationToken);
    }

    private async Task DeployFoundryHostedAgentToAzureCore(CancellationToken cancellationToken)
    {
        // Validate prerequisites
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

        var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var deploymentUrls = new Dictionary<string, string>();
        // Generate a unique resource group name with pattern: e2e-[testcasename]-[runid]-[attempt]
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("foundry-agent");
        var projectName = "FoundryAgent";

        output.WriteLine($"Test: {nameof(DeployFoundryHostedAgentToAzure)}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // Step 1: Prepare environment
            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            // Step 2: Set up CLI environment
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            // Step 3: Create starter project using aspire new (for basic AppHost scaffold)
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Foundry package to the AppHost
            output.WriteLine("Step 5: Adding Foundry hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Foundry");
            await auto.EnterAsync();

            // aspire add may show a version selection prompt
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Create a dedicated .NET hosted agent project
            // PublishAsHostedAgent requires a proper agent application, not a standard apiservice.
            output.WriteLine("Step 6: Creating .NET hosted agent project...");
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var hostedAgentDir = Path.Combine(projectDir, "DotNetHostedAgent");
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");

            Directory.CreateDirectory(hostedAgentDir);

            // Write minimal hosted agent .csproj
            // The project is created outside the repo, so it has no central package management.
            // Explicit versions are required, matching those from the playground DotNetHostedAgent.
            File.WriteAllText(Path.Combine(hostedAgentDir, "DotNetHostedAgent.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <EnablePreviewFeatures>true</EnablePreviewFeatures>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Azure.AI.AgentServer.AgentFramework" Version="1.0.0-beta.11" />
                    <PackageReference Include="Azure.AI.Projects" Version="1.2.0-beta.4" />
                    <PackageReference Include="Azure.AI.OpenAI" Version="2.5.0-beta.1" />
                    <PackageReference Include="Azure.Identity" Version="1.18.0" />
                    <PackageReference Include="Microsoft.Agents.AI.OpenAI" Version="1.1.0" />
                    <PackageReference Include="Microsoft.Extensions.AI" Version="10.4.0" />
                    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.4.0" />
                  </ItemGroup>
                </Project>
                """);

            // Write minimal hosted agent Program.cs
            // This follows the same pattern as the playground DotNetHostedAgent:
            // reads ConnectionStrings__chat, creates an AI agent, and runs on DEFAULT_AD_PORT.
            File.WriteAllText(Path.Combine(hostedAgentDir, "Program.cs"), """
                using System.ComponentModel;
                using System.Data.Common;
                using Azure.AI.AgentServer.AgentFramework.Extensions;
                using Azure.AI.OpenAI;
                using Azure.Identity;
                using Microsoft.Agents.AI;
                using Microsoft.Extensions.AI;

                string chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__chat")
                    ?? throw new InvalidOperationException("ConnectionStrings__chat is not set.");

                DbConnectionStringBuilder chatConnectionBuilder = new()
                {
                    ConnectionString = chatConnectionString,
                };

                string endpoint = chatConnectionBuilder.TryGetValue("Endpoint", out object? epValue) ? epValue?.ToString()! : throw new InvalidOperationException("Missing Endpoint.");
                string deploymentName = chatConnectionBuilder.TryGetValue("Deployment", out object? dnValue) ? dnValue?.ToString()! : throw new InvalidOperationException("Missing Deployment.");

                Uri openAiEndpoint = new(endpoint);

                [Description("Get a weather forecast")]
                string GetWeatherForecast() => "Sunny, 25°C";

                DefaultAzureCredential credential = new();

                IChatClient chatClient = new AzureOpenAIClient(openAiEndpoint, credential)
                    .GetChatClient(deploymentName)
                    .AsIChatClient();

                AIAgent agent = chatClient.AsAIAgent(
                    name: "WeatherAgent",
                    instructions: "You are the Weather Intelligence Agent.",
                    tools: [AIFunctionFactory.Create(GetWeatherForecast)]);

                await agent.RunAIAgentAsync(telemetrySourceName: "Agents");
                """);

            // Write Dockerfile for the hosted agent
            File.WriteAllText(Path.Combine(hostedAgentDir, ".dockerignore"), """
                bin/
                obj/
                """);

            output.WriteLine($"Created hosted agent project at: {hostedAgentDir}");

            // Step 7: Add hosted agent to the solution and add project reference from AppHost
            output.WriteLine("Step 7: Adding hosted agent to solution...");
            await auto.TypeAsync($"dotnet sln add DotNetHostedAgent/DotNetHostedAgent.csproj");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync($"dotnet add {projectName}.AppHost/{projectName}.AppHost.csproj reference DotNetHostedAgent/DotNetHostedAgent.csproj");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 8: Modify AppHost.cs to wire up Foundry + hosted agent
            // Replace the standard starter template AppHost with Foundry-based configuration.
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");
            output.WriteLine($"Modifying AppHost.cs at: {appHostFilePath}");

            var appHostContent = File.ReadAllText(appHostFilePath);

            // Add the Foundry using directive
            appHostContent = "using Aspire.Hosting.Foundry;\n" + appHostContent;

            // Insert Foundry resources before builder.Build().Run();
            appHostContent = appHostContent.Replace(
                "builder.Build().Run();",
                """
                var foundry = builder.AddFoundry("aif-myfoundry");
                var foundryProject = foundry.AddProject("proj-myproject");
                var chat = foundryProject.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

                builder.AddProject<Projects.DotNetHostedAgent>("dotnet-hosted-agent")
                    .WithReference(chat).WaitFor(chat)
                    .PublishAsHostedAgent(foundryProject);

                builder.Build().Run();
                """);

            File.WriteAllText(appHostFilePath, appHostContent);
            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            // Step 9: Navigate to AppHost project directory
            output.WriteLine("Step 9: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 10: Set environment variables for deployment
            // - Unset ASPIRE_PLAYGROUND to avoid conflicts
            // - Set Azure location
            // - Set AZURE__RESOURCEGROUP to use our unique resource group name
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 11: Deploy to Azure using aspire deploy
            output.WriteLine("Step 11: Starting Foundry Hosted Agent deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            // Wait for pipeline to complete successfully
            // Foundry deployments may take longer due to AI resource provisioning
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(35));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 12: Verify deployed resources exist in the resource group
            output.WriteLine("Step 12: Verifying deployed resources...");
            await auto.TypeAsync(
                $"RG_NAME=\"{resourceGroupName}\" && " +
                "echo \"Resource group: $RG_NAME\" && " +
                "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                "resources=$(az resource list -g \"$RG_NAME\" -o table 2>/dev/null) && " +
                "echo \"$resources\" && " +
                "if [ -z \"$resources\" ]; then echo \"❌ No resources found in resource group\"; exit 1; fi && " +
                "echo \"✅ Resources found in resource group\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 13: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployFoundryHostedAgentToAzure),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployFoundryHostedAgentToAzure),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Clean up the resource group we created
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName, output);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
    }

    /// <summary>
    /// Triggers cleanup of a specific resource group.
    /// This is fire-and-forget - the hourly cleanup workflow handles any missed resources.
    /// </summary>
    private static void TriggerCleanupResourceGroup(string resourceGroupName, ITestOutputHelper output)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
