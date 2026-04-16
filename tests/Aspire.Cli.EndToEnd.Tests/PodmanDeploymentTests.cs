// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI deployment to Docker Compose using Podman as the container runtime.
/// Validates that setting ASPIRE_CONTAINER_RUNTIME=podman flows through to compose operations.
/// Runs Podman inside a privileged Docker helper container so the test does not depend on host Podman state.
/// </summary>
public sealed class PodmanDeploymentTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspirePodmanDeployTest";

    [Fact]
    [OuterloopTest("Requires Docker to run a privileged Podman helper container")]
    public async Task CreateAndDeployToDockerComposeWithPodman()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        using var workspace = TemporaryWorkspace.Create(output);

        var strategy = CliInstallStrategy.Detect();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        using var terminal = CliE2ETestHelpers.CreatePodmanDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        if (strategy.Mode == CliInstallMode.PullRequest)
        {
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        // Step 0: Verify Podman is available inside the helper container.
        await auto.TypeAsync("podman --version || echo 'PODMAN_NOT_FOUND'");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

        // Step 1: Set the container runtime to Podman
        await auto.TypeAsync("export ASPIRE_CONTAINER_RUNTIME=podman");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Create a new Aspire Starter App (no Redis cache)
        await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

        // Step 3: Navigate into the project directory
        await auto.TypeAsync($"cd {ProjectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Add Aspire.Hosting.Docker package using aspire add
        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();

        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromSeconds(180));

        // Step 5: Modify AppHost's main file to add Docker Compose environment
        {
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostDir = Path.Combine(projectDir, $"{ProjectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

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

        // Step 6: Create output directory for deployment artifacts
        await auto.TypeAsync("mkdir -p deploy-output");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 7: Unset ASPIRE_PLAYGROUND before deploy
        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 8: Run aspire deploy with Podman as the container runtime
        await auto.TypeAsync("aspire deploy -o deploy-output --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        // Step 9: Verify containers are running with podman ps
        await auto.TypeAsync("podman ps");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 10: Verify the application is accessible inside the helper container's network namespace.
        await auto.TypeAsync("curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:$(podman ps --format '{{.Ports}}' --filter 'name=webfrontend' | grep -oE '[0-9]+->8080' | head -1 | cut -d- -f1) 2>/dev/null || echo 'request-failed'");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Step 11: Clean up - destroy the deployment using aspire destroy
        await auto.AspireDestroyAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
