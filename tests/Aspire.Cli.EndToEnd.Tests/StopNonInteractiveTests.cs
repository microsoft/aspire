// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for aspire stop in non-interactive mode.
/// Validates fix for https://github.com/microsoft/aspire/issues/14558.
/// </summary>
public sealed class StopNonInteractiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StopNonInteractiveSingleAppHost()
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
        await auto.AspireNewAsync("TestStopApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd TestStopApp/TestStopApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost in the background using aspire start
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen to avoid matching old patterns
        await auto.ClearScreenAsync(counter);

        // Stop the AppHost using aspire stop --non-interactive --project (targets specific AppHost)
        await auto.TypeAsync("aspire stop --non-interactive --project TestStopApp.AppHost.csproj");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(StopCommandStrings.AppHostStoppedSuccessfully, timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen
        await auto.ClearScreenAsync(counter);

        // Verify that stop --non-interactive handles no running AppHosts gracefully
        await auto.TypeAsync("aspire stop --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(SharedCommandStrings.AppHostNotRunning, timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task StopAllAppHostsFromAppHostDirectory()
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

        // Create first project
        await auto.AspireNewAsync("App1", counter);

        // Clear screen before second project creation
        await auto.ClearScreenAsync(counter);

        // Create second project
        await auto.AspireNewAsync("App2", counter);

        // Start first AppHost in background
        await auto.TypeAsync("cd App1/App1.AppHost && aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen before starting second apphost
        await auto.ClearScreenAsync(counter);

        // Navigate back and start second AppHost in background
        await auto.TypeAsync("cd ../../App2/App2.AppHost && aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen
        await auto.ClearScreenAsync(counter);

        // Stop all AppHosts from within an AppHost directory using --non-interactive --all
        await auto.TypeAsync("aspire stop --non-interactive --all");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(StopCommandStrings.AppHostStoppedSuccessfully, timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen
        await auto.ClearScreenAsync(counter);

        // Verify no AppHosts are running
        await auto.TypeAsync("aspire stop --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(SharedCommandStrings.AppHostNotRunning, timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task StopAllAppHostsFromUnrelatedDirectory()
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

        // Create first project
        await auto.AspireNewAsync("App1", counter);

        // Clear screen before second project creation
        await auto.ClearScreenAsync(counter);

        // Create second project
        await auto.AspireNewAsync("App2", counter);

        // Start first AppHost in background
        await auto.TypeAsync("cd App1/App1.AppHost && aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen before starting second apphost
        await auto.ClearScreenAsync(counter);

        // Navigate back and start second AppHost in background
        await auto.TypeAsync("cd ../../App2/App2.AppHost && aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Navigate to workspace root (unrelated to any AppHost directory)
        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(workspace.WorkspaceRoot.FullName, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen
        await auto.ClearScreenAsync(counter);

        // Stop all AppHosts from an unrelated directory using --non-interactive --all
        await auto.TypeAsync("aspire stop --non-interactive --all");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(StopCommandStrings.AppHostStoppedSuccessfully, timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen
        await auto.ClearScreenAsync(counter);

        // Verify no AppHosts are running
        await auto.TypeAsync("aspire stop --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(SharedCommandStrings.AppHostNotRunning, timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task StopNonInteractiveMultipleAppHostsShowsError()
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

        // Create first project
        await auto.AspireNewAsync("App1", counter);

        // Clear screen before second project creation
        await auto.ClearScreenAsync(counter);

        // Create second project
        await auto.AspireNewAsync("App2", counter);

        // Start first AppHost in background
        await auto.TypeAsync("cd App1/App1.AppHost && aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen before starting second apphost
        await auto.ClearScreenAsync(counter);

        // Navigate back and start second AppHost in background
        await auto.TypeAsync("cd ../../App2/App2.AppHost && aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Navigate to workspace root
        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(workspace.WorkspaceRoot.FullName, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear screen
        await auto.ClearScreenAsync(counter);

        // Try to stop in non-interactive mode - should get an error about multiple AppHosts
        await auto.TypeAsync("aspire stop --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(
            string.Format(CultureInfo.InvariantCulture, StopCommandStrings.MultipleAppHostsNonInteractive, "--apphost", "--all"),
            timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Clear screen
        await auto.ClearScreenAsync(counter);

        // Now use --all to stop all AppHosts
        await auto.TypeAsync("aspire stop --all");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(StopCommandStrings.AppHostStoppedSuccessfully, timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
