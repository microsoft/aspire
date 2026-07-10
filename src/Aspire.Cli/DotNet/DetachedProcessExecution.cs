// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Processes;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

internal sealed class DetachedProcessExecution(
    IsolatedProcessStartInfo startInfo,
    string fileName,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string?> environment,
    ILogger logger,
    ProcessInvocationOptions options,
    ILayoutDiscovery? layoutDiscovery,
    IBundleService? bundleService,
    CliExecutionContext? executionContext) : IProcessExecution
{
    private DetachedProcess? _process;

    public string FileName => fileName;

    public IReadOnlyList<string> Arguments => arguments;

    public IReadOnlyDictionary<string, string?> EnvironmentVariables => environment;

    public int ProcessId => Process.Id;

    public DateTimeOffset? StartTime => Process.StartTime;

    public bool HasExited => Process.HasExited;

    public int? ExitCode => Process.ExitCode;

    private DetachedProcess Process =>
        _process ?? throw new InvalidOperationException($"{nameof(DetachedProcessExecution)} has not been started. Call {nameof(StartAsync)} first.");

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveEnvironment = environment
            .Where(static kvp => kvp.Value is not null)
            .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);

        string? dcpPath = null;
        if (!OperatingSystem.IsWindows())
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
            if (!File.Exists(dcpPath))
            {
                throw new InvalidOperationException($"Could not find DCP executable at '{dcpPath}'.");
            }

            layoutLease?.AddEnvironment(effectiveEnvironment);
            logger.LogDebug("Launching detached child process through DCP fork-process: {DcpPath}", dcpPath);
        }

        _process = await DetachedProcessLauncher.StartAsync(
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
        await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return Process.ExitCode ?? -1;
    }

    public void Kill(bool entireProcessTree)
    {
        Process.Kill(entireProcessTree);
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
}
