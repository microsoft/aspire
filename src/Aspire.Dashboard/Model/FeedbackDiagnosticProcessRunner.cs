// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Result of running a short-lived diagnostic process while gathering dashboard feedback.
/// </summary>
/// <param name="Started">Whether the process was successfully started.</param>
/// <param name="ExitCode">The process exit code, or <c>-1</c> when the process did not start or was killed.</param>
/// <param name="StandardOutput">The captured standard output. Standard error is intentionally not captured.</param>
/// <param name="TimedOut">Whether the process was killed because it exceeded its timeout.</param>
/// <param name="FailureMessage">A message describing why the process could not be started, when applicable.</param>
internal readonly record struct FeedbackDiagnosticProcessResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    bool TimedOut,
    string? FailureMessage);

/// <summary>
/// Runs short-lived external processes used to gather feedback diagnostics, for example
/// <c>aspire doctor</c>, the MSBuild AppHost property probe, and <c>node --version</c>. Abstracted
/// behind an interface so the provider can be unit tested without spawning real processes.
/// </summary>
internal interface IFeedbackDiagnosticProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with the supplied arguments and captures its standard output.
    /// Standard error is drained but discarded so a clean stdout payload (for example
    /// <c>aspire doctor --format json</c>) can be used without scrubbing.
    /// </summary>
    Task<FeedbackDiagnosticProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class FeedbackDiagnosticProcessRunner(ILogger<FeedbackDiagnosticProcessRunner> logger) : IFeedbackDiagnosticProcessRunner
{
    public async Task<FeedbackDiagnosticProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new FeedbackDiagnosticProcessResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, TimedOut: false, FailureMessage: "the process could not be started.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Process.Start throws Win32Exception when the executable cannot be found on PATH and
            // InvalidOperationException for an invalid start configuration. Both mean we could not
            // launch the diagnostic tool, so collapse them into a single failure result.
            logger.LogDebug(ex, "Could not start '{FileName}' while gathering dashboard feedback diagnostics.", fileName);
            return new FeedbackDiagnosticProcessResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, TimedOut: false, FailureMessage: ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        Task<string>? outputTask = null;
        Task<string>? drainErrorTask = null;
        try
        {
            // Read stdout to completion while concurrently draining stderr. stderr is discarded, but
            // it must still be read or a full stderr pipe buffer would deadlock the child before it
            // can exit and flush stdout.
            outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            drainErrorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, drainErrorTask).ConfigureAwait(false);

            return new FeedbackDiagnosticProcessResult(Started: true, ExitCode: process.ExitCode, StandardOutput: outputTask.Result, TimedOut: false, FailureMessage: null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timed out (the linked token fired because of CancelAfter, not the caller's token).
            return new FeedbackDiagnosticProcessResult(Started: true, ExitCode: -1, StandardOutput: string.Empty, TimedOut: true, FailureMessage: null);
        }
        finally
        {
            KillProcess(process, fileName);

            // After a kill the pending reads can fault (for example IOException on the closed pipe);
            // observe them so the faults cannot resurface as UnobservedTaskException.
            ObserveFault(outputTask);
            ObserveFault(drainErrorTask);
        }
    }

    private void KillProcess(Process process, string fileName)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            logger.LogDebug(ex, "Could not kill the '{FileName}' process while gathering dashboard feedback diagnostics.", fileName);
        }
    }

    private static void ObserveFault(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
