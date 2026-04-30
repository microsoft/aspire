// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire ps command.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class PsCommandTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PsCommandListsRunningAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspirePsTestApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspirePsTestApp/AspirePsTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // First, verify aspire ps shows no running AppHosts
        await auto.TypeAsync("aspire ps");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(SharedCommandStrings.AppHostNotRunning, timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost in the background using aspire start
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Now verify aspire ps shows the running AppHost
        await auto.TypeAsync("aspire ps");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("AspirePsTestApp.AppHost", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Test aspire ps --format json output
        await auto.TypeAsync("aspire ps --format json");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("\"appHostPath\":", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Stop the AppHost using aspire stop
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(StopCommandStrings.AppHostStoppedSuccessfully, timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify aspire ps shows no running AppHosts again after stop
        await auto.TypeAsync("aspire ps");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(SharedCommandStrings.AppHostNotRunning, timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task PsFormatJsonOutputsOnlyJsonToStdout()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        var outputFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "ps-output.json");
        var containerOutputFilePath = CliE2ETestHelpers.ToContainerPath(outputFilePath, workspace);

        // Run aspire ps --format json with stdout redirected to a file.
        // Status messages go to stderr (Spectre.Console spinner, cleared on completion),
        // JSON output goes to stdout (redirected to the file).
        // We only wait for the success prompt since the Spectre status spinner is
        // transient and erased before WaitUntil polling can observe it.
        await auto.TypeAsync($"aspire ps --format json > {containerOutputFilePath}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the file contains only the expected JSON output (empty array).
        var content = File.ReadAllText(outputFilePath).Trim();
        Assert.Equal("[]", content);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
