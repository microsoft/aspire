// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Monitors the auxiliary backchannel directory and maintains connections to running AppHost instances.
/// </summary>
internal interface IAuxiliaryBackchannelMonitor
{
    /// <summary>
    /// Gets all active AppHost connections.
    /// </summary>
    IEnumerable<IAppHostAuxiliaryBackchannel> Connections { get; }

    /// <summary>
    /// Gets connections for a specific AppHost hash (prefix).
    /// </summary>
    /// <param name="hash">The AppHost hash.</param>
    /// <returns>All connections for the given hash, or empty if none.</returns>
    IEnumerable<IAppHostAuxiliaryBackchannel> GetConnectionsByHash(string hash);

    /// <summary>
    /// Gets or sets the path to the selected AppHost. When set, this AppHost will be used for MCP operations.
    /// </summary>
    string? SelectedAppHostPath { get; set; }

    /// <summary>
    /// Gets the currently selected AppHost connection based on the selection logic.
    /// Returns the explicitly selected AppHost, or the single in-scope AppHost, or null if none available.
    /// </summary>
    IAppHostAuxiliaryBackchannel? SelectedConnection { get; }

    /// <summary>
    /// Gets the AppHost path of the currently resolved connection, or <c>null</c> if no connection is available.
    /// </summary>
    string? ResolvedAppHostPath => SelectedConnection?.AppHostInfo?.AppHostPath;

    /// <summary>
    /// Gets all connections that are within the scope of the specified working directory.
    /// </summary>
    /// <param name="workingDirectory">The working directory to check against.</param>
    /// <returns>A list of in-scope connections.</returns>
    IReadOnlyList<IAppHostAuxiliaryBackchannel> GetConnectionsForWorkingDirectory(DirectoryInfo workingDirectory);

    /// <summary>
    /// Triggers an immediate scan of the backchannels directory for new/removed AppHosts.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the scan operation.</returns>
    Task ScanAsync(CancellationToken cancellationToken = default);
}
