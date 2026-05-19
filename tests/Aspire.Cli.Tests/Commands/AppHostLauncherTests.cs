// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Layout;
using Aspire.Cli.Processes;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class AppHostLauncherTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void GetDetachedFailureMessage_ReturnsBuildSpecificMessage_ForBuildFailureExitCode()
    {
        var message = AppHostLauncher.GetDetachedFailureMessage(CliExitCodes.FailedToBuildArtifacts);

        Assert.Equal(RunCommandStrings.AppHostFailedToBuild, message);
    }

    [Fact]
    public void GetDetachedFailureMessage_ReturnsExitCodeMessage_ForUnknownExitCode()
    {
        var message = AppHostLauncher.GetDetachedFailureMessage(123);

        Assert.Contains("123", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CliExitCodes.Success, true)]
    [InlineData(CliExitCodes.FailedToDotnetRunAppHost, false)]
    public void IsSuccessfulDetachedEarlyExit_OnlyTreatsZeroAsSuccess(int exitCode, bool expected)
    {
        var result = AppHostLauncher.IsSuccessfulDetachedEarlyExit(exitCode);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GenerateChildLogFilePath_UsesDetachChildNamingWithoutProcessId()
    {
        var logsDirectory = Path.Combine(Path.GetTempPath(), "aspire-cli-tests");
        var now = new DateTimeOffset(2026, 02, 12, 18, 00, 00, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var path = AppHostLauncher.GenerateChildLogFilePath(logsDirectory, timeProvider);
        var fileName = Path.GetFileName(path);

        Assert.StartsWith(logsDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("cli_20260212T180000000_detach-child_", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".log", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain($"_{Environment.ProcessId}", fileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForAppHostReadyAsync_ReturnsNullWhenReadinessIsUnavailable()
    {
        var connection = new TestAppHostAuxiliaryBackchannel();

        var ready = await AppHostLauncher.WaitForAppHostReadyAsync(connection, CancellationToken.None);

        Assert.Null(ready);
    }

    [Fact]
    public async Task WaitForAppHostReadyAsync_PropagatesReadinessFailures()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsV3 = true,
            WaitForAppHostReadyHandler = _ => throw new IOException("connection lost")
        };

        var exception = await Assert.ThrowsAsync<IOException>(() => AppHostLauncher.WaitForAppHostReadyAsync(connection, CancellationToken.None));
        Assert.Equal("connection lost", exception.Message);
    }

    [Fact]
    public async Task WaitForLegacyDetachedStartupStabilityAsync_ReturnsFalseWhenChildExitsDuringStabilityWindow()
    {
        var stable = await AppHostLauncher.WaitForLegacyDetachedStartupStabilityAsync(
            new TestAppHostAuxiliaryBackchannel { SupportsV2 = false },
            Task.CompletedTask,
            TimeSpan.FromSeconds(120),
            TimeProvider.System,
            CancellationToken.None);

        Assert.False(stable);
    }

    [Fact]
    public async Task WaitForLegacyDetachedStartupStabilityAsync_ReturnsTrueWhenChildStaysAliveForStabilityWindow()
    {
        var childExitTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        var stable = await AppHostLauncher.WaitForLegacyDetachedStartupStabilityAsync(
            new TestAppHostAuxiliaryBackchannel { SupportsV2 = false },
            childExitTask,
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            CancellationToken.None);

        Assert.True(stable);
    }

    [Fact]
    public async Task LaunchDetachedAsync_WaitsForReadinessRpcBeforeReportingSuccess()
    {
        using var harness = AppHostLauncherHarness.Create(outputHelper);
        var readiness = new TaskCompletionSource<WaitForAppHostReadyResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.AddConnection(new TestAppHostAuxiliaryBackchannel
        {
            SupportsV3 = true,
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "https://localhost:18888/login?t=test" },
            WaitForAppHostReadyHandler = ct => readiness.Task.WaitAsync(ct)
        });
        harness.ProcessLauncher.Mode = TestDetachedProcessLauncher.ChildProcessMode.StayAlive;

        var launchTask = harness.Launcher.LaunchDetachedAsync(
            harness.AppHostFile,
            format: null,
            isolated: false,
            isExtensionHost: false,
            waitForDebugger: false,
            globalArgs: [],
            additionalArgs: [],
            stopAfterLaunchDelay: null,
            CancellationToken.None);

        await harness.ProcessLauncher.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(launchTask, await Task.WhenAny(launchTask, Task.Delay(TimeSpan.FromMilliseconds(100))));

        readiness.SetResult(new WaitForAppHostReadyResponse { IsReady = true });

        var result = await launchTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains(RunCommandStrings.StartingAppHostInBackground, harness.InteractionService.ShownStatuses);
        Assert.Empty(harness.InteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task LaunchDetachedAsync_ReportsFailureWhenReadinessWaitIsInterruptedByChildExit()
    {
        using var harness = AppHostLauncherHarness.Create(outputHelper);
        harness.AddConnection(new TestAppHostAuxiliaryBackchannel
        {
            SupportsV3 = true,
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "https://localhost:18888/login?t=test" },
            WaitForAppHostReadyHandler = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }
        });
        harness.ProcessLauncher.Mode = TestDetachedProcessLauncher.ChildProcessMode.ExitWithFailure;
        harness.ProcessLauncher.ChildLogLines =
        [
            "[2026-05-15 17:07:30.501] [INFO] [AppHost] apphost.ts(5,22): error TS1109: Expression expected.",
            "[2026-05-15 17:07:30.521] [FAIL] [GuestAppHostProject] TypeScript (Node.js) apphost exited with code 2"
        ];

        var result = await harness.Launcher.LaunchDetachedAsync(
            harness.AppHostFile,
            format: null,
            isolated: false,
            isExtensionHost: false,
            waitForDebugger: false,
            globalArgs: [],
            additionalArgs: [],
            stopAfterLaunchDelay: null,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, result.ExitCode);
        Assert.Contains(RunCommandStrings.FailedToStartAppHost, harness.InteractionService.DisplayedErrors);
        Assert.Contains(RunCommandStrings.AppHostFailedToBuild, harness.InteractionService.DisplayedErrors);
        Assert.Contains(harness.InteractionService.DisplayedMessages, m => m.Message == $"{RunCommandStrings.RecentAppHostStartupOutput}:");
        Assert.Contains(harness.InteractionService.DisplayedLines, line => line.Line == "apphost.ts(5,22): error TS1109: Expression expected.");
    }

    [Fact]
    public async Task WaitForLegacyDetachedStartupStabilityAsync_UsesV2ResourceSnapshotProbeWhenAvailable()
    {
        var probeCount = 0;
        var childExitTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsV2 = true,
            GetResourceSnapshotsHandler = _ =>
            {
                probeCount++;
                return Task.FromResult<List<ResourceSnapshot>>([]);
            }
        };

        var stable = await AppHostLauncher.WaitForLegacyDetachedStartupStabilityAsync(
            connection,
            childExitTask,
            TimeSpan.FromSeconds(120),
            TimeProvider.System,
            CancellationToken.None);

        Assert.True(stable);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task WaitForLegacyDetachedStartupStabilityAsync_RetriesV2ProbeUntilChildExits()
    {
        var probeCount = 0;
        using var childProcess = Process.Start(CreateQuickFailProcessStartInfo()) ?? throw new InvalidOperationException("Failed to start test child process.");
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsV2 = true,
            GetResourceSnapshotsHandler = _ =>
            {
                probeCount++;
                throw new IOException("model unavailable");
            }
        };

        var stable = await AppHostLauncher.WaitForLegacyDetachedStartupStabilityAsync(
            connection,
            childProcess.WaitForExitAsync(),
            TimeSpan.FromSeconds(120),
            TimeProvider.System,
            CancellationToken.None);

        Assert.False(stable);
        Assert.True(probeCount > 0);
    }

    [Fact]
    public async Task LaunchDetachedAsync_ReportsSuccessWhenLegacyV2ProbeSucceeds()
    {
        using var harness = AppHostLauncherHarness.Create(outputHelper);
        var probeCount = 0;
        harness.AddConnection(new TestAppHostAuxiliaryBackchannel
        {
            SupportsV3 = false,
            SupportsV2 = true,
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "https://localhost:18888/login?t=test" },
            GetResourceSnapshotsHandler = _ =>
            {
                probeCount++;
                return Task.FromResult<List<ResourceSnapshot>>([]);
            }
        });
        harness.ProcessLauncher.Mode = TestDetachedProcessLauncher.ChildProcessMode.StayAlive;

        var result = await harness.Launcher.LaunchDetachedAsync(
            harness.AppHostFile,
            format: null,
            isolated: false,
            isExtensionHost: false,
            waitForDebugger: false,
            globalArgs: [],
            additionalArgs: [],
            stopAfterLaunchDelay: null,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(harness.InteractionService.DisplayedErrors);
        Assert.False(harness.ProcessLauncher.StartedProcess?.HasExited);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task LaunchDetachedAsync_ReportsFailureWhenLegacyV2ProbeDoesNotSucceedBeforeChildExit()
    {
        using var harness = AppHostLauncherHarness.Create(outputHelper);
        harness.AddConnection(new TestAppHostAuxiliaryBackchannel
        {
            SupportsV3 = false,
            SupportsV2 = true,
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "https://localhost:18888/login?t=test" },
            GetResourceSnapshotsHandler = _ => throw new IOException("model unavailable")
        });
        harness.ProcessLauncher.Mode = TestDetachedProcessLauncher.ChildProcessMode.ExitWithFailure;
        harness.ProcessLauncher.ChildLogLines =
        [
            "[2026-05-16 19:07:52.383] [INFO] [Build] /work/BrokenAppHost/Program.cs(3,41): error CS1002: ; expected [/work/BrokenAppHost/BrokenAppHost.csproj]",
            "[2026-05-16 19:07:52.392] [INFO] [Build] Build FAILED."
        ];

        var result = await harness.Launcher.LaunchDetachedAsync(
            harness.AppHostFile,
            format: null,
            isolated: false,
            isExtensionHost: false,
            waitForDebugger: false,
            globalArgs: [],
            additionalArgs: [],
            stopAfterLaunchDelay: null,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, result.ExitCode);
        Assert.Contains(RunCommandStrings.FailedToStartAppHost, harness.InteractionService.DisplayedErrors);
        Assert.Contains(RunCommandStrings.AppHostFailedToBuild, harness.InteractionService.DisplayedErrors);
        Assert.Contains(harness.InteractionService.DisplayedLines, line => line.Line.Contains("error CS1002", StringComparison.Ordinal));
        Assert.Contains(harness.InteractionService.DisplayedLines, line => line.Line == "Build FAILED.");
    }

    [Fact]
    public void DetachedChildEnvironmentFilter_PreservesDebugSessionVariables()
    {
        Assert.True(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.ExtensionEndpoint));
        Assert.True(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.ExtensionDebugSessionId));

        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionInfo));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionRunMode));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionPort));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionToken));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionServerCertificate));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DcpInstanceIdPrefix));
    }

    [Fact]
    public void DetachedChildEnvironment_IncludesProfilingTelemetryContext()
    {
        using var listener = CreateActivityListener("test-detached-child-environment");
        using var source = new ActivitySource("test-detached-child-environment");
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);
        activity.SetBaggage(ProfilingTelemetry.Baggage.SessionId, "session-1");
        activity.TraceStateString = "state-1";

        var environment = AppHostLauncher.CreateDetachedChildEnvironment(activity);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.Equal("true", environment[ProfilingTelemetry.EnvironmentVariables.Enabled]);
        Assert.Equal("session-1", environment[ProfilingTelemetry.EnvironmentVariables.SessionId]);
        Assert.Equal("session-1", environment[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.Equal(activity.Id, environment[ProfilingTelemetry.EnvironmentVariables.TraceParent]);
        Assert.Equal("state-1", environment[ProfilingTelemetry.EnvironmentVariables.TraceState]);
    }

    [Fact]
    public void DetachedChildEnvironment_DoesNotIncludeStartupStatusFile()
    {
        var environment = AppHostLauncher.CreateDetachedChildEnvironment(null);

        Assert.False(environment.ContainsKey("ASPIRE_CLI_START_READY_FILE"));
    }

    [Fact]
    public void DetachedChildEnvironment_IncludesProfilingTelemetryContextFromActiveProfilingSpan()
    {
        using var listener = CreateActivityListener(ProfilingTelemetry.ActivitySourceName);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true")));

        using var activity = profilingTelemetry.StartDetachedSpawnChild("aspire", ["run"], childCommand: "run");
        Assert.True(activity.IsRunning);

        var environment = AppHostLauncher.CreateDetachedChildEnvironment(Activity.Current);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.Equal("true", environment[ProfilingTelemetry.EnvironmentVariables.Enabled]);
        var sessionId = environment[ProfilingTelemetry.EnvironmentVariables.SessionId];
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal(sessionId, environment[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.Equal(Activity.Current?.Id, environment[ProfilingTelemetry.EnvironmentVariables.TraceParent]);
    }

    [Fact]
    public void DetachedChildEnvironment_DoesNotEnableProfilingForNonProfilingActivity()
    {
        using var listener = CreateActivityListener("test-detached-child-environment");
        using var source = new ActivitySource("test-detached-child-environment");
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);

        var environment = AppHostLauncher.CreateDetachedChildEnvironment(activity);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.Enabled));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.SessionId));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceParent));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceState));
    }

    [Fact]
    public void DetachedChildEnvironment_AllowsMissingProfilingTelemetryContext()
    {
        var environment = AppHostLauncher.CreateDetachedChildEnvironment(null);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.Enabled));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.SessionId));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceParent));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceState));
    }

    [Fact]
    public async Task ReadChildLogTail_ReturnsBoundedRelevantNonEmptyTail()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var childLogFile = Path.Combine(workspace.WorkspaceRoot.FullName, "child.log");
        await File.WriteAllLinesAsync(childLogFile, [
            "[2026-05-15 17:07:24.674] [DBUG] [Features] Feature updateNotificationsEnabled = True (default: True)",
            "[2026-05-15 17:07:25.069] [INFO] [Stdout] :gear: Preparing Aspire server...",
            "[2026-05-15 17:07:27.381] [INFO] [Stdout] Connecting to AppHost...",
            "[2026-05-15 17:07:28.618] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npm install",
            "[2026-05-15 17:07:29.512] [INFO] [AppHost] up to date, audited 116 packages in 619ms",
            "[2026-05-15 17:07:29.520] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npx --no-install tsc --noEmit -p tsconfig.apphost.json",
            "[2026-05-15 17:07:30.501] [INFO] [AppHost] apphost.ts(5,22): error TS1109: Expression expected.",
            "[2026-05-15 17:07:30.521] [FAIL] [GuestAppHostProject] TypeScript (Node.js) apphost exited with code 2",
            "[2026-05-15 17:07:30.522] [FAIL] [GuestAppHostProject] AppHost server process has exited. Unable to connect to backchannel at /tmp/cli.sock",
            "[2026-05-15 17:07:30.528] [FAIL] [AspireCliTelemetry] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "System.InvalidOperationException: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.534] [INFO] [Stdout] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.540] [INFO] [Stdout] See logs at /tmp/child.log"
        ]);

        var lines = AppHostLauncher.ReadChildLogTail(childLogFile, maxLines: 5);

        Assert.Equal([
            "apphost.ts(5,22): error TS1109: Expression expected."
        ], lines);
    }

    [Fact]
    public async Task ReadChildLogTail_IncludesBuildOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var childLogFile = Path.Combine(workspace.WorkspaceRoot.FullName, "child.log");
        await File.WriteAllLinesAsync(childLogFile, [
            "[2026-05-16 19:07:51.709] [INFO] [Build]   Determining projects to restore...",
            "[2026-05-16 19:07:51.743] [INFO] [Build]   All projects are up-to-date for restore.",
            "[2026-05-16 19:07:52.383] [INFO] [Build] /work/BrokenAppHost/Program.cs(3,41): error CS1002: ; expected [/work/BrokenAppHost/BrokenAppHost.csproj]",
            "[2026-05-16 19:07:52.392] [INFO] [Build] Build FAILED.",
            "[2026-05-16 19:07:52.392] [INFO] [Build]     1 Error(s)"
        ]);

        var lines = AppHostLauncher.ReadChildLogTail(childLogFile, maxLines: 4);

        Assert.Equal([
            "  All projects are up-to-date for restore.",
            "/work/BrokenAppHost/Program.cs(3,41): error CS1002: ; expected [/work/BrokenAppHost/BrokenAppHost.csproj]",
            "Build FAILED.",
            "    1 Error(s)"
        ], lines);
    }

    [Fact]
    public async Task ReadChildLogReplayTail_ReturnsRicherBoundedRelevantTail()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var childLogFile = Path.Combine(workspace.WorkspaceRoot.FullName, "child.log");
        await File.WriteAllLinesAsync(childLogFile, [
            "[2026-05-15 17:07:24.674] [DBUG] [Features] Feature updateNotificationsEnabled = True (default: True)",
            "[2026-05-15 17:07:25.069] [INFO] [Stdout] :gear: Preparing Aspire server...",
            "[2026-05-15 17:07:27.381] [INFO] [Stdout] Connecting to AppHost...",
            "[2026-05-15 17:07:28.618] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npm install",
            "[2026-05-15 17:07:29.512] [INFO] [AppHost] up to date, audited 116 packages in 619ms",
            "[2026-05-15 17:07:29.520] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npx --no-install tsc --noEmit -p tsconfig.apphost.json",
            "[2026-05-15 17:07:30.501] [INFO] [AppHost] apphost.ts(5,22): error TS1109: Expression expected.",
            "[2026-05-15 17:07:30.521] [FAIL] [GuestAppHostProject] TypeScript (Node.js) apphost exited with code 2",
            "[2026-05-15 17:07:30.522] [FAIL] [GuestAppHostProject] AppHost server process has exited. Unable to connect to backchannel at /tmp/cli.sock",
            "[2026-05-15 17:07:30.528] [FAIL] [AspireCliTelemetry] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "System.InvalidOperationException: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.534] [INFO] [Stdout] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.540] [INFO] [Stdout] See logs at /tmp/child.log"
        ]);

        var entries = AppHostLauncher.ReadChildLogReplayTail(childLogFile, maxLines: 6);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Debug, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("Executing: /opt/homebrew/bin/npm install", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
                Assert.Equal(CliLogFormat.Categories.AppHost, entry.Category);
                Assert.Equal("up to date, audited 116 packages in 619ms", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Debug, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("Executing: /opt/homebrew/bin/npx --no-install tsc --noEmit -p tsconfig.apphost.json", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
                Assert.Equal(CliLogFormat.Categories.AppHost, entry.Category);
                Assert.Equal("apphost.ts(5,22): error TS1109: Expression expected.", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Error, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("TypeScript (Node.js) apphost exited with code 2", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Error, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("AppHost server process has exited. Unable to connect to backchannel at /tmp/cli.sock", entry.Message);
            });
    }

    [Fact]
    public async Task ReadChildLogReplayTail_IncludesBuildOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var childLogFile = Path.Combine(workspace.WorkspaceRoot.FullName, "child.log");
        await File.WriteAllLinesAsync(childLogFile, [
            "[2026-05-16 19:07:51.709] [INFO] [Build]   Determining projects to restore...",
            "[2026-05-16 19:07:52.383] [INFO] [Build] /work/BrokenAppHost/Program.cs(3,41): error CS1002: ; expected [/work/BrokenAppHost/BrokenAppHost.csproj]",
            "[2026-05-16 19:07:52.392] [INFO] [Build] Build FAILED."
        ]);

        var entries = AppHostLauncher.ReadChildLogReplayTail(childLogFile, maxLines: 3);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
                Assert.Equal(CliLogFormat.Categories.Build, entry.Category);
                Assert.Equal("  Determining projects to restore...", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
                Assert.Equal(CliLogFormat.Categories.Build, entry.Category);
                Assert.Equal("/work/BrokenAppHost/Program.cs(3,41): error CS1002: ; expected [/work/BrokenAppHost/BrokenAppHost.csproj]", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
                Assert.Equal(CliLogFormat.Categories.Build, entry.Category);
                Assert.Equal("Build FAILED.", entry.Message);
            });
    }

    private static ActivityListener CreateActivityListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    private static ProcessStartInfo CreateQuickFailProcessStartInfo()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c ping -n 2 127.0.0.1 >NUL & exit /b 6")
            : ("/bin/sh", "-c \"sleep 0.1; exit 6\"");

        return new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private sealed class AppHostLauncherHarness : IDisposable
    {
        private readonly TemporaryWorkspace _workspace;
        private readonly DirectoryInfo _homeDirectory;
        private readonly FileLoggerProvider _fileLoggerProvider;

        private AppHostLauncherHarness(
            TemporaryWorkspace workspace,
            DirectoryInfo homeDirectory,
            FileLoggerProvider fileLoggerProvider,
            AppHostLauncher launcher,
            FileInfo appHostFile,
            TestInteractionService interactionService,
            TestAuxiliaryBackchannelMonitor monitor,
            TestDetachedProcessLauncher processLauncher)
        {
            _workspace = workspace;
            _homeDirectory = homeDirectory;
            _fileLoggerProvider = fileLoggerProvider;
            Launcher = launcher;
            AppHostFile = appHostFile;
            InteractionService = interactionService;
            Monitor = monitor;
            ProcessLauncher = processLauncher;
        }

        public AppHostLauncher Launcher { get; }

        public FileInfo AppHostFile { get; }

        public TestInteractionService InteractionService { get; }

        public TestAuxiliaryBackchannelMonitor Monitor { get; }

        public TestDetachedProcessLauncher ProcessLauncher { get; }

        public static AppHostLauncherHarness Create(ITestOutputHelper outputHelper)
        {
            var workspace = TemporaryWorkspace.Create(outputHelper);
            var homeDirectory = workspace.WorkspaceRoot.CreateSubdirectory("home");
            var hivesDirectory = workspace.WorkspaceRoot.CreateSubdirectory("hives");
            var cacheDirectory = workspace.WorkspaceRoot.CreateSubdirectory("cache");
            var sdkDirectory = workspace.WorkspaceRoot.CreateSubdirectory("sdks");
            var logsDirectory = workspace.WorkspaceRoot.CreateSubdirectory("logs");
            var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
            File.WriteAllText(appHostFile.FullName, "<Project />");

            var executionContext = new CliExecutionContext(
                workspace.WorkspaceRoot,
                hivesDirectory,
                cacheDirectory,
                sdkDirectory,
                logsDirectory,
                Path.Combine(logsDirectory.FullName, "parent.log"),
                homeDirectory: homeDirectory);
            var interactionService = new TestInteractionService();
            var monitor = new TestAuxiliaryBackchannelMonitor();
            var processLauncher = new TestDetachedProcessLauncher();
            var fileLoggerProvider = new FileLoggerProvider(executionContext.LogFilePath, new TestStartupErrorWriter());
            var processShutdownService = new ProcessShutdownService(
                new FixedLayoutDiscovery(),
                new LayoutProcessRunner(new TestProcessExecutionFactory()),
                executionContext,
                NullLogger<ProcessShutdownService>.Instance,
                TimeProvider.System);
            var launcher = new AppHostLauncher(
                new TestProjectLocator(),
                executionContext,
                interactionService,
                monitor,
                TestHelpers.CreateInteractiveHostEnvironment(),
                TestTelemetryHelper.CreateInitializedTelemetry(),
                new ProfilingTelemetry(new ConfigurationBuilder().Build()),
                fileLoggerProvider,
                processShutdownService,
                processLauncher,
                NullLogger<AppHostLauncher>.Instance,
                TimeProvider.System);

            return new AppHostLauncherHarness(
                workspace,
                homeDirectory,
                fileLoggerProvider,
                launcher,
                appHostFile,
                interactionService,
                monitor,
                processLauncher);
        }

        public void AddConnection(TestAppHostAuxiliaryBackchannel connection)
        {
            var socketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(AppHostFile.FullName, _homeDirectory.FullName);
            var hash = AppHostHelper.ExtractHashFromSocketPath(socketPrefix) ?? throw new InvalidOperationException("Expected socket hash.");
            connection.Hash = hash;
            connection.AppHostInfo ??= new AppHostInformation
            {
                AppHostPath = AppHostFile.FullName,
                ProcessId = Environment.ProcessId,
                StartedAt = DateTimeOffset.UtcNow
            };

            Monitor.AddConnection(hash, $"{socketPrefix}.sock", connection);
        }

        public void Dispose()
        {
            ProcessLauncher.Dispose();
            _fileLoggerProvider.Dispose();
            _workspace.Dispose();
        }
    }

    private sealed class TestDetachedProcessLauncher : IDetachedProcessLauncher, IDisposable
    {
        public enum ChildProcessMode
        {
            StayAlive,
            ExitWithFailure
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChildProcessMode Mode { get; set; } = ChildProcessMode.StayAlive;

        public IReadOnlyList<string> ChildLogLines { get; set; } = [];

        public Process? StartedProcess { get; private set; }

        public Process Start(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Func<string, bool>? shouldRemoveEnvironmentVariable = null,
            IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null)
        {
            _ = fileName;
            _ = shouldRemoveEnvironmentVariable;
            _ = additionalEnvironmentVariables;

            var childLogFile = GetChildLogFile(arguments);
            if (ChildLogLines.Count > 0)
            {
                File.WriteAllLines(childLogFile, ChildLogLines);
            }

            StartedProcess = Process.Start(CreateProcessStartInfo(workingDirectory)) ?? throw new InvalidOperationException("Failed to start test child process.");
            Started.SetResult();
            return StartedProcess;
        }

        public void Dispose()
        {
            if (StartedProcess is { HasExited: false })
            {
                StartedProcess.Kill(entireProcessTree: true);
                StartedProcess.WaitForExit(TimeSpan.FromSeconds(10));
            }

            StartedProcess?.Dispose();
        }

        private ProcessStartInfo CreateProcessStartInfo(string workingDirectory)
        {
            var (fileName, arguments) = OperatingSystem.IsWindows()
                ? CreateWindowsProcessCommand()
                : CreateUnixProcessCommand();

            return new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        private (string FileName, string Arguments) CreateWindowsProcessCommand()
            => Mode switch
            {
                ChildProcessMode.StayAlive => ("cmd.exe", "/c ping -n 60 127.0.0.1 >NUL"),
                ChildProcessMode.ExitWithFailure => ("cmd.exe", "/c ping -n 2 127.0.0.1 >NUL & exit /b 6"),
                _ => throw new InvalidOperationException($"Unexpected child process mode: {Mode}")
            };

        private (string FileName, string Arguments) CreateUnixProcessCommand()
            => Mode switch
            {
                ChildProcessMode.StayAlive => ("/bin/sh", "-c \"sleep 60\""),
                ChildProcessMode.ExitWithFailure => ("/bin/sh", "-c \"sleep 0.1; exit 6\""),
                _ => throw new InvalidOperationException($"Unexpected child process mode: {Mode}")
            };

        private static string GetChildLogFile(IReadOnlyList<string> arguments)
        {
            var logFileIndex = -1;
            for (var i = 0; i < arguments.Count; i++)
            {
                if (arguments[i] == "--log-file")
                {
                    logFileIndex = i;
                    break;
                }
            }

            if (logFileIndex < 0 || logFileIndex + 1 >= arguments.Count)
            {
                throw new InvalidOperationException("Expected child arguments to include --log-file.");
            }

            return arguments[logFileIndex + 1];
        }
    }

    private sealed class FixedLayoutDiscovery : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => null;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => null;

        public bool IsBundleModeAvailable(string? projectDirectory = null) => false;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
