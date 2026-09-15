// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Tests aspire secret CRUD operations on a .NET AppHost.
/// </summary>
public sealed class SecretDotNetAppHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SecretCrudOnDotNetAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create an Empty AppHost project interactively
        await auto.AspireNewAsync("TestSecrets", counter, template: AspireTemplate.EmptyAppHost);

        // cd into the project
        await auto.TypeAsync("cd TestSecrets");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set secrets
        await auto.TypeAsync("aspire secret set Azure:Location eastus2");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set Parameters:db-password s3cret");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Get
        await auto.TypeAsync("aspire secret get Azure:Location");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("eastus2", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // List
        await auto.TypeAsync("aspire secret list");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("db-password", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Delete
        await auto.TypeAsync("aspire secret delete Azure:Location");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("deleted successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify deletion
        await auto.TypeAsync("aspire secret list");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("db-password", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    public async Task SecretDeploymentEnvironmentLoadsSelectedAspireSecretsFile()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("DeploymentSecrets", counter, template: AspireTemplate.EmptyAppHost);

        var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "DeploymentSecrets", "apphost.cs");
        var directiveLines = File.ReadLines(appHostFilePath)
            .TakeWhile(line => line.StartsWith("#:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
            .Where(line => line.StartsWith("#:", StringComparison.Ordinal));
        var directives = string.Join(Environment.NewLine, directiveLines);

        await File.WriteAllTextAsync(appHostFilePath, $$"""
            {{directives}}

            #pragma warning disable ASPIREPIPELINES001

            using Aspire.Hosting.Pipelines;

            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = args,
                DisableDashboard = true
            });

            var appHostEnvironment = builder.Environment.EnvironmentName;
            var apiKey = builder.Configuration["Parameters:api_key"] ?? "<missing>";

            if (!StringComparer.Ordinal.Equals(appHostEnvironment, "Staging") ||
                !StringComparer.Ordinal.Equals(apiKey, "staging-secret"))
            {
                throw new InvalidOperationException($"E2E_SECRET_MISMATCH:{appHostEnvironment}:{apiKey}");
            }

            builder.Pipeline.AddStep("verify-deployment-secret", async context =>
            {
                var task = await context.ReportingStep
                    .CreateTaskAsync("Verifying deployment secret", context.CancellationToken)
                    .ConfigureAwait(false);

                await using (task.ConfigureAwait(false))
                {
                    Console.WriteLine("E2E_DEPLOY_SECRET_OK");

                    await task.CompleteAsync(
                        "E2E_DEPLOY_SECRET_OK",
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
                }
            }, requiredBy: WellKnownPipelineSteps.Deploy);

            builder.Build().Run();
            """, TestContext.Current.CancellationToken);

        await auto.TypeAsync("cd DeploymentSecrets");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set Parameters:api_key development-secret");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set Parameters:api_key staging-secret --environment Staging");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret get Parameters:api_key --environment Staging | grep -qx 'staging-secret' && echo E2E_DOTNET_STAGING_SECRET_GET_OK");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("E2E_DOTNET_STAGING_SECRET_GET_OK", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret list --all | grep 'staging-secret' | grep 'Staging' >/dev/null && echo E2E_DOTNET_SECRET_LIST_ALL_OK");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("E2E_DOTNET_SECRET_LIST_ALL_OK", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire deploy --environment Staging --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("E2E_DEPLOY_SECRET_OK", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // dotnet applies this profile after RunCommand has prepared the launch context.
        // It must select Staging secrets even when the parent process is Development.
        await File.WriteAllTextAsync(Path.ChangeExtension(appHostFilePath, ".run.json"), """
            {
              "profiles": {
                "Staging": {
                  "commandName": "Project",
                  "environmentVariables": {
                    "DOTNET_ENVIRONMENT": "Staging"
                  }
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        await auto.TypeAsync("DOTNET_ENVIRONMENT=Development aspire start --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(3));
        await auto.AspireStopAsync(counter);
    }
}
