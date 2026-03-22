// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI deployment to Docker Compose.
/// Tests the complete workflow: create project, add Docker integration, deploy, and verify.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class DockerDeploymentTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireDockerDeployTest";

    [Fact]
    public async Task CreateAndDeployToDockerCompose()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        using var terminal = CliE2ETestHelpers.CreateTestTerminal();

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // PrepareEnvironment
        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        // Step 1: Create a new Aspire Starter App (no Redis cache)
        await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

        // Step 2: Navigate into the project directory
        await auto.TypeAsync($"cd {ProjectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Add Aspire.Hosting.Docker package using aspire add
        // Pass the package name directly as an argument to avoid interactive selection
        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();

        // In CI, aspire add shows a version selection prompt (unlike aspire new which auto-selects when channel is set)
        if (isCI)
        {
            await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
            await auto.EnterAsync(); // select first version (PR build)
        }

        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

        // Step 4: Modify AppHost's main file to add Docker Compose environment
        // Note: Aspire templates use AppHost.cs as the main entry point, not Program.cs
        {
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostDir = Path.Combine(projectDir, $"{ProjectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert the Docker Compose environment before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Add Docker Compose environment for deployment
builder.AddDockerComposeEnvironment("compose");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");
        }

        // Step 5: Create output directory for deployment artifacts
        await auto.TypeAsync("mkdir -p deploy-output");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 6: Unset ASPIRE_PLAYGROUND before deploy
        // ASPIRE_PLAYGROUND=true takes precedence over --non-interactive in CliHostEnvironment,
        // which causes Spectre.Console to try to show interactive spinners and prompts concurrently,
        // resulting in "Operations with dynamic displays cannot run at the same time" errors.
        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 7: Run aspire deploy to deploy to Docker Compose
        // This will build the project, generate Docker Compose files, and start the containers
        // Use --non-interactive to avoid any prompts during deployment
        await auto.TypeAsync("aspire deploy -o deploy-output --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        // Step 8: Capture the port from docker ps output for verification
        // We need to parse the port from docker ps to make a web request
        await auto.TypeAsync("docker ps --format '{{.Ports}}' | grep -oE '0\\.0\\.0\\.0:[0-9]+' | head -1 | cut -d: -f2");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 9: Verify the deployment is running with docker ps
        await auto.TypeAsync("docker ps");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 10: Make a web request to verify the application is working
        // We'll use curl to make the request
        await auto.TypeAsync("curl -s -o /dev/null -w '%{http_code}' http://localhost:$(docker ps --format '{{.Ports}}' --filter 'name=webfrontend' | grep -oE '0\\.0\\.0\\.0:[0-9]+->8080' | head -1 | cut -d: -f2 | cut -d'-' -f1) 2>/dev/null || echo 'request-failed'");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Step 11: Clean up - stop and remove containers
        await auto.TypeAsync("cd deploy-output && docker compose down --volumes --remove-orphans 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task CreateAndDeployToDockerComposeInteractive()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        using var terminal = CliE2ETestHelpers.CreateTestTerminal();

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // PrepareEnvironment
        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        // Step 1: Create a new Aspire Starter App (no Redis cache)
        await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

        // Step 2: Navigate into the project directory
        await auto.TypeAsync($"cd {ProjectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Add Aspire.Hosting.Docker package using aspire add
        // Pass the package name directly as an argument to avoid interactive selection
        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();

        // In CI, aspire add shows a version selection prompt (unlike aspire new which auto-selects when channel is set)
        if (isCI)
        {
            await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
            await auto.EnterAsync(); // select first version (PR build)
        }

        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

        // Step 4: Modify AppHost's main file to add Docker Compose environment
        // Note: Aspire templates use AppHost.cs as the main entry point, not Program.cs
        {
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostDir = Path.Combine(projectDir, $"{ProjectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert the Docker Compose environment before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Add Docker Compose environment for deployment
builder.AddDockerComposeEnvironment("compose");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");
        }

        // Step 5: Create output directory for deployment artifacts
        await auto.TypeAsync("mkdir -p deploy-output");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 6: Unset ASPIRE_PLAYGROUND before deploy
        // ASPIRE_PLAYGROUND=true takes precedence over --non-interactive in CliHostEnvironment,
        // which causes Spectre.Console to try to show interactive spinners and prompts concurrently,
        // resulting in "Operations with dynamic displays cannot run at the same time" errors.
        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 7: Run aspire deploy to deploy to Docker Compose in INTERACTIVE MODE
        // This test specifically validates that the concurrent ShowStatusAsync fix works correctly
        // when interactive spinners are enabled (without --non-interactive flag).
        // The fix prevents nested ShowStatusAsync calls from causing Spectre.Console errors.
        await auto.TypeAsync("aspire deploy -o deploy-output");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        // Step 8: Capture the port from docker ps output for verification
        // We need to parse the port from docker ps to make a web request
        await auto.TypeAsync("docker ps --format '{{.Ports}}' | grep -oE '0\\.0\\.0\\.0:[0-9]+' | head -1 | cut -d: -f2");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 9: Verify the deployment is running with docker ps
        await auto.TypeAsync("docker ps");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 10: Make a web request to verify the application is working
        // We'll use curl to make the request
        await auto.TypeAsync("curl -s -o /dev/null -w '%{http_code}' http://localhost:$(docker ps --format '{{.Ports}}' --filter 'name=webfrontend' | grep -oE '0\\.0\\.0\\.0:[0-9]+->8080' | head -1 | cut -d: -f2 | cut -d'-' -f1) 2>/dev/null || echo 'request-failed'");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Step 11: Clean up - stop and remove containers
        await auto.TypeAsync("cd deploy-output && docker compose down --volumes --remove-orphans 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
