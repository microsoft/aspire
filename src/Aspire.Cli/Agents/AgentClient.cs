// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes an agent client that Aspire can configure during <c>aspire agent init</c>.
/// </summary>
internal sealed class AgentClient
{
    private readonly IReadOnlySet<AgentAssetKind> _supportedAssetKinds;

    private AgentClient(AgentClientKind kind, string name, params AgentAssetKind[] supportedAssetKinds)
    {
        Kind = kind;
        Name = name;
        _supportedAssetKinds = supportedAssetKinds.ToHashSet();
    }

    /// <summary>
    /// GitHub Copilot CLI.
    /// </summary>
    public static AgentClient CopilotCli { get; } = new(
        AgentClientKind.CopilotCli,
        "GitHub Copilot CLI",
        AgentAssetKind.Skill,
        AgentAssetKind.Mcp);

    /// <summary>
    /// GitHub Copilot App.
    /// </summary>
    public static AgentClient CopilotApp { get; } = new(
        AgentClientKind.CopilotApp,
        "GitHub Copilot App",
        AgentAssetKind.Skill,
        AgentAssetKind.Mcp);

    /// <summary>
    /// Anthropic Claude Code.
    /// </summary>
    public static AgentClient ClaudeCode { get; } = new(
        AgentClientKind.ClaudeCode,
        "Claude Code",
        AgentAssetKind.Skill,
        AgentAssetKind.Mcp);

    /// <summary>
    /// Visual Studio Code.
    /// </summary>
    public static AgentClient VsCode { get; } = new(
        AgentClientKind.VsCode,
        "VS Code",
        AgentAssetKind.Skill,
        AgentAssetKind.Mcp);

    /// <summary>
    /// OpenCode.
    /// </summary>
    public static AgentClient OpenCode { get; } = new(
        AgentClientKind.OpenCode,
        "OpenCode",
        AgentAssetKind.Skill,
        AgentAssetKind.Mcp);

    /// <summary>
    /// Gets the stable client kind used by existing client-specific configuration.
    /// </summary>
    public AgentClientKind Kind { get; }

    /// <summary>
    /// Gets the user-facing client name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets whether this client supports the specified agent asset kind.
    /// </summary>
    public bool Supports(AgentAssetKind assetKind) => _supportedAssetKinds.Contains(assetKind);
}
