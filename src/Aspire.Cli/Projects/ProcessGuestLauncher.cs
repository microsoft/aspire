// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Processes;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Launches a guest language process by starting a local OS process.
/// </summary>
internal sealed class ProcessGuestLauncher : IGuestProcessLauncher
{
    private readonly string _language;
    private readonly ILogger _logger;
    private readonly FileLoggerProvider? _fileLoggerProvider;
    private readonly Func<string, string?> _commandResolver;

    public ProcessGuestLauncher(
        string language,
        ILogger logger,
        FileLoggerProvider? fileLoggerProvider = null,
        Func<string, string?>? commandResolver = null)
    {
        _language = language;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _commandResolver = commandResolver ?? PathLookupHelper.FindFullPathFromPath;
    }

    public async Task<(int ExitCode, OutputCollector? Output)> LaunchAsync(
        string command,
        string[] args,
        DirectoryInfo workingDirectory,
        IDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken,
        Func<Task>? afterLaunchAsync = null,
        GuestLaunchOptions? options = null)
    {
        var activity = GetCurrentProfilingActivity();
        AddEvent(activity, ProfilingTelemetry.Events.GuestProcessResolveStart);

        if (!CommandPathResolver.TryResolveCommand(command, _commandResolver, out var resolvedCommand, out var errorMessage))
        {
            AddEvent(activity, ProfilingTelemetry.Events.GuestProcessResolveFailed);
            activity?.SetStatus(ActivityStatusCode.Error, errorMessage);
            _logger.LogError("Command '{Command}' not found in PATH", command);
            var errorOutput = new OutputCollector();
            errorOutput.AppendError(errorMessage!);
            return (-1, errorOutput);
        }

        var resolvedCommandPath = resolvedCommand ?? throw new InvalidOperationException("Command resolution succeeded without a resolved command path.");
        ProfilingTelemetry.SetProcessInvocation(activity, resolvedCommandPath, args);
        AddEvent(activity, ProfilingTelemetry.Events.GuestProcessResolved, TelemetryConstants.Tags.ProcessExecutablePath, resolvedCommandPath);
        _logger.LogDebug("{ExecutingCommandPrefix}{Command} {Args}", CliLogFormat.MessagePrefixes.Executing, resolvedCommandPath, string.Join(" ", args));

        var effectiveEnvironmentVariables = environmentVariables.ToDictionary();
        ProfilingTelemetry.AddActivityContextToEnvironment(activity, effectiveEnvironmentVariables);

        var outputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.AppHost);
        var stdoutCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStdoutSeen = 0;
        var firstStderrSeen = 0;

        // Per-line handlers are shared between the two spawn paths. The pid arrives via parameter
        // rather than being closed over because the isolated path uses an Action<IsolatedProcess, string>
        // at construction time, and we want both paths to fire telemetry/log lines against the same
        // pid the line actually came from (no race against the Process variable assignment).
        void HandleStdoutLine(int pid, string line)
        {
            if (Interlocked.Exchange(ref firstStdoutSeen, 1) == 0)
            {
                AddEvent(activity, ProfilingTelemetry.Events.GuestFirstStdout, TelemetryConstants.Tags.ProcessPid, pid);
            }

            _logger.LogTrace("{Language}({ProcessId}) stdout: {Line}", _language, pid, line);
            outputCollector.AppendOutput(line);
        }

        void HandleStderrLine(int pid, string line)
        {
            if (Interlocked.Exchange(ref firstStderrSeen, 1) == 0)
            {
                AddEvent(activity, ProfilingTelemetry.Events.GuestFirstStderr, TelemetryConstants.Tags.ProcessPid, pid);
            }

            _logger.LogTrace("{Language}({ProcessId}) stderr: {Line}", _language, pid, line);
            outputCollector.AppendError(line);
        }

        Process process;
        IAsyncDisposable? lifetime = null;
        Task stdoutDrain;
        Task stderrDrain;
        // Readers for exit-code / has-exited that route through the IsolatedProcess wrapper on
        // the isolated path. Process.ExitCode on a Process.GetProcessById-derived instance
        // throws InvalidOperationException on Windows ("Process was not started by this
        // object") — see https://github.com/dotnet/runtime/issues/45003. The wrapper sidesteps
        // this by querying GetExitCodeProcess directly against the kept CreateProcess handle.
        Func<int> readExitCode;
        Func<bool> readHasExited;

        AddEvent(activity, ProfilingTelemetry.Events.GuestProcessStart);

        try
        {
            if (options?.IsolateConsoleForGracefulShutdown == true)
            {
                // Run-path spawn: isolated console group + anonymous-pipe stdio so DCP's
                // AttachConsole + GenerateConsoleCtrlEvent dance can target the guest without
                // also signalling the CLI itself. Build the canonical ProcessStartInfo first so
                // env/arg shape stays identical to the inherited branch; IsolatedConsoleSpawner
                // translates to IsolatedProcessStartInfo and fail-fasts on Windows + null job.
                var startInfo = new ProcessStartInfo
                {
                    FileName = resolvedCommandPath,
                    WorkingDirectory = workingDirectory.FullName,
                };

                foreach (var arg in args)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                foreach (var (key, value) in effectiveEnvironmentVariables)
                {
                    startInfo.Environment[key] = value;
                }

                var isolatedChild = IsolatedConsoleSpawner.StartIsolated(
                    startInfo,
                    HandleStdoutLine,
                    HandleStderrLine);
                process = isolatedChild.Process;
                stdoutDrain = isolatedChild.StandardOutputClosed;
                stderrDrain = isolatedChild.StandardErrorClosed;
                lifetime = isolatedChild;
                readExitCode = () => isolatedChild.ExitCode;
                readHasExited = () => isolatedChild.HasExited;
            }
            else
            {
                // Inherited-console spawn — today's behavior, retained for non-Run callers
                // (publish, scaffolding) where the new-console dance is unnecessary.
                var startInfo = new ProcessStartInfo
                {
                    FileName = resolvedCommandPath,
                    WorkingDirectory = workingDirectory.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var arg in args)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                foreach (var (key, value) in effectiveEnvironmentVariables)
                {
                    startInfo.EnvironmentVariables[key] = value;
                }

                var inheritedProcess = new Process { StartInfo = startInfo };
                // Publish the lifetime immediately so a fault between here and the end of the
                // wiring block (Start, BeginOutputReadLine, BeginErrorReadLine) still runs disposal
                // through the finally — Process owns an OS handle even before Start.
                lifetime = ProcessLifetimeAdapter.ForProcess(inheritedProcess);
                inheritedProcess.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data is null)
                    {
                        // ProcessDataReceivedEventArgs.Data is null when the redirected stdout stream closes.
                        stdoutCompleted.TrySetResult();
                    }
                    else
                    {
                        HandleStdoutLine(inheritedProcess.Id, e.Data);
                    }
                };
                inheritedProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data is null)
                    {
                        // ProcessDataReceivedEventArgs.Data is null when the redirected stderr stream closes.
                        stderrCompleted.TrySetResult();
                    }
                    else
                    {
                        HandleStderrLine(inheritedProcess.Id, e.Data);
                    }
                };
                inheritedProcess.Start();
                inheritedProcess.BeginOutputReadLine();
                inheritedProcess.BeginErrorReadLine();
                process = inheritedProcess;
                stdoutDrain = stdoutCompleted.Task;
                stderrDrain = stderrCompleted.Task;
                // Non-isolated path: Process was created via new Process { StartInfo = ... } +
                // Start(), so Process.ExitCode / Process.HasExited work normally on every OS.
                readExitCode = () => inheritedProcess.ExitCode;
                readHasExited = () => inheritedProcess.HasExited;
            }

            _logger.LogDebug("{Language} guest process {ProcessId} started: {Command}", _language, process.Id, resolvedCommandPath);
            activity?.SetTag(TelemetryConstants.Tags.ProcessPid, process.Id);
            AddEvent(activity, ProfilingTelemetry.Events.GuestProcessStarted, TelemetryConstants.Tags.ProcessPid, process.Id);
            if (afterLaunchAsync is not null)
            {
                await afterLaunchAsync().ConfigureAwait(false);
            }

            try
            {
                using var _ = cancellationToken.Register(() =>
                    _logger.LogInformation("Cancellation requested while waiting for {Language} guest process {ProcessId} to exit", _language, process.Id));

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The guest process is the AppHost's primary process for this language. When the caller
                // cancels - either because the user pressed Ctrl+C or because a fatal startup condition
                // (e.g. the AppHost server backchannel timed out) escalated into a teardown - we must kill
                // the process tree, otherwise the AppHost stays alive after the CLI returns and the run
                // appears to hang from the user's perspective.
                //
                // We don't rethrow the OperationCanceledException because the caller in GuestAppHostProject
                // uses the returned exit code to distinguish user cancellation from internal teardown
                // (e.g. surfacing captured output when the guest was killed because the AppHost system
                // failed). Wait without honoring cancellation so the OS reports the final exit code and
                // the redirected output streams have time to drain.
                await ShutdownGuestProcessAsync(process, options).ConfigureAwait(false);
            }

            _logger.LogDebug("{Language} guest process {ProcessId} exited with code {ExitCode}", _language, process.Id, readExitCode());

            var finalExitCode = readExitCode();
            activity?.SetTag(TelemetryConstants.Tags.ProcessExitCode, finalExitCode);
            AddEvent(activity, ProfilingTelemetry.Events.GuestProcessExited, TelemetryConstants.Tags.ProcessExitCode, finalExitCode);

            // Wait for the redirected streams to finish draining so no trailing lines are lost.
            // Pass a fresh token rather than the outer cancellation token: when WaitForExitAsync
            // above was canceled we deliberately killed the process and want to give the streams
            // their full 5s grace period to flush trailing lines, otherwise drain would short-circuit
            // immediately and we'd both drop output and log a misleading "drain timeout" warning.
            if (!await WaitForDrainAsync(Task.WhenAll(stdoutDrain, stderrDrain)))
            {
                AddEvent(activity, ProfilingTelemetry.Events.GuestOutputDrainTimeout, TelemetryConstants.Tags.ProcessPid, process.Id);
                _logger.LogWarning("{Language}({ProcessId}): Timed out waiting for output streams to drain after process exit", _language, process.Id);
            }

            return (finalExitCode, outputCollector);
        }
        finally
        {
            // Single disposal site for both spawn paths. The lifetime is either an IsolatedProcess
            // (which drains pumps + closes the anonymous pipes + NUL stdin handle on top of
            // disposing the Process) or a ProcessLifetimeAdapter that just disposes the Process.
            // Null when the spawn itself threw (e.g. IsolatedConsoleSpawner fail-fast) — nothing
            // to dispose in that case.
            if (lifetime is not null)
            {
                await lifetime.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Task ShutdownGuestProcessAsync(
        Process process,
        GuestLaunchOptions? options)
    {
        // Run-path graceful ladder shared with AppHostServerSession and ProcessExecution when the
        // central budget is wired and enabled; otherwise a best-effort force-kill for non-Run callers
        // (publish, extension adapter) that didn't opt into the central shutdown budget. The coordinator
        // starts the central graceful clock when it selects the ladder, so the wait is always bounded.
        return ProcessShutdownCoordinator.ShutdownAsync(
            process,
            options?.GracefulShutdownSignaler,
            options?.ShutdownService,
            fallbackRequestGracefulShutdown: !OperatingSystem.IsWindows(),
            fallbackKillEntireProcessTree: true,
            _logger,
            $"{_language} guest");
    }

    private static Activity? GetCurrentProfilingActivity()
    {
        var activity = Activity.Current;
        return activity?.Source.Name == ProfilingTelemetry.ActivitySourceName ? activity : null;
    }

    private static void AddEvent(Activity? activity, string eventName, string? tagName = null, object? tagValue = null)
    {
        if (activity is null)
        {
            return;
        }

        if (tagName is null)
        {
            activity.AddEvent(new ActivityEvent(eventName));
            return;
        }

        activity.AddEvent(new ActivityEvent(eventName, tags: new ActivityTagsCollection
        {
            [tagName] = tagValue
        }));
    }

    private static async Task<bool> WaitForDrainAsync(Task drainTask)
    {
        // Bounded grace period for stdout/stderr to flush after the process exits. Intentionally
        // does not honor any outer cancellation token: callers reach here after killing the
        // process on cancellation and we want to give the streams their full budget to surface
        // trailing output regardless of why we got here.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await drainTask.WaitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

    }
}
