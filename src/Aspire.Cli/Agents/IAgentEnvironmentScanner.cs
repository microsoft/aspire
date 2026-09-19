// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Discovers one or more agent clients without changing their configuration.
/// </summary>
internal interface IAgentEnvironmentScanner
{
    /// <summary>
    /// Returns read-only evidence of the agent clients found in the scan context.
    /// </summary>
    /// <param name="context">The directories to scan.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Read-only evidence of the detected agent clients.</returns>
    Task<IReadOnlyList<AgentClientDetection>> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken);
}
