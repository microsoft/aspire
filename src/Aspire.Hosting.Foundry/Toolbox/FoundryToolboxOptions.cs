// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Options used when adding a Microsoft Foundry Toolbox resource to a project.
/// </summary>
[AspireDto]
internal sealed class FoundryToolboxOptions
{
    /// <summary>
    /// Gets or sets the optional Toolbox version to reference. When unset, the default
    /// Toolbox version is used.
    /// </summary>
    public string? Version { get; set; }
}

/// <summary>
/// Options for an MCP tool in a Microsoft Foundry Toolbox.
/// </summary>
[AspireDto]
public sealed class FoundryToolboxMcpToolOptions
{
    /// <summary>
    /// Gets or sets the label sent to the MCP server. The Toolbox tool name is used when unset.
    /// </summary>
    public string? ServerLabel { get; set; }

    /// <summary>
    /// Gets or sets a description of the MCP server.
    /// </summary>
    public string? ServerDescription { get; set; }

    /// <summary>
    /// Gets or sets the declared approval policy for tools discovered from the MCP server.
    /// </summary>
    /// <remarks>
    /// The Toolbox publishes this policy as MCP discovery metadata. Applications consuming the
    /// Toolbox remain responsible for enforcing approval before invoking a discovered tool.
    /// </remarks>
    public FoundryToolboxMcpApprovalPolicy? ApprovalPolicy { get; set; }
}

/// <summary>
/// Declares which tools discovered from an MCP server require approval.
/// </summary>
[AspireDto]
public sealed class FoundryToolboxMcpApprovalPolicy
{
    /// <summary>
    /// Gets or sets a policy that applies to every tool exposed by the MCP server.
    /// </summary>
    /// <remarks>
    /// This cannot be combined with <see cref="Always"/> or <see cref="Never"/>.
    /// </remarks>
    public FoundryToolboxMcpGlobalApprovalMode? Global { get; set; }

    /// <summary>
    /// Gets or sets the filter for MCP tools that always require approval.
    /// </summary>
    public FoundryToolboxMcpApprovalFilter? Always { get; set; }

    /// <summary>
    /// Gets or sets the filter for MCP tools that never require approval.
    /// </summary>
    public FoundryToolboxMcpApprovalFilter? Never { get; set; }
}

/// <summary>
/// Selects MCP tools by name or read-only status for an approval policy.
/// </summary>
[AspireDto]
public sealed class FoundryToolboxMcpApprovalFilter
{
    /// <summary>
    /// Gets or sets the names of the MCP tools selected by this filter.
    /// </summary>
    public string[]? ToolNames { get; set; }

    /// <summary>
    /// Gets or sets whether this filter selects read-only or non-read-only MCP tools.
    /// </summary>
    public bool? ReadOnly { get; set; }
}

/// <summary>
/// Declares a global approval requirement for tools discovered from an MCP server.
/// </summary>
public enum FoundryToolboxMcpGlobalApprovalMode
{
    /// <summary>
    /// No discovered tool requires approval.
    /// </summary>
    Never,

    /// <summary>
    /// Every discovered tool requires approval.
    /// </summary>
    Always
}
