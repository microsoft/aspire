// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
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
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.VerifyPullRequestCliVersionAsync(counter);

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

        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

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

        // Step 10: Verify the frontend responds from inside its own network namespace.
        await VerifyFrontendRespondsAsync(auto, counter);

        // Step 11: Clean up - destroy the deployment using aspire destroy
        await auto.AspireDestroyAsync(counter);
    }

    [Fact]
    public async Task CreateAndDeployToDockerComposeInteractive()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.VerifyPullRequestCliVersionAsync(counter);

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

        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

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

        // Step 10: Verify the frontend responds from inside its own network namespace.
        await VerifyFrontendRespondsAsync(auto, counter);

        // Step 11: Clean up - destroy the deployment using aspire destroy
        await auto.AspireDestroyAsync(counter);
    }

    /// <summary>
    /// Asserts the deployed frontend actually serves HTTP 200 from inside its own network namespace.
    /// </summary>
    private static async Task VerifyFrontendRespondsAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        // The status comparison is what makes this an assertion. An `|| echo 'request-failed'`
        // fallback would hide an unreachable frontend behind a zero exit, and matching only the
        // prompt marker would also accept a 4xx/5xx response, so WaitForSuccessPromptAsync would
        // pass against a broken deployment.
        //
        // `found` carries the outcome out of the loop: an exhausted loop would otherwise end on
        // `sleep 3` and report success. The trailing `test` turns "never got a 200" into a
        // non-zero exit.
        //
        // The sentinel is split so only the shell can assemble it: the typed line reads
        // FRONTEND$(echo _OK) while the executed echo emits FRONTEND_OK. Otherwise
        // WaitUntilTextAsync matches the echoed command still on screen and passes regardless of
        // the response. Same idiom as DockerComposeDeployWithVolumeTests.
        //
        // Retried because the service can still be starting immediately after deploy reports
        // success, which is a likely contributor to the flakiness that had these tests quarantined.
        await auto.TypeAsync(
            "found=0; for i in $(seq 1 20); do " +
            "container=$(docker ps --filter 'name=webfrontend' --format '{{.ID}}' | head -1); " +
            "if [ -n \"$container\" ]; then " +
            "status=$(docker run --rm --network container:$container curlimages/curl:8.12.1 " +
            "-s -o /dev/null -w '%{http_code}' http://localhost:8080 2>/dev/null); " +
            "echo \"HTTP=[$status]\"; " +
            "if [ \"$status\" = \"200\" ]; then found=1; echo \"FRONTEND$(echo _OK)\"; break; fi; " +
            "fi; " +
            "echo \"Attempt $i: waiting for webfrontend...\"; sleep 3; done; " +
            "test \"$found\" = 1");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("FRONTEND_OK", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));
    }
}
