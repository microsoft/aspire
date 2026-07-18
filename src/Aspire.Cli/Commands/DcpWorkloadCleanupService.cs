// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class DcpWorkloadCleanupService(
    ILayoutDiscovery layoutDiscovery,
    IBundleService bundleService,
    LayoutProcessRunner layoutProcessRunner,
    CliExecutionContext executionContext,
    ILogger<DcpWorkloadCleanupService> logger)
{
    public async Task<DcpWorkloadCleanupResult> CleanupAsync(string workloadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);

        using var layoutLease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "dcp-cleanup", cancellationToken).ConfigureAwait(false);
        var dcpDirectory = layoutLease?.Layout.GetDcpPath() ??
            layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
        if (dcpDirectory is null)
        {
            logger.LogWarning("Could not find DCP in the Aspire layout.");
            return DcpWorkloadCleanupResult.NotFound();
        }

        var dcpPath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
        if (!File.Exists(dcpPath))
        {
            logger.LogWarning("Could not find DCP executable at '{DcpPath}'.", dcpPath);
            return DcpWorkloadCleanupResult.NotFound();
        }

        int exitCode;
        string output;
        string error;
        try
        {
            (exitCode, output, error) = await layoutProcessRunner.RunAsync(
                dcpPath,
                ["cleanup", workloadId],
                workingDirectory: executionContext.WorkingDirectory.FullName,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to start DCP cleanup process at '{DcpPath}'.", dcpPath);
            return new DcpWorkloadCleanupResult(CliExitCodes.FailedToDotnetRunAppHost, string.Empty, ex.Message, DcpFound: true);
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            logger.LogDebug("DCP cleanup stdout: {Output}", output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            logger.LogDebug("DCP cleanup stderr: {Error}", error.Trim());
        }

        if (exitCode != 0)
        {
            logger.LogWarning("DCP cleanup exited with code {ExitCode}.", exitCode);
        }

        return new DcpWorkloadCleanupResult(exitCode, output, error, DcpFound: true);
    }
}

internal sealed record DcpWorkloadCleanupResult(int ExitCode, string Output, string Error, bool DcpFound)
{
    public static DcpWorkloadCleanupResult NotFound() => new(CliExitCodes.FailedToDotnetRunAppHost, string.Empty, string.Empty, DcpFound: false);
}
