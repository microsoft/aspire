// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the Go starter template (aspire-go-starter).
/// Validates that aspire new creates a Go API + Redis project with a restored Go SDK
/// and that aspire start runs it successfully.
/// </summary>
public sealed class GoStarterTemplateTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunGoStarterProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            mountDockerSocket: true,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await auto.AspireNewAsync("GoStarterApp", counter, template: AspireTemplate.GoStarter);

        // The starter template restores the generated Go SDK as part of aspire new.
        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "GoStarterApp");
        var aspireModulePath = Path.Combine(projectRoot, ".aspire", "modules", "aspire.go");
        Assert.True(File.Exists(aspireModulePath), $"Expected generated Go SDK file not found: {aspireModulePath}");

        await auto.TypeAsync("cd GoStarterApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // The workspace is not a git repository, so VCS stamping would fail the build.
        await auto.RunCommandAsync("go build -buildvcs=false ./...", counter, TimeSpan.FromMinutes(3));

        // The Go API runs `go mod tidy` before it starts, which downloads the OpenTelemetry modules.
        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(4));
        await auto.AssertResourcesExistAsync(counter, "cache", "api");
        await auto.AspireStopAsync(counter);
    }
}
