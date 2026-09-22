// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Agents;

/// <summary>
/// Owns discovery and native configuration for a client environment without writing during discovery.
/// </summary>
internal interface IAgentEnvironmentScanner
{
    /// <summary>
    /// Gets the stable command-line identifier for this configuration environment.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the display name shared by selection and configuration results.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Adds detected clients to the context without changing files or configuration.
    /// </summary>
    /// <param name="context">The workspace boundary and collected client detections.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Gets edits for this selected environment, even when no client was detected.
    /// </summary>
    IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request);

    /// <summary>
    /// Gets user-hook configuration for detected supported clients, independently of selection.
    /// </summary>
    AgentHookConfiguration? GetHookConfiguration(AgentInitRequest request) => null;
}

/// <summary>
/// Read-only evidence that a client is present.
/// </summary>
internal sealed record AgentClientDetection(AgentClientKind Client, string? Version, bool IsInsiders);
