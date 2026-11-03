// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the Go Empty AppHost template (aspire-go-empty).
/// Validates that aspire new creates a Go AppHost project that restores, compiles
/// and starts, both through the interactive template picker and the direct template command.
/// </summary>
public sealed class GoEmptyAppHostTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunGoEmptyAppHostProjectFromTemplatePicker()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CreateTerminal(repoRoot, strategy, workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await auto.AspireNewAsync("GoEmptyApp", counter, template: AspireTemplate.GoEmptyAppHost);

        await RestoreBuildAndRunAsync(auto, counter, workspace, "GoEmptyApp");
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunGoEmptyAppHostProjectFromDirectTemplate()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CreateTerminal(repoRoot, strategy, workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await auto.AspireNewGoEmptyAppHostAsync("GoDirectApp", counter);

        await RestoreBuildAndRunAsync(auto, counter, workspace, "GoDirectApp");
    }

    private Hex1bTerminal CreateTerminal(string repoRoot, CliInstallStrategy strategy, TemporaryWorkspace workspace) =>
        CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            mountDockerSocket: true,
            workspace: workspace);

    private static async Task RestoreBuildAndRunAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace,
        string projectName)
    {
        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        Assert.True(File.Exists(Path.Combine(projectRoot, "apphost.go")), $"Expected apphost.go in {projectRoot}");
        Assert.True(File.Exists(Path.Combine(projectRoot, "go.mod")), $"Expected go.mod in {projectRoot}");

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

        // The workspace is not a git repository, so VCS stamping would fail the build.
        await auto.RunCommandAsync("go build -buildvcs=false ./...", counter, TimeSpan.FromMinutes(3));

        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(3));
        await auto.AspireStopAsync(counter);
    }
}
