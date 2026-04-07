// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Diagnostics;
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
        CancellationToken cancellationToken)
    {
        if (!CommandPathResolver.TryResolveCommand(command, _commandResolver, out var resolvedCommand, out var errorMessage))
        {
            _logger.LogError("Command '{Command}' not found in PATH", command);
            var errorOutput = new OutputCollector();
            errorOutput.AppendError(errorMessage!);
            return (-1, errorOutput);
        }

        _logger.LogDebug("Executing: {Command} {Args}", resolvedCommand, string.Join(" ", args));

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedCommand,
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

        foreach (var (key, value) in environmentVariables)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        var outputCollector = new OutputCollector(_fileLoggerProvider, "AppHost");
        var stdoutCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is null)
            {
                // ProcessDataReceivedEventArgs.Data is null when the redirected stdout stream closes.
                stdoutCompleted.TrySetResult();
            }
            else
            {
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
                _logger.LogTrace("{Language}({ProcessId}) stderr: {Line}", _language, process.Id, e.Data);
                outputCollector.AppendError(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        // Wait for the redirected streams to finish draining so no trailing lines are lost.
        if (!await WaitForDrainAsync(Task.WhenAll(stdoutCompleted.Task, stderrCompleted.Task), cancellationToken))
        {
            _logger.LogWarning("{Language}({ProcessId}): Timed out waiting for output streams to drain after process exit", _language, process.Id);
        }

        return (process.ExitCode, outputCollector);
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
