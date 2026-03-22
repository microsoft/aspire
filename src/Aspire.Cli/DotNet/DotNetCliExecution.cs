// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Represents a configured dotnet CLI execution backed by a real process.
/// </summary>
internal sealed class DotNetCliExecution : IDotNetCliExecution
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly DotNetCliRunnerInvocationOptions _options;
    private Task? _stdoutForwarder;
    private Task? _stderrForwarder;

    internal DotNetCliExecution(Process process, ILogger logger, DotNetCliRunnerInvocationOptions options)
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
    public bool Start()
    {
        var suppressLogging = _options.SuppressLogging;

        var started = _process.Start();

        if (!started)
        {
            if (!suppressLogging)
            {
                _logger.LogDebug("Failed to start dotnet process with args: {Args}", string.Join(" ", Arguments));
            }
            return false;
        }

        if (!suppressLogging)
        {
            _logger.LogDebug("Started dotnet with PID: {ProcessId}", _process.Id);
        }

        // Start stream forwarders
        _stdoutForwarder = Task.Run(async () =>
        {
            await ForwardStreamToLoggerAsync(
                _process.StandardOutput,
                "stdout",
                _options.StandardOutputCallback,
                suppressLogging);
        });

        _stderrForwarder = Task.Run(async () =>
        {
            await ForwardStreamToLoggerAsync(
                _process.StandardError,
                "stderr",
                _options.StandardErrorCallback,
                suppressLogging);
        });

        return true;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var suppressLogging = _options.SuppressLogging;

        if (!suppressLogging)
        {
            _logger.LogDebug("Waiting for dotnet process to exit with PID: {ProcessId}", _process.Id);
        }

        await _process.WaitForExitAsync(cancellationToken);

        if (!_process.HasExited)
        {
            if (!suppressLogging)
            {
                _logger.LogDebug("dotnet process with PID: {ProcessId} has not exited, killing it.", _process.Id);
            }
            _process.Kill(false);
        }
        else
        {
            if (!suppressLogging)
            {
                _logger.LogDebug("dotnet process with PID: {ProcessId} has exited with code: {ExitCode}", _process.Id, _process.ExitCode);
            }
        }

        // Explicitly close the streams to unblock any pending ReadLineAsync calls.
        // In some environments (particularly CI containers), the stream handles may not
        // be automatically closed when the process exits, causing ReadLineAsync to block
        // indefinitely. Disposing the streams forces them to close.
        _logger.LogDebug("Closing stdout/stderr streams for PID: {ProcessId}", _process.Id);
        _process.StandardOutput.Close();
        _process.StandardError.Close();

        // Wait for all the stream forwarders to finish so we know we've got everything
        // fired off through the callbacks. Use a timeout as a safety net in case
        // something else is unexpectedly holding the streams open.
        if (_stdoutForwarder is not null && _stderrForwarder is not null)
        {
            var forwarderTimeout = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var forwardersCompleted = Task.WhenAll([_stdoutForwarder, _stderrForwarder]);

            var completedTask = await Task.WhenAny(forwardersCompleted, forwarderTimeout);
            if (completedTask == forwarderTimeout)
            {
                _logger.LogWarning("Stream forwarders for PID {ProcessId} did not complete within timeout after stream close. Continuing anyway.", _process.Id);
            }
            else
            {
                _logger.LogDebug("Pending forwarders for PID completed: {ProcessId}", _process.Id);
            }
        }

        return _process.ExitCode;
    }

    private async Task ForwardStreamToLoggerAsync(StreamReader reader, string identifier, Action<string>? lineCallback, bool suppressLogging)
    {
        if (!suppressLogging)
        {
            _logger.LogDebug(
                "Starting to forward stream with identifier '{Identifier}' on process '{ProcessId}' to logger",
                identifier,
                _process.Id
                );
        }

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (!suppressLogging)
                {
                    _logger.LogDebug(
                        "dotnet({ProcessId}) {Identifier}: {Line}",
                        _process.Id,
                        identifier,
                        line
                        );
                }
                lineCallback?.Invoke(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // Stream was closed externally (e.g., after process exit). This is expected.
            _logger.LogDebug("Stream forwarder completed for {Identifier} - stream was closed", identifier);
        }
    }
}
