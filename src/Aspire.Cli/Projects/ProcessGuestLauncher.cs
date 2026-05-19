// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Diagnostics;
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

    public ProcessGuestLauncher(string language, ILogger logger, FileLoggerProvider? fileLoggerProvider = null, Func<string, string?>? commandResolver = null)
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
        Func<Task>? afterLaunchAsync = null)
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

        var effectiveEnvironmentVariables = environmentVariables.ToDictionary();
        ProfilingTelemetry.AddActivityContextToEnvironment(activity, effectiveEnvironmentVariables);
        foreach (var (key, value) in effectiveEnvironmentVariables)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        var outputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.AppHost);
        var stdoutCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStdoutSeen = 0;
        var firstStderrSeen = 0;

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is null)
            {
                // ProcessDataReceivedEventArgs.Data is null when the redirected stdout stream closes.
                stdoutCompleted.TrySetResult();
            }
            else
            {
                if (Interlocked.Exchange(ref firstStdoutSeen, 1) == 0)
                {
                    AddEvent(activity, ProfilingTelemetry.Events.GuestFirstStdout, TelemetryConstants.Tags.ProcessPid, process.Id);
                }

                _logger.LogTrace("{Language}({ProcessId}) stdout: {Line}", _language, process.Id, e.Data);
                outputCollector.AppendOutput(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is null)
            {
                // ProcessDataReceivedEventArgs.Data is null when the redirected stderr stream closes.
                stderrCompleted.TrySetResult();
            }
            else
            {
                if (Interlocked.Exchange(ref firstStderrSeen, 1) == 0)
                {
                    AddEvent(activity, ProfilingTelemetry.Events.GuestFirstStderr, TelemetryConstants.Tags.ProcessPid, process.Id);
                }

                _logger.LogTrace("{Language}({ProcessId}) stderr: {Line}", _language, process.Id, e.Data);
                outputCollector.AppendError(e.Data);
            }
        };

        AddEvent(activity, ProfilingTelemetry.Events.GuestProcessStart);
        process.Start();
        activity?.SetTag(TelemetryConstants.Tags.ProcessPid, process.Id);
        AddEvent(activity, ProfilingTelemetry.Events.GuestProcessStarted, TelemetryConstants.Tags.ProcessPid, process.Id);
        if (afterLaunchAsync is not null)
        {
            await afterLaunchAsync().ConfigureAwait(false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        activity?.SetTag(TelemetryConstants.Tags.ProcessExitCode, process.ExitCode);
        AddEvent(activity, ProfilingTelemetry.Events.GuestProcessExited, TelemetryConstants.Tags.ProcessExitCode, process.ExitCode);

        // Wait for the redirected streams to finish draining so no trailing lines are lost.
        if (!await WaitForDrainAsync(Task.WhenAll(stdoutCompleted.Task, stderrCompleted.Task), cancellationToken))
        {
            AddEvent(activity, ProfilingTelemetry.Events.GuestOutputDrainTimeout, TelemetryConstants.Tags.ProcessPid, process.Id);
            _logger.LogWarning("{Language}({ProcessId}): Timed out waiting for output streams to drain after process exit", _language, process.Id);
        }

        return (process.ExitCode, outputCollector);
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

    private static async Task<bool> WaitForDrainAsync(Task drainTask, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await drainTask.WaitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
