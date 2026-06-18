// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Aspire.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class AppHostServerSessionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task StartAsync_DoesNotMutateCallerEnvironmentVariables()
    {
        var project = new RecordingAppHostServerProject();
        var environmentVariables = new Dictionary<string, string>
        {
            ["EXISTING_VALUE"] = "present"
        };

        await using var session = new AppHostServerSession(
            project,
            environmentVariables,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None);
        _ = session.StartAsync();

        Assert.Equal("present", environmentVariables["EXISTING_VALUE"]);
        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.RemoteAppHostToken));

        Assert.NotNull(project.ReceivedEnvironmentVariables);
        Assert.Equal("present", project.ReceivedEnvironmentVariables["EXISTING_VALUE"]);
        Assert.Equal(session.AuthenticationToken, project.ReceivedEnvironmentVariables[KnownConfigNames.RemoteAppHostToken]);
    }

    [Fact]
    public async Task StartAsync_PropagatesProfilingContextToServerEnvironment()
    {
        var project = new RecordingAppHostServerProject();
        var environmentVariables = new Dictionary<string, string>
        {
            ["EXISTING_VALUE"] = "present"
        };
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource);

        await using var session = new AppHostServerSession(
            project,
            environmentVariables,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None,
            profilingTelemetry);
        _ = session.StartAsync();

        Assert.Equal("present", environmentVariables["EXISTING_VALUE"]);
        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.RemoteAppHostToken));
        Assert.False(environmentVariables.ContainsKey(ProfilingTelemetry.EnvironmentVariables.Enabled));

        var receivedEnvironmentVariables = Assert.IsType<Dictionary<string, string>>(project.ReceivedEnvironmentVariables);
        Assert.Equal("present", receivedEnvironmentVariables["EXISTING_VALUE"]);
        Assert.Equal(session.AuthenticationToken, receivedEnvironmentVariables[KnownConfigNames.RemoteAppHostToken]);
        Assert.Equal("true", receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.Enabled]);
        Assert.Equal("true", receivedEnvironmentVariables[KnownConfigNames.Legacy.StartupProfilingEnabled]);
        Assert.Equal("session-1", receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.SessionId]);
        Assert.Equal("session-1", receivedEnvironmentVariables[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.StartsWith("00-", receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.TraceParent], StringComparison.Ordinal);
        Assert.Equal(
            receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.TraceParent],
            receivedEnvironmentVariables[KnownConfigNames.Legacy.StartupTraceParent]);
    }

    [Fact]
    public async Task StartAsync_DoesNotLeaveServerProcessActivityAmbient()
    {
        var project = new RecordingAppHostServerProject();
        using var parentSource = new ActivitySource("test-apphost-server-parent");
        using var parentListener = ActivityListenerHelper.Create(parentSource);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource);

        using var parentActivity = parentSource.StartActivity("aspire/cli/run");
        Assert.NotNull(parentActivity);

        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None,
            profilingTelemetry);
        _ = session.StartAsync();

        Assert.Same(parentActivity, Activity.Current);

        var receivedEnvironmentVariables = Assert.IsType<Dictionary<string, string>>(project.ReceivedEnvironmentVariables);
        Assert.NotEqual(parentActivity.Id, receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.TraceParent]);
    }

    [Fact]
    public void AuthenticationToken_IsAvailableBeforeStart()
    {
        var project = new RecordingAppHostServerProject();
        var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(session.AuthenticationToken));
    }

    [Fact]
    public async Task GetRpcClientAsync_WhenServerExitsBeforeSocketIsAvailable_FailsWithoutWaitingForConnectionTimeout()
    {
        // RecordingAppHostServerProject spawns `dotnet --version`, which exits almost immediately
        // with exit code 0 against a socket path ("test.sock") that never hosts an RPC server.
        // The connection race in GetRpcClientAsync should observe the server-exit signal and fail
        // fast rather than burning the full connection-retry timeout.
        var project = new RecordingAppHostServerProject();

        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None);
        _ = session.StartAsync();

        // Wait for the process to exit so the stopwatch measures only the early-exit detection
        // latency, not the variable execution time of "dotnet --version" on loaded CI machines.
        // Poll the OS by pid rather than awaiting the execution's WaitForExitAsync, which the
        // session's own drive loop is already awaiting on the same execution instance.
        Assert.True(
            WaitForProcessExit(project.StartedExecution!.ProcessId, TimeSpan.FromSeconds(30)),
            "Expected the server probe process to exit.");

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetRpcClientAsync(TestContext.Current.CancellationToken)).DefaultTimeout();
        stopwatch.Stop();

        Assert.Equal("AppHost server process exited before the RPC connection could be established. Exit code: 0.", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected RPC connection to fail promptly after the server process exited, but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void SessionState_BeforeStart_IsNull()
    {
        var project = new RecordingAppHostServerProject();
        var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None);

        Assert.Null(session.SocketPath);
        Assert.Null(session.Output);
        Assert.Null(session.ServerProcessId);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_Throws()
    {
        var project = new RecordingAppHostServerProject();
        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None);

        _ = session.StartAsync();

        Assert.Throws<InvalidOperationException>(() => { _ = session.StartAsync(); });
    }

    [Fact]
    public async Task StartAsync_StopRequested_KillsProcessAndCompletesTask()
    {
        var project = new LongRunningAppHostServerProject();
        using var stopCts = new CancellationTokenSource();
        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            stopCts.Token);

        var completion = session.StartAsync();

        // Process should be running before we ask the session to stop.
        Assert.False(completion.IsCompleted);
        Assert.False(session.HasServerExited);

        stopCts.Cancel();

        // The session's stop registration fires Kill synchronously inline. The Exited
        // event fires asynchronously, so allow the completion task to observe the result.
        var exitCode = await completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(session.HasServerExited);
        Assert.Equal(session.TryGetServerExitCode(), exitCode);
    }

    [Fact]
    public async Task StartAsync_StopRequested_WithGracefulServices_InvokesGracefulSignalerAndExits()
    {
        // External-stop flow with graceful infra wired: the session's ladder must invoke the
        // injected graceful signaler before falling through to wait-for-exit. Simulating "graceful
        // succeeded" by having the fake signaler kill the process keeps the ladder out of the
        // escalation branch and verifies the happy path end-to-end.
        var project = new LongRunningAppHostServerProject();
        using var stopCts = new CancellationTokenSource();
        using var shutdownService = new TestGracefulShutdownWindow();

        // Model the run path: graceful shutdown is enabled so the session routes through the ladder.

        var signaler = new RecordingGracefulSignaler(onSignal: pid =>
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Already exited; treat as graceful success.
            }
            return Task.FromResult(true);
        });

        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            stopCts.Token,
            profilingTelemetry: null,
            gracefulShutdownSignaler: signaler,
            shutdownService: shutdownService);

        var completion = session.StartAsync();
        Assert.False(completion.IsCompleted);
        var serverPid = session.ServerProcessId!.Value;

        stopCts.Cancel();

        await completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(session.HasServerExited);
        Assert.Contains(serverPid, signaler.Pids);
        // Graceful budget was never exhausted in this scenario — the signaler simulated success
        // and WaitForExitAsync observed the exit before anyone called Expire().
        Assert.False(shutdownService.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_StopRequested_GracefulIgnored_ExpireEscalatesToTreeKill()
    {
        // External stop fires, the fake signaler accepts the request but the process refuses to
        // exit (simulating a child that ignores SIGTERM/Ctrl+C). Expiring the central token must
        // make the ladder break out of WaitForExitAsync and escalate to Kill so the process tree
        // is guaranteed to die — covering the "DCP signal didn't take" failure mode.
        var project = new LongRunningAppHostServerProject();
        using var stopCts = new CancellationTokenSource();
        using var shutdownService = new TestGracefulShutdownWindow();

        // Model the run path: graceful shutdown is enabled so the session routes through the ladder.
        // Escalation is driven by the explicit Expire() below, not by the budget elapsing.

        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaler = new RecordingGracefulSignaler(onSignal: _ =>
        {
            signaled.TrySetResult();
            // Pretend the OS signal was issued but the process is a bad citizen and ignores it.
            return Task.FromResult(true);
        });

        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            stopCts.Token,
            profilingTelemetry: null,
            gracefulShutdownSignaler: signaler,
            shutdownService: shutdownService);

        var completion = session.StartAsync();
        Assert.False(completion.IsCompleted);

        stopCts.Cancel();

        // Wait until the ladder has actually called the signaler; otherwise Expire() could fire
        // before the ladder reaches WaitForExitAsync and the cancellation observation race would
        // get tangled with the signaler-cancellation race.
        await signaled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        shutdownService.Expire();

        await completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(session.HasServerExited);
    }

    [Fact]
    public async Task StartAsync_StopRequested_GracefulSignalerThrows_StillEscalatesToKill()
    {
        // Best-effort contract: a thrown exception from the signaler (e.g. DCP layout discovery
        // failed, network blip, anything) must not strand the kill ladder. The ladder logs and
        // falls through to wait+escalate. Expiring the token triggers the kill exactly like the
        // "signal succeeded but process ignored it" case above.
        var project = new LongRunningAppHostServerProject();
        using var stopCts = new CancellationTokenSource();
        using var shutdownService = new TestGracefulShutdownWindow();

        // Model the run path: graceful shutdown is enabled so the session routes through the ladder.

        var signaler = new RecordingGracefulSignaler(onSignal: _ =>
            throw new InvalidOperationException("simulated DCP failure"));

        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            stopCts.Token,
            profilingTelemetry: null,
            gracefulShutdownSignaler: signaler,
            shutdownService: shutdownService);

        var completion = session.StartAsync();
        Assert.False(completion.IsCompleted);

        stopCts.Cancel();
        shutdownService.Expire();

        await completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(session.HasServerExited);
        Assert.Single(signaler.Pids);
    }

    [Fact]
    public async Task DisposeAsync_WithUnconfiguredGracefulService_ForceKillsWithoutSignaling()
    {
        // A graceful window with IsEnabled == false models a non-run command. The coordinator's
        // graceful-vs-force decision is all-or-nothing per command and keys off IsEnabled, so
        // dispose-only teardown here must take the force-kill path and never invoke the signaler.
        // (On the run path IsEnabled is true, so completion routes through the bounded ladder instead —
        // that bound comes from the central graceful clock, not from a per-session external-stop flag.)
        var project = new LongRunningAppHostServerProject();
        using var stopCts = new CancellationTokenSource();
        using var shutdownService = new TestGracefulShutdownWindow { IsEnabled = false };
        var signaler = new RecordingGracefulSignaler();

        var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            stopCts.Token,
            profilingTelemetry: null,
            gracefulShutdownSignaler: signaler,
            shutdownService: shutdownService);

        var completion = session.StartAsync();
        Assert.False(completion.IsCompleted);
        var pid = session.ServerProcessId!.Value;
        Assert.False(session.HasServerExited);

        // DisposeAsync must return promptly even though the graceful token will never fire and
        // the process would otherwise run for a minute. The 30 s timeout is the regression check:
        // if the dispose-only safety net regresses and the ladder takes the graceful path,
        // WaitForExitAsync(gracefulToken) will hang and this WaitAsync will throw.
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // After DisposeAsync, the Process handle has been disposed, so reading HasExited on the
        // original reference would throw. Probe the OS instead: a force-killed process is reaped
        // fast on all platforms — within ~tens of ms — but allow a few seconds of slack for
        // loaded CI hosts before failing.
        Assert.True(WaitForProcessExit(pid, TimeSpan.FromSeconds(5)), $"Process {pid} should be exited after DisposeAsync.");
        Assert.Empty(signaler.Pids);
    }

    [Fact]
    public async Task StartAsync_ProcessExitsNaturally_CompletionReturnsExitCode()
    {
        var project = new RecordingAppHostServerProject();
        await using var session = new AppHostServerSession(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            CancellationToken.None);

        var exitCode = await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task StartAsync_ProjectRunThrows_DisposesProjectAndFaultsCompletion()
    {
        var project = new ThrowingAppHostServerProject();

        // Capture unobserved task exceptions only for the sentinel message we throw, so the test
        // doesn't false-positive on faulted tasks orphaned by other concurrently running xUnit
        // tests sharing the process.
        var unobserved = new List<Exception>();
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(static ex => ex is InvalidOperationException { Message: "simulated launch failure" }))
            {
                unobserved.Add(e.Exception);
                e.SetObserved();
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            // Run the session in a separate async method so the session local is unreachable
            // by the time we force GC — otherwise the stack-rooted reference keeps the faulted
            // completion task alive and UnobservedTaskException never fires.
            await RunScenarioAsync(project);

            // DisposeAsync must observe the faulted completion task created by StartAsync's catch
            // path. Otherwise the orphaned faulted task surfaces as an UnobservedTaskException once
            // GC reaps the TaskCompletionSource — silent corruption for short-lived RPC callers
            // that intentionally discard the StartAsync result.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }

        Assert.True(project.Disposed);
        Assert.Empty(unobserved);

        static async Task RunScenarioAsync(ThrowingAppHostServerProject project)
        {
            var session = new AppHostServerSession(
                project,
                environmentVariables: null,
                debug: false,
                NullLogger<AppHostServerSession>.Instance,
                CancellationToken.None);

            Assert.Throws<InvalidOperationException>(() => { _ = session.StartAsync(); });

            await session.DisposeAsync();
        }
    }

    [Fact]
    public void CreatePrebuiltAppHostServer_DisposesLayoutLeaseWhenConstructorFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appPath = workspace.CreateDirectory("apphost").FullName;
        var integrationCachePathBlockedByFile = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations");
        File.WriteAllText(integrationCachePathBlockedByFile, string.Empty);

        var versionDirectory = workspace.CreateDirectory("version");
        var versionLease = BundleVersionLease.Acquire(versionDirectory.FullName, "test", "apphost-server");
        var layoutLease = new BundleLayoutLease(
            new LayoutConfiguration(),
            versionLease);
        var factory = CreateAppHostServerProjectFactory();

        Assert.True(BundleVersionLease.HasActiveLease(versionDirectory.FullName));

        try
        {
            Assert.ThrowsAny<IOException>(() => factory.CreatePrebuiltAppHostServer(
                appPath,
                "test.sock",
                new LayoutConfiguration(),
                layoutLease));

            Assert.False(BundleVersionLease.HasActiveLease(versionDirectory.FullName));
        }
        finally
        {
            layoutLease.Dispose();
        }
    }

    private static IProcessExecution CreateServerExecution(ProcessStartInfo startInfo, AppHostServerRunControl? runControl)
    {
        // Build a real execution through the production factory so the test exercises the unified
        // IProcessExecution shutdown ladder rather than a bespoke fake. The options mirror what
        // DotNetBasedAppHostServerProject.Run wires from the run control.
        var options = new ProcessInvocationOptions
        {
            GracefulShutdownSignaler = runControl?.GracefulShutdownSignaler,
            ShutdownService = runControl?.ShutdownService,
            // Matches production: the graceful ladder always tree-kills on escalation; this fallback
            // only governs the no-graceful-services path, where Unix force-kills the tree.
            KillEntireProcessTreeOnCancel = !OperatingSystem.IsWindows(),
        };

        return new ProcessExecutionFactory(NullLogger<ProcessExecutionFactory>.Instance)
            .CreateExecution(startInfo, options);
    }

    private static bool WaitForProcessExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = Process.GetProcessById(pid);
                if (probe.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // GetProcessById throws when no process with the given id is running — i.e. the
                // process exited and was reaped from the OS table. That is exactly the success
                // signal we are waiting for.
                return true;
            }
            catch (InvalidOperationException)
            {
                // HasExited can throw if the handle was opened against a process that died and
                // was reaped between the GetProcessById call and the property read. Treat the
                // same as the ArgumentException case.
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    private static AppHostServerProjectFactory CreateAppHostServerProjectFactory()
    {
        var executionContext = TestExecutionContextFactory.CreateTestContext();
        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            executionContext,
            NullLogger<BundleNuGetService>.Instance);

        return new AppHostServerProjectFactory(
            new TestDotNetCliRunner(),
            MockPackagingServiceFactory.Create(),
            new NullBundleService(),
            nugetService,
            new TestDotNetSdkInstaller(),
            executionContext,
            new TestProcessExecutionFactory(),
            NullLoggerFactory.Instance);
    }

    private sealed class RecordingAppHostServerProject : IAppHostServerProject
    {
        public string AppDirectoryPath => Directory.GetCurrentDirectory();

        public Dictionary<string, string>? ReceivedEnvironmentVariables { get; private set; }

        public IProcessExecution? StartedExecution { get; private set; }

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            string? requestedChannel = null,
            string? packageSourceOverride = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AppHostServerRunResult Run(
            int hostPid,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            string[]? additionalArgs = null,
            bool debug = false,
            AppHostServerRunControl? runControl = null)
        {
            ReceivedEnvironmentVariables = environmentVariables is null
                ? null
                : new Dictionary<string, string>(environmentVariables);

            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--version");

            var execution = CreateServerExecution(startInfo, runControl);
            execution.Start();

            StartedExecution = execution;
            return new AppHostServerRunResult(
                SocketPath: "test.sock",
                OutputCollector: new OutputCollector(),
                Execution: execution);
        }
    }

    private sealed class LongRunningAppHostServerProject : IAppHostServerProject
    {
        public string AppDirectoryPath => Directory.GetCurrentDirectory();

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            string? requestedChannel = null,
            string? packageSourceOverride = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AppHostServerRunResult Run(
            int hostPid,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            string[]? additionalArgs = null,
            bool debug = false,
            AppHostServerRunControl? runControl = null)
        {
            // Use a cross-platform long-running command so the test exercises the kill path
            // rather than a quickly-exiting probe like `dotnet --version`.
            var (fileName, arguments) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", "pause" })
                : ("sleep", new[] { "60" });

            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var execution = CreateServerExecution(startInfo, runControl);
            execution.Start();

            return new AppHostServerRunResult(
                SocketPath: "test.sock",
                OutputCollector: new OutputCollector(),
                Execution: execution);
        }
    }

    private sealed class ThrowingAppHostServerProject : IAppHostServerProject, IDisposable
    {
        public string AppDirectoryPath => Directory.GetCurrentDirectory();

        public bool Disposed { get; private set; }

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            string? requestedChannel = null,
            string? packageSourceOverride = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AppHostServerRunResult Run(
            int hostPid,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            string[]? additionalArgs = null,
            bool debug = false,
            AppHostServerRunControl? runControl = null) =>
            throw new InvalidOperationException("simulated launch failure");

        public void Dispose() => Disposed = true;
    }
}
