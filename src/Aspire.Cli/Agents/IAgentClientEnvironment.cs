// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Owns discovery and native configuration for one or more clients without writing during discovery.
/// </summary>
internal interface IAgentClientEnvironment
{
    /// <summary>
    /// Returns read-only evidence of the agent clients found within the workspace boundary.
    /// </summary>
    /// <param name="clients">The catalog entries associated with this environment.</param>
    /// <param name="workingDirectory">The current working directory.</param>
    /// <param name="workspaceRoot">The boundary for project configuration discovery.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Read-only evidence of the detected agent clients.</returns>
    Task<IReadOnlyList<AgentClientDetection>> ScanAsync(IReadOnlyList<AgentClient> clients, DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Gets edits for selected clients, including clients that were not detected.
    /// </summary>
    IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request);
}

/// <summary>
/// Read-only evidence that a client is present.
/// </summary>
internal sealed record AgentClientDetection(AgentClient Client, string? Version, bool IsInsiders);
