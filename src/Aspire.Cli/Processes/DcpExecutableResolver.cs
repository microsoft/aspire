// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Shared;

namespace Aspire.Cli.Processes;

/// <summary>
/// Resolves the DCP executable path with an optional bundle layout lease.
/// </summary>
internal static class DcpExecutableResolver
{
    /// <summary>
    /// Tries to resolve the DCP executable path and returns the lease that keeps the selected bundle layout alive.
    /// </summary>
    public static async Task<DcpExecutableResolution?> TryGetDcpExecutableAsync(
        ILayoutDiscovery layoutDiscovery,
        IBundleService bundleService,
        CliExecutionContext executionContext,
        string commandName,
        CancellationToken cancellationToken)
    {
        var layoutLease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", commandName, cancellationToken).ConfigureAwait(false);
        try
        {
            var dcpDirectory = layoutLease?.Layout.GetDcpPath() ??
                layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
            if (dcpDirectory is null)
            {
                layoutLease?.Dispose();
                return null;
            }

            var dcpPath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
            if (!File.Exists(dcpPath))
            {
                layoutLease?.Dispose();
                return null;
            }

            return new DcpExecutableResolution(dcpPath, layoutLease);
        }
        catch
        {
            layoutLease?.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Represents a resolved DCP executable and the lease that must remain alive while it is used.
/// </summary>
internal sealed class DcpExecutableResolution(string executablePath, BundleLayoutLease? layoutLease) : IDisposable
{
    public string ExecutablePath { get; } = executablePath;

    public BundleLayoutLease? LayoutLease { get; } = layoutLease;

    public void Dispose()
    {
        LayoutLease?.Dispose();
    }
}
