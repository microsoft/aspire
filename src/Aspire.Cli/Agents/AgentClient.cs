// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes an agent client that Aspire can configure during <c>aspire agent init</c>.
/// </summary>
/// <param name="Name">The user-facing client name.</param>
/// <param name="SupportedAssetKinds">The agent asset kinds supported by the client.</param>
internal sealed record AgentClient(
    string Name,
    IReadOnlySet<AgentAssetKind> SupportedAssetKinds)
{
    /// <summary>
    /// GitHub Copilot CLI.
    /// </summary>
    public static AgentClient CopilotCli { get; } = new(
        "GitHub Copilot CLI",
        new HashSet<AgentAssetKind>
        {
            AgentAssetKind.Skills,
        });

    /// <summary>
    /// Anthropic Claude Code.
    /// </summary>
    public static AgentClient ClaudeCode { get; } = new(
        "Claude Code",
        new HashSet<AgentAssetKind>
        {
            AgentAssetKind.Skills,
        });

    /// <summary>
    /// Visual Studio Code.
    /// </summary>
    public static AgentClient VsCode { get; } = new(
        "VS Code",
        new HashSet<AgentAssetKind>
        {
            AgentAssetKind.Skills,
        });

    /// <summary>
    /// OpenCode.
    /// </summary>
    public static AgentClient OpenCode { get; } = new(
        "OpenCode",
        new HashSet<AgentAssetKind>
        {
            AgentAssetKind.Skills,
        });

    /// <summary>
    /// Gets whether this client supports the specified agent asset type.
    /// </summary>
    public bool Supports(AgentAssetKind assetKind) => SupportedAssetKinds.Contains(assetKind);
}
