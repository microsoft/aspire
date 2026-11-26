// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI stop --wait, which returns only after DCP has torn down the AppHost resources.
/// </summary>
public sealed class StopWaitTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task StopWaitRemovesFixedNameContainerBeforeReturning()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("This test validates unreleased stop --wait behavior. Build a local Aspire CLI bundle or run in CI so the test uses current PR bits instead of the GA CLI.");
        }

        var projectSuffix = Guid.NewGuid().ToString("N")[..6].ToLowerInvariant();
        var projectName = $"StopWait{projectSuffix}";
        var resourceName = $"waitcache{projectSuffix}";
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);

        var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "apphost.cs");
        var content = File.ReadAllText(appHostFilePath);
        var sdkLine = content.Split('\n', 2)[0].TrimEnd('\r');

        // A transient container with a fixed name is the shape that conflicts on an immediate restart
        // when stop returns before DCP has removed the previous container.
        File.WriteAllText(appHostFilePath, $$"""
            {{sdkLine}}

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddContainer("{{resourceName}}", "redis")
                .WithContainerName("{{resourceName}}");

            builder.Build().Run();
            """);

        await auto.RunCommandAsync($"cd {projectName}", counter);

        await auto.AspireStartAsync(counter, TimeSpan.FromMinutes(5), skipDashboardCheck: true);
        await auto.RunCommandAsync($"aspire wait {resourceName} --status up --timeout 300", counter, TimeSpan.FromMinutes(6));
        await auto.RunCommandAsync($"docker ps -a --format '{{{{.Names}}}}' | grep -qx '{resourceName}'", counter);

        await auto.TypeAsync("aspire stop --wait");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(5));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        // Check once, without polling: stop --wait must not return until the container is gone.
        // The listing is captured first so a failing `docker ps` fails the command instead of
        // looking like an empty listing.
        await auto.RunCommandAsync(
            GetContainerAbsentCommand(resourceName),
            counter);

        await auto.AspireStartAsync(counter, TimeSpan.FromMinutes(5), skipDashboardCheck: true);
        await auto.RunCommandAsync($"aspire wait {resourceName} --status up --timeout 300", counter, TimeSpan.FromMinutes(6));

        // Observe briefly after restart so a container that starts and then fails because of a name
        // conflict with a leftover container is caught, not just the initial Running transition.
        await auto.RunCommandAsync(
            $"sleep 10; test \"$(docker inspect --format '{{{{.State.Running}}}}' {resourceName})\" = true",
            counter,
            TimeSpan.FromMinutes(1));
        await auto.RunCommandAsync($"aspire wait {resourceName} --status up --timeout 30", counter, TimeSpan.FromMinutes(1));

        await auto.TypeAsync("aspire stop --wait");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(5));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        await auto.RunCommandAsync(
            GetContainerAbsentCommand(resourceName),
            counter);
    }

    private static string GetContainerAbsentCommand(string containerName) =>
        $"names=\"$(docker ps -a --format '{{{{.Names}}}}')\" && if printf '%s\\n' \"$names\" | grep -qx '{containerName}'; then docker ps -a --filter name={containerName}; false; fi";
}
