// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI start and stop commands (background/detached mode).
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class StartStopTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/16191")]
    public async Task CreateStartAndStopAspireProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"StarterApp_{projectSuffix}";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            // Prepare Docker environment (prompt counting, umask, env vars)
            await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);

            // Install the Aspire CLI
            await auto.InstallAspireCliAsync(strategy, counter);

            // Create a new project using aspire new
            await auto.AspireNewAsync(projectName, counter);

            // Navigate to the AppHost directory
            await auto.TypeAsync($"cd {projectName}/{projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Start the AppHost in the background using aspire start
            await auto.TypeAsync("aspire start");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Stop the AppHost using aspire stop
            await auto.TypeAsync("aspire stop");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.ClearScreenAsync(counter);

            // Docker network cleanup can lag behind aspire stop on contended CI runners.
            await auto.ExecuteCommandUntilOutputAsync(counter, $"docker network ls --format json | grep -i -- '{projectName}' | wc -l", "0", timeout: TimeSpan.FromMinutes(5));
        }
        catch
        {
            testBodyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                await auto.CaptureAspireDiagnosticsAsync(counter, workspace);
            }
            catch { } // Best effort

            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch
            {
                if (!testBodyFailed)
                {
                    throw;
                }
            }
        }
    }

    [Fact]
    public async Task StopWithNoRunningAppHostExitsSuccessfully()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI
        await auto.InstallAspireCliAsync(strategy, counter);

        // Run aspire stop with no running AppHost - should exit with code 0
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task AddPackageWhileAppHostRunningDetached()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireAddTestApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireAddTestApp/AspireAddTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost in detached mode (locks the project file)
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Add a package while the AppHost is running - this should auto-stop the
        // running instance before modifying the project, then succeed.
        // --non-interactive skips the version selection prompt.
        await auto.TypeAsync("aspire add mongodb --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("was added successfully.", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: stop if still running (the add command may have stopped it)
        // aspire stop should return successfully whether the app host is still running
        // or was already stopped by the preceding add command.
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(1));

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task AddPackageInteractiveWhileAppHostRunningDetached()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireAddInteractiveApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireAddInteractiveApp/AspireAddInteractiveApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost in detached mode (locks the project file)
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire add interactively (no integration argument) while AppHost is running.
        // This exercises the interactive package selection flow and verifies the
        // running instance is auto-stopped before modifying the project.
        await auto.TypeAsync("aspire add");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(AddCommandStrings.SelectAnIntegrationToAdd, timeout: TimeSpan.FromMinutes(1));
        await auto.TypeAsync("mongodb"); // type to filter the list
        await auto.EnterAsync(); // select the filtered result
        var waitingForVersionSelection = false;
        await auto.WaitUntilAsync(snapshot =>
        {
            waitingForVersionSelection = snapshot.ContainsText("Select a version of");
            return waitingForVersionSelection || snapshot.ContainsText("was added successfully.");
        }, timeout: TimeSpan.FromSeconds(30), description: "version prompt or add success");

        if (waitingForVersionSelection)
        {
            await auto.EnterAsync(); // Accept the default version
        }

        await auto.WaitUntilTextAsync("was added successfully.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: stop if still running
        // aspire stop should return successfully whether the app host is still running
        // or was already stopped by the preceding add command.
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(1));

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
