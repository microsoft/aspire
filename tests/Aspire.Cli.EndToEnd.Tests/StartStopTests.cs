// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
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
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"StarterApp_{projectSuffix}";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

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
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);

        // Docker network cleanup can lag behind aspire stop on contended CI runners.
        await auto.ExecuteCommandUntilOutputAsync(counter, $"docker network ls --format json | grep -i -- '{projectName}' | wc -l", "0", timeout: TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task StopWithNoRunningAppHostExitsSuccessfully()
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

        // Run aspire stop with no running AppHost - should exit with code 0
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task LinkedWorktreeExplicitIsolationAndPrimaryStopLeavesItRunning()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"WorktreeIsolation_{projectSuffix}";
        var linkedWorktreeName = $"{projectName} linked";
        var linkedWorktreeRelativePath = $".worktrees/{linkedWorktreeName}";

        var workspace = TemporaryWorkspace.Create(output);
        var containerWorkspace = $"/workspace/{workspace.WorkspaceRoot.Name}";
        var primaryCheckout = $"{containerWorkspace}/{projectName}";
        var linkedCheckout = $"{primaryCheckout}/{linkedWorktreeRelativePath}";

        var bothInstancesPath = Path.Combine(workspace.WorkspaceRoot.FullName, "both-instances.json");
        var remainingInstancePath = Path.Combine(workspace.WorkspaceRoot.FullName, "remaining-instance.json");
        var stoppedInstancesPath = Path.Combine(workspace.WorkspaceRoot.FullName, "stopped-instances.json");
        var bothInstancesContainerPath = CliE2ETestHelpers.ToContainerPath(bothInstancesPath, workspace);
        var remainingInstanceContainerPath = CliE2ETestHelpers.ToContainerPath(remainingInstancePath, workspace);
        var stoppedInstancesContainerPath = CliE2ETestHelpers.ToContainerPath(stoppedInstancesPath, workspace);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter);

        // Create both checkouts through git inside the container. A host-created worktree would
        // write absolute .git admin paths in the host namespace, which are invalid in this mount.
        await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(projectName)}", counter);
        await auto.RunCommandAsync(
            "git init -b main && " +
            "git config user.name 'Aspire CLI E2E' && " +
            "git config user.email 'aspire-cli-e2e@example.invalid' && " +
            "git add . && " +
            "git commit -m 'Initial AppHost'",
            counter);

        Exception? primaryFailure = null;
        try
        {
            // Direct CLI launches intentionally remain explicit-only. Isolate the linked checkout
            // while this single container keeps ~/.aspire/backchannels shared for structured discovery.
            await auto.AspireStartAsync(counter);
            await auto.RunCommandAsync(
                $"git worktree add {AspireCliShellCommandHelpers.QuoteBashArg(linkedWorktreeRelativePath)} -b linked-worktree",
                counter);
            await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(linkedCheckout)}", counter);
            await auto.AspireStartAsync(counter, isolated: true);

            await auto.RunCommandAsync(
                $"aspire ps --format json > {AspireCliShellCommandHelpers.QuoteBashArg(bothInstancesContainerPath)}",
                counter,
                TimeSpan.FromMinutes(1));

            var bothInstances = ReadRunningAppHosts(bothInstancesPath);
            Assert.Equal(2, bothInstances.Count);

            var primaryInstance = Assert.Single(bothInstances, instance =>
                string.Equals(Path.GetDirectoryName(instance.AppHostPath), primaryCheckout, StringComparison.Ordinal));
            var linkedInstance = Assert.Single(bothInstances, instance =>
                string.Equals(Path.GetDirectoryName(instance.AppHostPath), linkedCheckout, StringComparison.Ordinal));

            Assert.NotEqual(primaryInstance.AppHostPid, linkedInstance.AppHostPid);
            Assert.NotEqual(primaryInstance.DashboardUri.Port, linkedInstance.DashboardUri.Port);

            // The linked checkout is nested beneath the primary workspace, so path containment
            // alone would stop both. Worktree-aware scoping must select only the primary instance.
            await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(primaryCheckout)}", counter);
            await auto.AspireStopAsync(counter);
            await auto.RunCommandAsync(
                $"aspire ps --format json > {AspireCliShellCommandHelpers.QuoteBashArg(remainingInstanceContainerPath)}",
                counter,
                TimeSpan.FromMinutes(1));

            var remainingInstance = Assert.Single(ReadRunningAppHosts(remainingInstancePath));
            Assert.Equal(linkedInstance.AppHostPath, remainingInstance.AppHostPath);
            Assert.Equal(linkedInstance.AppHostPid, remainingInstance.AppHostPid);
            Assert.Equal(linkedInstance.DashboardUri, remainingInstance.DashboardUri);

            await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(linkedCheckout)}", counter);
            await auto.AspireStopAsync(counter);
            await auto.RunCommandAsync(
                $"aspire ps --format json > {AspireCliShellCommandHelpers.QuoteBashArg(stoppedInstancesContainerPath)}",
                counter,
                TimeSpan.FromMinutes(1));

            Assert.Empty(ReadRunningAppHosts(stoppedInstancesPath));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            await CleanupLinkedWorktreeAppHostsAsync(auto, counter, projectName);
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is null)
            {
                throw;
            }

            // Keep the proof assertion as the test failure while making a teardown problem visible
            // in both xUnit output and the exception data captured in diagnostics.
            output.WriteLine($"Linked-worktree cleanup also failed: {cleanupFailure}");
            primaryFailure.Data["LinkedWorktreeCleanupFailure"] = cleanupFailure.ToString();
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task StartLaunchesDetachedCliAndAppHostOutsideLauncherProcessGroupAndSession()
    {
        // This validates that `aspire start` detaches both the background CLI and AppHost from the
        // invoking shell's process group and session. The Python script reads the JSON emitted by
        // `aspire start`, compares each started process with the launcher PGID/SID captured below,
        // and prints a success marker only after both processes are verified.
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"DetachedProcessGroup_{projectSuffix}";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter);

        await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(projectName)}", counter);

        // The Python script prints this marker only after it verifies both started processes are
        // outside the launcher's process group and session. Waiting for this marker makes the
        // success condition explicit instead of relying only on the script's zero exit code.
        const string assertionSuccessMarker = "detached process group and session assertions passed";
        var assertionScript = Path.Combine(workspace.WorkspaceRoot.FullName, "assert-detached-process-group.py");
        File.WriteAllText(assertionScript, $$"""
import json
import os
import subprocess
import sys

with open("/tmp/aspire-start.json", encoding="utf-8") as start_file:
    start_info = json.load(start_file)

launcher_pgid = os.environ["ASPIRE_E2E_LAUNCHER_PGID"]
launcher_sid = os.environ["ASPIRE_E2E_LAUNCHER_SID"]

def get_process_group_and_session(pid):
    output = subprocess.check_output(
        ["ps", "-o", "pgid=", "-o", "sid=", "-p", str(pid)],
        text=True)
    fields = output.split()
    if len(fields) != 2:
        raise SystemExit(f"Unexpected ps output for PID {pid}: {output!r}")
    return fields[0], fields[1]

for name, pid in (("detached CLI", start_info["cliPid"]), ("AppHost", start_info["appHostPid"])):
    pgid, sid = get_process_group_and_session(pid)
    print(f"{name} PID {pid}: PGID={pgid} SID={sid}; launcher PGID={launcher_pgid} SID={launcher_sid}")
    if pgid == launcher_pgid:
        raise SystemExit(f"{name} PID {pid} is still in the launcher's process group {launcher_pgid}")
    if sid == launcher_sid:
        raise SystemExit(f"{name} PID {pid} is still in the launcher's session {launcher_sid}")

print("{{assertionSuccessMarker}}")
""");

        var containerAssertionScript = CliE2ETestHelpers.ToContainerPath(assertionScript, workspace);

        // Capture the invoking shell's process group and session. The detached CLI and AppHost must
        // not share either value or Ctrl+C/terminal teardown can still affect the started app.
        await auto.RunCommandAsync("export ASPIRE_E2E_LAUNCHER_PGID=$(ps -o pgid= -p $$ | tr -d ' ') ASPIRE_E2E_LAUNCHER_SID=$(ps -o sid= -p $$ | tr -d ' ')", counter);
        await auto.RunCommandAsync("aspire start --format json > /tmp/aspire-start.json", counter, TimeSpan.FromMinutes(2));
        await auto.TypeAsync($"python3 {AspireCliShellCommandHelpers.QuoteBashArg(containerAssertionScript)}");
        await auto.EnterAsync();

        var assertionSuccessSearcher = new CellPatternSearcher()
            .Find(assertionSuccessMarker);
        var assertionErrorSearcher = new CellPatternSearcher()
            .FindPattern(counter.Value.ToString())
            .RightText(" ERR:");
        await auto.WaitUntilAsync(
            snapshot => assertionSuccessSearcher.Search(snapshot).Count > 0 ||
                assertionErrorSearcher.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromSeconds(30),
            description: "detached process group assertion success marker or failure prompt");
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    public async Task AddPackageWhileAppHostRunningDetached()
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
    }

    [Fact]
    public async Task AddPackageInteractiveWhileAppHostRunningDetached()
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
    }

    private static async Task CleanupLinkedWorktreeAppHostsAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string projectName)
    {
        // A detached CLI can still be finishing startup when an earlier proof assertion fails.
        // Retry the supported stop path, then independently prove that the CLI registry and Docker
        // network no longer contain anything from this AppHost before allowing teardown to pass.
        await auto.RunCommandAsync(
            "stopped=0; " +
            "for attempt in 1 2 3; do " +
            "if aspire stop --all; then stopped=1; break; " +
            "else echo \"aspire stop --all attempt $attempt failed\" >&2; sleep 2; fi; " +
            "done; " +
            "[ \"$stopped\" -eq 1 ]",
            counter,
            TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync(
            "stopped=0; " +
            "for attempt in $(seq 1 24); do " +
            "if aspire ps --format json | tr -d '[:space:]' | grep -qx '\\[\\]'; then stopped=1; break; fi; " +
            "sleep 5; " +
            "done; " +
            "if [ \"$stopped\" -eq 1 ]; then true; else aspire ps --format json; false; fi",
            counter,
            TimeSpan.FromMinutes(3));

        // Docker network deletion is asynchronous after the last DCP process exits. Require five
        // consecutive empty samples so a briefly empty list cannot let teardown race a late network
        // creation. Capture `docker network ls` before testing its output so command failure cannot
        // be mistaken for an empty list. First force one short, subshell-scoped retry and then run
        // another command, proving the persistent Hex1b shell survived the nonzero sample.
        await auto.ExecuteCommandUntilOutputAsync(
            counter,
            "r=$((r+1));([ $r -gt 1 ]||exit 1;echo retry-sampled)",
            "retry-sampled",
            timeout: TimeSpan.FromSeconds(30),
            retryInterval: TimeSpan.FromSeconds(1));
        await auto.RunCommandAsync(
            "[ \"$r\" -ge 2 ]&&echo shell-usable-after-retry&&unset r",
            counter);

        // Keep the stabilization command compact because the retry helper locates output relative
        // to the command row, which a wrapped Hex1b command obscures. The subshell scopes every
        // nonzero exit to this sample rather than terminating the persistent interactive shell.
        var quotedProjectName = AspireCliShellCommandHelpers.QuoteBashArg(projectName);
        await auto.ExecuteCommandUntilOutputAsync(
            counter,
            "(for i in {1..5};do " +
            $"n=$(docker network ls -q -f name={quotedProjectName})||exit 2;" +
            "[ -z \"$n\" ]||exit 1;sleep 1;done;echo stable-empty)",
            "stable-empty",
            timeout: TimeSpan.FromMinutes(5),
            retryInterval: TimeSpan.FromSeconds(8));
    }

    private static IReadOnlyList<RunningAppHostInfo> ReadRunningAppHosts(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        var instances = new List<RunningAppHostInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            // `aspire ps --format json` emits entries shaped like:
            // { "appHostPath": "/workspace/app/app.csproj", "appHostPid": 123,
            //   "status": "running", "dashboardUrl": "https://localhost:17234/login?t=..." }
            var appHostPath = element.GetProperty("appHostPath").GetString();
            var appHostPid = element.GetProperty("appHostPid").GetInt32();
            var status = element.GetProperty("status").GetString();
            var dashboardUrl = element.GetProperty("dashboardUrl").GetString();

            Assert.False(string.IsNullOrWhiteSpace(appHostPath));
            Assert.True(appHostPid > 0);
            Assert.Equal("running", status);
            Assert.True(
                Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var dashboardUri) && dashboardUri.Port > 0,
                $"Expected a dashboard URL with an explicit port, got '{dashboardUrl}'.");

            instances.Add(new RunningAppHostInfo(appHostPath, appHostPid, dashboardUri!));
        }

        return instances;
    }

    private sealed record RunningAppHostInfo(string AppHostPath, int AppHostPid, Uri DashboardUri);
}
