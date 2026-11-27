// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;
using Aspire.Shared;

namespace Aspire.Managed.Tests;

public class ParentProcessWatchdogTests
{
    [Fact]
    public void GetExpectedParentStartTimeUnix_PrefersStableValue()
    {
        var environment = new Dictionary<string, string?>
        {
            [KnownConfigNames.CliProcessStarted] = "1000",
            [KnownConfigNames.CliProcessStartedStable] = "1001"
        };

        var startTime = ParentProcessWatchdog.GetExpectedParentStartTimeUnix(environment.GetValueOrDefault, out var useRuntimeStartTime);

        Assert.Equal(1001, startTime);
        Assert.False(useRuntimeStartTime);
    }

    [Fact]
    public void GetExpectedParentStartTimeUnix_UsesRuntimeComparisonForLegacyValue()
    {
        var environment = new Dictionary<string, string?>
        {
            [KnownConfigNames.CliProcessStarted] = "1000"
        };

        var startTime = ParentProcessWatchdog.GetExpectedParentStartTimeUnix(environment.GetValueOrDefault, out var useRuntimeStartTime);

        Assert.Equal(1000, startTime);
        Assert.True(useRuntimeStartTime);
    }

    [Fact]
    public void GetExpectedParentStartTimeUnix_UsesLegacyValueWhenNoStableVariableIsConfigured()
    {
        // The extension-host watchdog only supplies the legacy seconds value; there is no stable
        // millisecond identity to read.
        var environment = new Dictionary<string, string?>
        {
            [KnownConfigNames.CliParentProcessStarted] = "1000"
        };

        var startTime = ParentProcessWatchdog.GetExpectedParentStartTimeUnix(
            environment.GetValueOrDefault,
            stableStartVariable: null,
            KnownConfigNames.CliParentProcessStarted,
            out var useRuntimeStartTime);

        Assert.Equal(1000, startTime);
        Assert.True(useRuntimeStartTime);
    }

    [Fact]
    public void GetExpectedParentStartTimeUnix_FallsBackToPidOnlyWhenNoStartTimeIsConfigured()
    {
        var environment = new Dictionary<string, string?>();

        var startTime = ParentProcessWatchdog.GetExpectedParentStartTimeUnix(
            environment.GetValueOrDefault,
            stableStartVariable: null,
            legacyStartVariable: null,
            out var useRuntimeStartTime);

        Assert.Null(startTime);
        Assert.True(useRuntimeStartTime);
    }

    [Fact]
    public async Task ParentExitedCallback_StillForceExitsWhenCancellationCallbackThrows()
    {
        using var operationCts = new CancellationTokenSource();
        using var registration = operationCts.Token.Register(static () => throw new InvalidOperationException("simulated cancellation callback failure"));
        int? exitCode = null;

        await ParentProcessWatchdog.CancelAndForceExitAsync(
            operationCts,
            CancellationToken.None,
            forceExitGracePeriod: TimeSpan.Zero,
            exit: code => exitCode = code);

        Assert.True(operationCts.IsCancellationRequested);
        Assert.Equal(124, exitCode);
    }

    [Fact]
    public async Task CancellationBackstopFiresWhenCancellationCallbackBlocks()
    {
        using var operationCts = new CancellationTokenSource();
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var registration = operationCts.Token.Register(() =>
        {
            callbackStarted.Set();
            releaseCallback.Wait();
        });
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var cancellationTask = Task.Run(() => ParentProcessWatchdog.CancelAndForceExitAsync(
            operationCts,
            CancellationToken.None,
            forceExitGracePeriod: TimeSpan.FromMilliseconds(50),
            exit: code => exitCode.TrySetResult(code)));

        try
        {
            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(124, await exitCode.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            releaseCallback.Set();
        }

        await cancellationTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StartCliParentScope_WithoutLauncherIdentity_IsNotWatchingButStillLinked()
    {
        using var launcherPid = new EnvVarScope(KnownConfigNames.CliParentProcessId, null);
        using var launcherStarted = new EnvVarScope(KnownConfigNames.CliParentProcessStarted, null);
        using var callerCts = new CancellationTokenSource();

        await using var scope = ParentProcessWatchdog.StartCliParentScope(callerCts.Token);

        // No launcher identity means nothing to watch, but the scope must still exist so callers have a
        // single code path. Only the caller's own cancellation can end the command.
        Assert.False(scope.IsWatchingLauncher);
        Assert.False(scope.Token.IsCancellationRequested);

        await callerCts.CancelAsync();
        Assert.True(scope.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task StartCliParentScope_WithLauncherIdentity_IsWatchingAndCancelsWhenLauncherIsGone()
    {
        using var launcherPid = new EnvVarScope(KnownConfigNames.CliParentProcessId, GetExitedProcessId());
        using var launcherStarted = new EnvVarScope(KnownConfigNames.CliParentProcessStarted, null);
        using var callerCts = new CancellationTokenSource();

        await using var scope = ParentProcessWatchdog.StartCliParentScope(callerCts.Token);

        Assert.True(scope.IsWatchingLauncher);

        // The monitor probes once before its first tick, so an already-dead launcher cancels promptly.
        await WaitForCancellationAsync(scope.Token);
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.False(callerCts.IsCancellationRequested);
    }

    [Fact]
    public async Task StartCliParentScope_DisposeStopsWatchingWithoutCancellingToken()
    {
        using var launcherPid = new EnvVarScope(KnownConfigNames.CliParentProcessId, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        using var launcherStarted = new EnvVarScope(KnownConfigNames.CliParentProcessStarted, null);
        using var callerCts = new CancellationTokenSource();

        var scope = ParentProcessWatchdog.StartCliParentScope(callerCts.Token);
        Assert.True(scope.IsWatchingLauncher);

        // Disposing the scope disarms the monitor; it must not manufacture a cancellation on the way out,
        // because the command may be completing normally.
        await scope.DisposeAsync();

        Assert.False(scope.Token.IsCancellationRequested);
        Assert.False(callerCts.IsCancellationRequested);
    }

    [Fact]
    public async Task StartCliParentScope_CancelAsync_DoesNotCancelCallerToken()
    {
        using var launcherPid = new EnvVarScope(KnownConfigNames.CliParentProcessId, null);
        using var launcherStarted = new EnvVarScope(KnownConfigNames.CliParentProcessStarted, null);
        using var callerCts = new CancellationTokenSource();

        await using var scope = ParentProcessWatchdog.StartCliParentScope(callerCts.Token);

        await scope.CancelAsync();

        // Cancelling the scope is how a command stops its own background work; it must not look like the
        // user pressed Ctrl+C, so the caller's token stays untouched.
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.False(callerCts.IsCancellationRequested);
    }

    [Fact]
    public async Task StartCliParentScope_DisposeIsIdempotent()
    {
        using var launcherPid = new EnvVarScope(KnownConfigNames.CliParentProcessId, null);
        using var launcherStarted = new EnvVarScope(KnownConfigNames.CliParentProcessStarted, null);
        using var callerCts = new CancellationTokenSource();

        var scope = ParentProcessWatchdog.StartCliParentScope(callerCts.Token);

        await scope.DisposeAsync();
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task StartCliParentScope_TokenAndCancelRemainUsableAfterDispose()
    {
        using var launcherPid = new EnvVarScope(KnownConfigNames.CliParentProcessId, null);
        using var launcherStarted = new EnvVarScope(KnownConfigNames.CliParentProcessStarted, null);
        using var callerCts = new CancellationTokenSource();

        var scope = ParentProcessWatchdog.StartCliParentScope(callerCts.Token);
        await scope.DisposeAsync();

        // Cancellation handlers and catch filters run while the scope is being disposed, so inspecting
        // the token then must not throw ObjectDisposedException.
        Assert.False(scope.Token.IsCancellationRequested);
        await scope.CancelAsync();
        Assert.False(scope.Token.IsCancellationRequested);
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    // The monitor probes the launcher immediately, so an already-exited PID is detected without waiting a
    // full poll interval. Output is not redirected: this file is under test, and inheriting the test
    // host's pipes would leave WriteLineAsync waiting on a pipe the exited child no longer reads.
    private static string GetExitedProcessId()
    {
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            ArgumentList = { "--info" },
            UseShellExecute = false,
        })!;
        exited.WaitForExit();

        return exited.Id.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
