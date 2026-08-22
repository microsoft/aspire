// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes an agent client that Aspire can configure during <c>aspire agent init</c>.
/// </summary>
internal sealed class AgentClient
{
    private readonly IReadOnlySet<AgentAssetKind> _supportedAssetKinds;

    private AgentClient(string name, params AgentAssetKind[] supportedAssetKinds)
    {
        Name = name;
        _supportedAssetKinds = supportedAssetKinds.ToHashSet();
    }

    /// <summary>
    /// GitHub Copilot CLI.
    /// </summary>
    public static AgentClient CopilotCli { get; } = new(
        "GitHub Copilot CLI",
        AgentAssetKind.Skills);

    /// <summary>
    /// Anthropic Claude Code.
    /// </summary>
    public static AgentClient ClaudeCode { get; } = new(
        "Claude Code",
        AgentAssetKind.Skills);

    /// <summary>
    /// Visual Studio Code.
    /// </summary>
    public static AgentClient VsCode { get; } = new(
        "VS Code",
        AgentAssetKind.Skills);

    /// <summary>
    /// OpenCode.
    /// </summary>
    public static AgentClient OpenCode { get; } = new(
        "OpenCode",
        AgentAssetKind.Skills);

    /// <summary>
    /// Gets the user-facing client name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets whether this client supports the specified agent asset type.
    /// </summary>
    public bool Supports(AgentAssetKind assetKind) => _supportedAssetKinds.Contains(assetKind);
}
