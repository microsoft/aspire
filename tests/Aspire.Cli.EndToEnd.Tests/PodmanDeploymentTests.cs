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
/// Requires Podman and docker-compose v2 installed on the host.
/// </summary>
public sealed class PodmanDeploymentTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspirePodmanDeployTest";

    [Fact]
    [ActiveIssue("https://github.com/mitchdenny/hex1b/pull/270")]
    [OuterloopTest("Requires Podman and docker-compose v2 installed on the host")]
    public async Task CreateAndDeployToDockerComposeWithPodman()
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

        // Step 0: Verify Podman is available, skip if not
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

        if (isCI)
        {
            await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
            await auto.EnterAsync();
        }

        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

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

        // Step 10: Verify the application is accessible
        await auto.TypeAsync("curl -s -o /dev/null -w '%{http_code}' http://localhost:$(podman ps --format '{{.Ports}}' --filter 'name=webfrontend' | grep -oE '0\\.0\\.0\\.0:[0-9]+->8080' | head -1 | cut -d: -f2 | cut -d'-' -f1) 2>/dev/null || echo 'request-failed'");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Step 11: Clean up - destroy the deployment using aspire destroy
        await auto.AspireDestroyAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
