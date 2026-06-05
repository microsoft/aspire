// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Defines groupings for MCP initialization prompts.
/// </summary>
internal sealed class McpInitPromptGroup
{
    /// <summary>
    /// Group for agent environment configurations (VS Code, Copilot CLI, etc.).
    /// </summary>
    public static readonly McpInitPromptGroup AgentEnvironments = new("AgentEnvironments", priority: 0);

    /// <summary>
    /// Group for additional optional configurations (AGENTS.md, Playwright, etc.).
    /// </summary>
    public static readonly McpInitPromptGroup AdditionalOptions = new("AdditionalOptions", priority: 1);

    private McpInitPromptGroup(string name, int priority)
    {
        Name = name;
        Priority = priority;
    }

    /// <summary>
    /// Gets the internal name of the prompt group.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the priority for ordering groups (lower numbers first).
    /// </summary>
    public int Priority { get; }
}
