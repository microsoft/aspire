// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Processes;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

internal sealed partial class DetachedProcessExecution(
    IsolatedProcessStartInfo startInfo,
    string fileName,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string?> environment,
    ILogger logger,
    ProcessInvocationOptions options,
    ILayoutDiscovery? layoutDiscovery,
    IBundleService? bundleService,
    CliExecutionContext? executionContext,
    string? dcpPathOverride = null) : IProcessExecution
{
    private DetachedChildProcess? _process;

    public string FileName => fileName;

    public IReadOnlyList<string> Arguments => arguments;

    public IReadOnlyDictionary<string, string?> EnvironmentVariables => environment;

    public int ProcessId => ChildProcess.Id;

    public DateTimeOffset? StartTime => ChildProcess.StartTime;

    public bool HasExited => ChildProcess.HasExited;

    public int? ExitCode => ChildProcess.ExitCode;

    private DetachedChildProcess ChildProcess =>
        _process ?? throw new InvalidOperationException($"{nameof(DetachedProcessExecution)} has not been started. Call {nameof(StartAsync)} first.");

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveEnvironment = environment
            .Where(static kvp => kvp.Value is not null)
            .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);

        string? dcpPath = dcpPathOverride;
        if (!OperatingSystem.IsWindows())
        {
            if (dcpPath is null)
            {
                if (layoutDiscovery is null || bundleService is null || executionContext is null)
                {
                    throw new InvalidOperationException("Detached Unix process launch requires Aspire layout services.");
                }

                using var layoutLease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "dcp-fork-process", cancellationToken).ConfigureAwait(false);
                var dcpDirectory = layoutLease?.Layout.GetDcpPath() ??
                    layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
                if (dcpDirectory is null)
                {
                    throw new InvalidOperationException("Could not find DCP in the Aspire layout.");
                }

                dcpPath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
                layoutLease?.AddEnvironment(effectiveEnvironment);
            }

            if (!File.Exists(dcpPath))
            {
                throw new InvalidOperationException($"Could not find DCP executable at '{dcpPath}'.");
            }

            logger.LogDebug("Launching detached child process through DCP fork-process: {DcpPath}", dcpPath);
        }

        _process = await StartDetachedChildProcessAsync(
            startInfo.FileName,
            startInfo.ArgumentList,
            startInfo.WorkingDirectory,
            ShouldRemoveEnvironmentVariable,
            effectiveEnvironment,
            dcpPath,
            cancellationToken,
            logger).ConfigureAwait(false);

        logger.LogDebug("{FileName}({ProcessId}) started detached in {WorkingDirectory}", fileName, _process.Id, startInfo.WorkingDirectory);
        return true;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await ChildProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return ChildProcess.ExitCode ?? -1;
    }

    public void Kill(bool entireProcessTree)
    {
        ChildProcess.Kill(entireProcessTree);
    }

    public ValueTask DisposeAsync()
    {
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool ShouldRemoveEnvironmentVariable(string name)
    {
        return IdentityResolver.IdentityEnvVarNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || options.EnvironmentVariableFilter?.Invoke(name) == true;
    }

    // When `aspire start` (or `aspire run --detach`) is used, the CLI spawns a child CLI process
    // which in turn spawns the AppHost. The child must suppress stdout/stderr so it cannot corrupt
    // the parent terminal, and on Unix it must be launched through DCP fork-process so it is placed
    // in a fresh process group/session. Keep that platform ceremony inside this implementation so
    // it can be removed once the BCL can launch detached process groups directly.
    private static Task<DetachedChildProcess> StartDetachedChildProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null,
        string? dcpPath = null,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.FromResult(new DetachedChildProcess(StartWindows(fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables)));
        }

        if (dcpPath is null)
        {
            throw new InvalidOperationException("Unix detached process launch requires a DCP executable path.");
        }

        return StartUnix(dcpPath, fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables, cancellationToken, logger);
    }

    private sealed class DetachedChildProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Process? _exitMonitorProcess;

        public DetachedChildProcess(Process process, Process? exitMonitorProcess = null)
            : this(process, exitMonitorProcess, GetStartTime(process))
        {
        }

        internal DetachedChildProcess(Process process, Process? exitMonitorProcess, DateTimeOffset? startTime)
        {
            _process = process;
            _exitMonitorProcess = exitMonitorProcess;
            Id = process.Id;
            StartTime = startTime;
        }

        public int Id { get; }

        public bool HasExited => _exitMonitorProcess?.HasExited ?? _process.HasExited;

        // DCP fork-process returns a PID before the detached Unix child reaches AppHostLauncher.
        // The child can exit in that gap, so cache a nullable start time instead of re-querying a
        // potentially dead PID and masking the DCP monitor's exit code with Process.StartTime failures.
        public DateTimeOffset? StartTime { get; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return _exitMonitorProcess is not null
                ? _exitMonitorProcess.WaitForExitAsync(cancellationToken)
                : _process.WaitForExitAsync(cancellationToken);
        }

        public int? ExitCode
        {
            get
            {
                if (_exitMonitorProcess is { HasExited: true } exitMonitorProcess)
                {
                    return exitMonitorProcess.ExitCode;
                }

                try
                {
                    return _process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    // Process.GetProcessById returns a handle that can observe HasExited, but .NET only
                    // exposes ExitCode for Process instances started by this object. DCP fork-process
                    // gives us a PID for an already-detached child; when the DCP monitor process is not
                    // available, the real exit code cannot be recovered from this handle.
                    return null;
                }
            }
        }

        public void Kill(bool entireProcessTree)
        {
            _process.Kill(entireProcessTree);
        }

        public void Dispose()
        {
            _process.Dispose();
            _exitMonitorProcess?.Dispose();
        }

        private static DateTimeOffset? GetStartTime(Process process)
        {
            try
            {
                return ProcessStartTimeHelper.TryGetProcessStartTime(process.Id) ?? new DateTimeOffset(process.StartTime);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return null;
            }
        }
    }
}
