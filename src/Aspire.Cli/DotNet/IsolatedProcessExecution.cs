// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// <see cref="IProcessExecution"/> backed by <see cref="IsolatedProcess"/> — used when
/// <see cref="ProcessInvocationOptions.IsolateConsole"/> is <c>true</c> so the child can
/// receive DCP's <c>stop-process-tree</c> CTRL+C dance on Windows without also signalling the
/// CLI. The cancellation path uses the shared
/// <see cref="ProcessGracefulShutdownLadder"/> when graceful infra is wired on the options;
/// otherwise it falls back to <see cref="ProcessTerminator"/> for full back-compat.
/// </summary>
internal sealed class IsolatedProcessExecution : IProcessExecution
{
    private readonly IsolatedProcess _isolated;
    private readonly ILogger _logger;
    private readonly ProcessInvocationOptions _options;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _arguments;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private int _disposed;

    internal IsolatedProcessExecution(
        IsolatedProcess isolated,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        ILogger logger,
        ProcessInvocationOptions options)
    {
        _isolated = isolated;
        _fileName = fileName;
        _arguments = arguments;
        _environment = environment;
        _logger = logger;
        _options = options;
    }

    /// <inheritdoc />
    public string FileName => _fileName;

    /// <inheritdoc />
    public IReadOnlyList<string> Arguments => _arguments;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> EnvironmentVariables => _environment;

    /// <inheritdoc />
    public bool HasExited => _isolated.HasExited;

    /// <inheritdoc />
    public int ExitCode => _isolated.ExitCode;

    /// <inheritdoc />
    public int ProcessId => _isolated.Id;

    /// <inheritdoc />
    public bool Start()
    {
        // IsolatedProcess.Start (called by the factory) already spawned the child and started
        // the pumps. We're a thin wrapper; "starting" is implicit. Returning true keeps the
        // factory contract identical to ProcessExecution.
        _logger.LogDebug("{FileName}({ProcessId}) started in isolated console group", _fileName, _isolated.Id);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("{FileName}({ProcessId}) waiting for exit", _fileName, _isolated.Id);

        try
        {
            await _isolated.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{FileName}({ProcessId}) wait was canceled, escalating shutdown", _fileName, _isolated.Id);

            await ProcessShutdownCoordinator.ShutdownAsync(
                _isolated.Process,
                _options.GracefulShutdownSignaler,
                _options.ShutdownService,
                gracefulBudgetActive: true,
                fallbackRequestGracefulShutdown: !OperatingSystem.IsWindows(),
                fallbackKillEntireProcessTree: _options.KillEntireProcessTreeOnCancel,
                _logger,
                _fileName).ConfigureAwait(false);

            throw;
        }

        _logger.LogDebug("{FileName}({ProcessId}) exited with code: {ExitCode}", _fileName, _isolated.Id, _isolated.ExitCode);

        // Wait for the stdout/stderr pumps to finish draining so callbacks see the tail of
        // the output. Bounded by a small drain budget — if the pumps somehow stay open
        // beyond it (orphaned pipes, hostile callback) we still surface the exit code.
        try
        {
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(_isolated.StandardOutputClosed, _isolated.StandardErrorClosed)
                .WaitAsync(drainCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{FileName}({ProcessId}) stdout/stderr pumps did not drain within timeout after exit", _fileName, _isolated.Id);
        }
        catch (Exception ex)
        {
            // A handler throw surfaces here via the pump's faulted Task. Log but continue —
            // ExitCode is still meaningful even if a callback misbehaved.
            _logger.LogWarning(ex, "{FileName}({ProcessId}) stdout/stderr pump faulted while draining after exit", _fileName, _isolated.Id);
        }

        return _isolated.ExitCode;
    }

    /// <inheritdoc />
    public void Kill(bool entireProcessTree)
    {
        _isolated.Kill(entireProcessTree);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // IProcessExecution is a sync IDisposable. IsolatedProcess.DisposeAsync blocks
        // briefly on pump drain (5 s ceiling). In practice DotNetCliRunner does not
        // dispose the execution (StartBackchannelAsync runs fire-and-forget and reads
        // HasExited/ExitCode after the await — see DotNetCliRunner.cs:145), so this
        // sync-blocking path is reached only by explicit consumers or finalization.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _isolated.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{FileName}({ProcessId}) IsolatedProcess dispose threw", _fileName, _isolated.Id);
        }
    }
}
