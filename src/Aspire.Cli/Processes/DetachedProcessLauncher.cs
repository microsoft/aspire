// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

internal interface IDetachedProcessLauncher
{
    Task<DetachedProcess> StartAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null,
        CancellationToken cancellationToken = default);
}

internal sealed class DetachedProcess : IDisposable
{
    private readonly Process _process;
    private readonly Process? _exitMonitorProcess;

    public DetachedProcess(Process process, Process? exitMonitorProcess = null)
        : this(process, exitMonitorProcess, GetStartTime(process))
    {
    }

    internal DetachedProcess(Process process, Process? exitMonitorProcess, DateTimeOffset? startTime)
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

internal sealed class DefaultDetachedProcessLauncher(
    ILayoutDiscovery layoutDiscovery,
    IBundleService bundleService,
    CliExecutionContext executionContext,
    ILogger<DefaultDetachedProcessLauncher> logger) : IDetachedProcessLauncher
{
    public async Task<DetachedProcess> StartAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return await DetachedProcessLauncher.StartAsync(
                fileName,
                arguments,
                workingDirectory,
                shouldRemoveEnvironmentVariable,
                additionalEnvironmentVariables,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        using var layoutLease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "dcp-fork-process", cancellationToken).ConfigureAwait(false);
        var dcpDirectory = layoutLease?.Layout.GetDcpPath() ??
            layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
        if (dcpDirectory is null)
        {
            throw new InvalidOperationException("Could not find DCP in the Aspire layout.");
        }

        var dcpPath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
        if (!File.Exists(dcpPath))
        {
            throw new InvalidOperationException($"Could not find DCP executable at '{dcpPath}'.");
        }

        Dictionary<string, string> effectiveEnvironment = additionalEnvironmentVariables is null
            ? []
            : new Dictionary<string, string>(additionalEnvironmentVariables);
        layoutLease?.AddEnvironment(effectiveEnvironment);

        logger.LogDebug("Launching detached child process through DCP fork-process: {DcpPath}", dcpPath);
        return await DetachedProcessLauncher.StartAsync(
            fileName,
            arguments,
            workingDirectory,
            shouldRemoveEnvironmentVariable,
            effectiveEnvironment,
            dcpPath,
            cancellationToken,
            logger).ConfigureAwait(false);
    }
}

// ============================================================================
// DetachedProcessLauncher — Platform-aware child process launcher for --detach
// ============================================================================
//
// When `aspire start` (or `aspire run --detach`) is used, the CLI spawns a child CLI process which
// in turn spawns the AppHost (the "grandchild"). Two constraints must hold:
//
// 1. The child's stdout/stderr must NOT appear on the parent's console.
//    The parent renders its own summary UX (dashboard URL, PID, log path) and
//    if the child's output (spinners, "Press CTRL+C", etc.) bleeds through, it
//    corrupts the parent's terminal — and breaks E2E tests that pattern-match
//    on the parent's output.
//
// 2. The child CLI must not remain in the parent's process group/session. If it does,
//    shells and tools that clean up the launcher's process group can also tear down the
//    detached AppHost process tree.
//
// These constraints are platform-specific:
//
//   Windows: P/Invoke CreateProcess with CREATE_NEW_CONSOLE, STARTUPINFOEX,
//   SW_HIDE, and an explicit PROC_THREAD_ATTRIBUTE_HANDLE_LIST. Child
//   stdout/stderr go to the NUL device and only the NUL handle is inheritable.
//
//   Linux/macOS: delegate the actual detached child spawn to DCP fork-process.
//   Current .NET ProcessStartInfo cannot request a fresh Unix session/process group
//   for the launched process; DCP applies setsid before exec. Keep this shim isolated
//   so it can be removed once the underlying process-group support is available
//   through the BCL.
//

/// <summary>
/// Launches a child process with stdout/stderr suppressed and no handle/fd
/// inheritance to grandchild processes. Used by <c>aspire start</c>.
/// </summary>
internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Starts a detached child process with stdout/stderr going to the null device
    /// and no inheritable handles/fds leaking to grandchildren.
    /// </summary>
    /// <param name="fileName">The executable path (e.g. dotnet or the native CLI).</param>
    /// <param name="arguments">The command-line arguments for the child process.</param>
    /// <param name="workingDirectory">The working directory for the child process.</param>
    /// <param name="shouldRemoveEnvironmentVariable">Optional predicate that returns <see langword="true" /> for environment variable names that should be removed from the child process.</param>
    /// <param name="additionalEnvironmentVariables">Optional dictionary of environment variables to add to the child process without mutating the parent.</param>
    /// <param name="dcpPath">The DCP executable path used for Unix detached launches.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="logger">Optional logger used for Unix DCP fork-process diagnostics.</param>
    /// <returns>A <see cref="DetachedProcess"/> object representing the launched child.</returns>
    public static Task<DetachedProcess> StartAsync(
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
            return Task.FromResult(new DetachedProcess(StartWindows(fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables)));
        }

        if (dcpPath is null)
        {
            throw new InvalidOperationException("Unix detached process launch requires a DCP executable path.");
        }

        return StartUnix(dcpPath, fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables, cancellationToken, logger);
    }
}
