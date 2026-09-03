// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents an MCP server that can be selected for configuration by <c>aspire agent init --mcps</c>.
/// This is a selectable definition, distinct from the <see cref="AgentEnvironmentApplicator"/>
/// instances scanners discover for each detected agent environment (the actual configuration
/// targets a selected server is applied to).
/// </summary>
[DebuggerDisplay("Name = {Name}, Description = {Description}")]
internal sealed class McpServerDefinition
{
    /// <summary>
    /// The Aspire MCP server. Unselected by default — MCP configuration is strictly opt-in.
    /// </summary>
    public static readonly McpServerDefinition Aspire = new("aspire", AgentCommandStrings.InitCommand_ConfigureMcpServer);

    private McpServerDefinition(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>
    /// Gets the MCP server name (used for <c>--mcps</c> matching).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description shown in the selection prompt.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Returns whether this definition has the specified name.
    /// </summary>
    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal) => string.Equals(Name, name, comparison);

    /// <summary>
    /// Gets all MCP server definitions the CLI knows how to configure.
    /// </summary>
    public static IReadOnlyList<McpServerDefinition> All { get; } = [Aspire];

    /// <inheritdoc />
    public override string ToString() => Name;
}
