// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Owns discovery and native configuration for a client environment without writing during discovery.
/// </summary>
internal interface IAgentEnvironmentScanner
{
    /// <summary>
    /// Returns read-only evidence that this environment is available within the workspace boundary.
    /// </summary>
    /// <param name="workingDirectory">The current working directory.</param>
    /// <param name="workspaceRoot">The boundary for project configuration discovery.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Read-only evidence when detected; otherwise, <see langword="null"/>.</returns>
    Task<AgentEnvironmentDetection?> ScanAsync(DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Gets edits for selected clients, including clients that were not detected.
    /// </summary>
    IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request);
}

/// <summary>
/// Read-only evidence that a client is present.
/// </summary>
internal sealed record AgentClientDetection(AgentClient Client, string? Version, bool IsInsiders);

/// <summary>
/// Read-only installation evidence returned without knowledge of catalog entries.
/// </summary>
internal readonly record struct AgentEnvironmentDetection(string? Version, bool IsInsiders);
