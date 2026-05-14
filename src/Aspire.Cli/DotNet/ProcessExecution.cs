// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Represents a configured process execution backed by a real OS process.
/// </summary>
internal sealed class ProcessExecution : IProcessExecution
{
    private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly ProcessInvocationOptions _options;
    private Task? _readTask;

    internal ProcessExecution(Process process, ILogger logger, ProcessInvocationOptions options)
    {
        _process = process;
        _logger = logger;
        _options = options;
    }

    /// <inheritdoc />
    public string FileName => _process.StartInfo.FileName;

    /// <inheritdoc />
    public IReadOnlyList<string> Arguments => _process.StartInfo.ArgumentList.ToArray();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> EnvironmentVariables =>
        _process.StartInfo.Environment.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <inheritdoc />
    public bool HasExited => _process.HasExited;

    /// <inheritdoc />
    public int ExitCode => _process.ExitCode;

    /// <inheritdoc />
    public int ProcessId => _process.Id;

    /// <inheritdoc />
    public bool Start()
    {
        var started = _process.Start();

        if (!started)
        {
            _logger.LogDebug("{FileName} failed to start with args: {Args}", FileName, string.Join(" ", Arguments));
            return false;
        }

        _logger.LogDebug("{FileName}({ProcessId}) started in {WorkingDirectory}", FileName, _process.Id, _process.StartInfo.WorkingDirectory);

        // Use ReadAllLinesAsync to multiplex stdout and stderr on a single task,
        // replacing the previous two-forwarder approach with idle-timeout drain logic.
        _readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _process.ReadAllLinesAsync())
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace(
                            "{FileName}({ProcessId}) {Identifier}: {Line}",
                            FileName,
                            _process.Id,
                            line.StandardError ? "stderr" : "stdout",
                            line.Content
                            );
                    }

                    if (line.StandardError)
                    {
                        _options.StandardErrorCallback?.Invoke(line.Content);
                    }
                    else
                    {
                        _options.StandardOutputCallback?.Invoke(line.Content);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Stream was closed externally (e.g., after process exit). This is expected.
                _logger.LogDebug("{FileName}({ProcessId}) read loop completed - stream was closed", FileName, _process.Id);
            }
        });

        return true;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("{FileName}({ProcessId}) waiting for exit", FileName, _process.Id);

        await _process.WaitForExitAsync(cancellationToken);

        if (!_process.HasExited)
        {
            _logger.LogDebug("{FileName}({ProcessId}) has not exited, killing it", FileName, _process.Id);
            _process.Kill(false);
        }
        else
        {
            _logger.LogDebug("{FileName}({ProcessId}) exited with code: {ExitCode}", FileName, _process.Id, _process.ExitCode);
        }

        // Wait for the read loop to drain any remaining output. In some environments
        // the stream handles can stay open after the process exits (e.g., when a grandchild
        // holds the handle), so apply a timeout to avoid hanging indefinitely.
        if (_readTask is not null)
        {
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drainCts.CancelAfter(s_drainTimeout);
            try
            {
                await _readTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
                _logger.LogDebug("{FileName}({ProcessId}) read loop completed", FileName, _process.Id);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{FileName}({ProcessId}) read loop did not complete within drain timeout", FileName, _process.Id);
            }
        }

        return _process.ExitCode;
    }

    /// <inheritdoc />
    public void Kill(bool entireProcessTree)
    {
        _process.Kill(entireProcessTree);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _process.Dispose();
    }
}
