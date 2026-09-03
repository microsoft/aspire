// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// A location where a selected <see cref="McpServerDefinition"/> can be applied — for example,
/// writing an MCP config file for a specific detected agent environment (VS Code, Claude Code,
/// GitHub Copilot, etc.). Targets are discovered by <see cref="IAgentEnvironmentScanner"/>
/// implementations and are distinct from the selectable <see cref="McpServerDefinition"/> the
/// user chooses to apply to them.
/// </summary>
internal sealed class McpConfigurationTarget
{
    private readonly AgentEnvironmentApplicator _applicator;

    /// <summary>
    /// Initializes a new instance of <see cref="McpConfigurationTarget"/> that wraps the
    /// underlying <see cref="AgentEnvironmentApplicator"/> discovered by a scanner.
    /// </summary>
    /// <param name="applicator">The applicator that writes the MCP configuration for this target.</param>
    public McpConfigurationTarget(AgentEnvironmentApplicator applicator)
    {
        ArgumentNullException.ThrowIfNull(applicator);
        _applicator = applicator;
    }

    /// <summary>
    /// Gets the human-readable description of this target, shown in prompts and status output.
    /// </summary>
    public string Description => _applicator.Description;

    /// <summary>
    /// Applies the selected MCP server configuration to this target.
    /// </summary>
    public Task ApplyAsync(CancellationToken cancellationToken) => _applicator.ApplyAsync(cancellationToken);
}
