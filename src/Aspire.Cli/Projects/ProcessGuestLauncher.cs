// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly IProcessExecutionFactory _processExecutionFactory;

    public ProcessGuestLauncher(
        string language,
        ILogger logger,
        FileLoggerProvider? fileLoggerProvider = null,
        Func<string, string?>? commandResolver = null,
        IProcessExecutionFactory? processExecutionFactory = null)
    {
        _language = language;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _commandResolver = commandResolver ?? PathLookupHelper.FindFullPathFromPath;
        // The guest launcher does its own per-line trace logging via the per-line callbacks below,
        // so the execution's logger is suppressed to avoid double-logging each stdout/stderr line.
        // Defaulting here (rather than requiring DI threading through GuestRuntime/ScaffoldingService)
        // keeps the construction sites unchanged; the factory is stateless.
        _processExecutionFactory = processExecutionFactory ?? new ProcessExecutionFactory(NullLogger<ProcessExecutionFactory>.Instance);
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
        var firstStdoutSeen = 0;
        var firstStderrSeen = 0;

        // The execution local is forward-referenced by the per-line callbacks so they can read the
        // child's pid per line. ProcessInvocationOptions.StandardOutputCallback is Action<string>
        // (line only), but the guest wants the pid in each trace line. The callbacks only fire after
        // Start(), by which point `execution` is assigned and ProcessId is valid.
        IProcessExecution execution = null!;

        void HandleStdoutLine(string line)
        {
            var pid = execution.ProcessId;
            if (Interlocked.Exchange(ref firstStdoutSeen, 1) == 0)
            {
                AddEvent(activity, ProfilingTelemetry.Events.GuestFirstStdout, TelemetryConstants.Tags.ProcessPid, pid);
            }

            _logger.LogTrace("{Language}({ProcessId}) stdout: {Line}", _language, pid, line);
            outputCollector.AppendOutput(line);
        }

        void HandleStderrLine(string line)
        {
            var pid = execution.ProcessId;
            if (Interlocked.Exchange(ref firstStderrSeen, 1) == 0)
            {
                AddEvent(activity, ProfilingTelemetry.Events.GuestFirstStderr, TelemetryConstants.Tags.ProcessPid, pid);
            }

            _logger.LogTrace("{Language}({ProcessId}) stderr: {Line}", _language, pid, line);
            outputCollector.AppendError(line);
        }

        // Canonical ProcessStartInfo — the factory translates it into the right spawn mode
        // (isolated console group on the run path, ordinary redirected process elsewhere) and
        // strips ASPIRE_CLI_* identity overrides from the child env. The environment is overlaid
        // onto the inherited parent block, matching the previous inherited-console behavior.
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

        var invocationOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = HandleStdoutLine,
            StandardErrorCallback = HandleStderrLine,
            // Run-path spawn: isolated console group + anonymous-pipe stdio so DCP's AttachConsole +
            // GenerateConsoleCtrlEvent dance can target the guest without also signalling the CLI.
            IsolateConsole = options?.IsolateConsoleForGracefulShutdown == true,
            GracefulShutdownSignaler = options?.GracefulShutdownSignaler,
            ShutdownService = options?.ShutdownService,
            // The guest is the AppHost's primary process; always tree-kill on escalation so no
            // descendants (tsx/node) are orphaned. This fallback only governs the no-graceful path
            // (non-Run callers); the graceful ladder always tree-kills regardless.
            KillEntireProcessTreeOnCancel = true,
        };

        AddEvent(activity, ProfilingTelemetry.Events.GuestProcessStart);

        execution = _processExecutionFactory.CreateExecution(startInfo, invocationOptions);

        try
        {
            execution.Start();

            _logger.LogDebug("{Language} guest process {ProcessId} started: {Command}", _language, execution.ProcessId, resolvedCommandPath);
            activity?.SetTag(TelemetryConstants.Tags.ProcessPid, execution.ProcessId);
            AddEvent(activity, ProfilingTelemetry.Events.GuestProcessStarted, TelemetryConstants.Tags.ProcessPid, execution.ProcessId);
            if (afterLaunchAsync is not null)
            {
                await afterLaunchAsync().ConfigureAwait(false);
            }

            int finalExitCode;
            try
            {
                using var _ = cancellationToken.Register(() =>
                    _logger.LogInformation("Cancellation requested while waiting for {Language} guest process {ProcessId} to exit", _language, execution.ProcessId));

                // WaitForExitAsync owns the shutdown ladder: on cancellation it runs the shared
                // graceful-then-tree-kill (or force-kill fallback) decision and drains the output
                // streams before rethrowing OCE. There is no separate shutdown driver here.
                finalExitCode = await execution.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The guest process is the AppHost's primary process for this language. The execution
                // has already killed the tree and drained output by the time the OCE surfaces. We don't
                // rethrow because the caller in GuestAppHostProject uses the returned exit code to
                // distinguish user cancellation from internal teardown (surfacing captured output when
                // the guest was killed because the AppHost system failed). Read the final code from the
                // now-exited process; -1 only if the kill somehow left it observably alive.
                finalExitCode = execution.HasExited ? execution.ExitCode : -1;
            }

            _logger.LogDebug("{Language} guest process {ProcessId} exited with code {ExitCode}", _language, execution.ProcessId, finalExitCode);
            activity?.SetTag(TelemetryConstants.Tags.ProcessExitCode, finalExitCode);
            AddEvent(activity, ProfilingTelemetry.Events.GuestProcessExited, TelemetryConstants.Tags.ProcessExitCode, finalExitCode);

            return (finalExitCode, outputCollector);
        }
        finally
        {
            // Single disposal site. The execution drains its stdout/stderr pumps (bounded internally)
            // and, on the isolated path, releases the anonymous pipes + NUL stdin handle on top of
            // disposing the underlying process.
            await execution.DisposeAsync().ConfigureAwait(false);
        }
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
}
