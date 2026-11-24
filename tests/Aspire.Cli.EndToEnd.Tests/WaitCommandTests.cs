// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI wait command.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class WaitCommandTests(ITestOutputHelper output)
{
    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/14993")]
    public async Task CreateStartWaitAndStopAspireProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireWaitApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireWaitApp/AspireWaitApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost in the background using aspire start
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for the webfrontend resource to be up (running).
        // Use a longer timeout in Docker-in-Docker where container startup is slower.
        await auto.TypeAsync("aspire wait webfrontend --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        // Stop the AppHost using aspire stop
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task WaitForMultipleResourcesAndAllReportsBatchOutcomes()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"WaitBatchApp_{projectSuffix}";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);

        await auto.TypeAsync($"cd {projectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Replace the generated apphost.cs with executables run through /bin/sh. Executables keep the
        // scenario free of Docker, so it exercises the AppHost inventory handshake, batch waits and
        // JSON output rather than container startup time.
        var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "apphost.cs");
        var content = File.ReadAllText(appHostFilePath);

        // Extract the first line (#:sdk directive) so the replacement uses the same SDK version.
        var sdkLine = content.Split('\n', 2)[0].TrimEnd('\r');

        var newContent = $$"""
            {{sdkLine}}

            var builder = DistributedApplication.CreateBuilder(args);

            // Long-running service without health checks: it is healthy once it is running.
            builder.AddExecutable("svc", "/bin/sh", ".", "-c", "sleep 3600");

            // Run-to-completion job: exit code 0 is reported as "completed".
            builder.AddExecutable("job", "/bin/sh", ".", "-c", "exit 0");

            // Explicit-start job that fails when started. `wait --all` skips it while it is not started,
            // and the test starts it later to produce a failing batch.
            builder.AddExecutable("failjob", "/bin/sh", ".", "-c", "exit 3")
                .WithExplicitStart();

            builder.Build().Run();
            """;

        File.WriteAllText(appHostFilePath, newContent);

        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Waiting for several named resources succeeds when the service is healthy and the job exited 0.
        await auto.RunCommandAsync("aspire wait svc job --timeout 120", counter, TimeSpan.FromMinutes(3));

        // --all selects every visible resource, skips the not-started explicit-start resource,
        // and writes only the JSON document to stdout.
        await auto.RunCommandAsync("aspire wait --all --format json --timeout 120 > wait-all.json 2> wait-all.err", counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("cat wait-all.json", counter);
        await auto.RunCommandAsync(
            """jq -e '.outcome == "success" and .status == "healthy" and ([.resources[] | {key: .displayName, value: .outcome}] | from_entries) == {"svc": "success", "job": "completed", "failjob": "skipped"}' wait-all.json""",
            counter);
        await auto.RunCommandAsync(
            """jq -e '(.resources[] | select(.displayName == "job") | .exitCode) == 0' wait-all.json""",
            counter);

        await auto.RunCommandAsync("aspire resource failjob start", counter, TimeSpan.FromMinutes(1));

        // An executable without health checks is healthy while it runs, so a "healthy" wait that observes
        // the job before it exits succeeds. A "down" wait succeeds for any exit code and makes the failing
        // batch below deterministic.
        await auto.RunCommandAsync("aspire wait failjob --status down --timeout 120", counter, TimeSpan.FromMinutes(3));

        // A nonzero exit fails the batch with WaitResourceFailed. The service may finish before or
        // after the failure (success or cancelled), so only the failing resource's outcome is asserted.
        await auto.RunCommandAsync(
            $"aspire wait svc failjob --format json --timeout 120 > wait-failing.json 2> wait-failing.err; WAIT_EXIT=$?; echo \"wait exit code: $WAIT_EXIT\"; test $WAIT_EXIT -eq {CliExitCodes.WaitResourceFailed}",
            counter,
            TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("cat wait-failing.json", counter);
        await auto.RunCommandAsync(
            """jq -e '.outcome == "failure" and (.resources[] | select(.displayName == "failjob") | .outcome == "failure" and .exitCode == 3)' wait-failing.json""",
            counter);

        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
