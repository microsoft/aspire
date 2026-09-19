// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Interface for detecting agent environments in the current context.
/// </summary>
internal interface IAgentEnvironmentDetector
{
    /// <summary>
    /// Detects available agent environments by running all registered scanners.
    /// </summary>
    /// <param name="context">The directories to scan.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Read-only evidence of the detected agent clients.</returns>
    Task<IReadOnlyList<AgentClientDetection>> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken);
}
