// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI stop --force cleanup.
/// </summary>
public sealed class StopForceTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task StopForceCleansUpPersistentContainer()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("This test validates unreleased stop --force cleanup behavior. Build a local Aspire CLI bundle or run in CI so the test uses current PR bits instead of the GA CLI.");
        }

        var projectSuffix = Guid.NewGuid().ToString("N")[..6].ToLowerInvariant();
        var projectName = $"StopForce{projectSuffix}";
        var resourceName = $"cleanupcache{projectSuffix}";
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

        File.WriteAllText(appHostFilePath, $$"""
            {{sdkLine}}

            #pragma warning disable ASPIREPERSISTENCE001

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddContainer("{{resourceName}}", "redis")
                .WithContainerName("{{resourceName}}")
                .WithPersistentLifetime();

            builder.Build().Run();
            """);

        await auto.TypeAsync($"cd {projectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter, TimeSpan.FromMinutes(5), skipDashboardCheck: true);

        await auto.TypeAsync($"found=0; for i in $(seq 1 24); do if docker ps -a --format '{{{{.Names}}}}' | grep -qx '{resourceName}'; then found=1; break; fi; sleep 5; done; if [ \"$found\" -eq 1 ]; then true; else docker ps -a; false; fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("aspire stop --force");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        await auto.TypeAsync($"removed=0; for i in $(seq 1 24); do if ! docker ps -a --format '{{{{.Names}}}}' | grep -qx '{resourceName}'; then removed=1; break; fi; sleep 5; done; if [ \"$removed\" -eq 1 ]; then true; else docker ps -a --filter name={resourceName}; false; fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }
}
