// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies an agent client (CLI/editor) that Aspire can configure during <c>aspire agent init</c>.
/// </summary>
internal enum AgentClientKind
{
    /// <summary>GitHub Copilot CLI.</summary>
    CopilotCli,

    /// <summary>GitHub Copilot App.</summary>
    CopilotApp,

    /// <summary>Anthropic Claude Code.</summary>
    ClaudeCode,

    /// <summary>Visual Studio Code.</summary>
    VsCode,

    /// <summary>OpenCode.</summary>
    OpenCode,
}

/// <summary>
/// Provides agent asset capabilities for known clients.
/// </summary>
internal static class AgentClientKindExtensions
{
    /// <summary>
    /// Gets whether the client supports the specified agent asset kind.
    /// </summary>
    public static bool Supports(this AgentClientKind client, AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill or AgentAssetKind.Mcp =>
                client is AgentClientKind.CopilotCli or
                    AgentClientKind.CopilotApp or
                    AgentClientKind.ClaudeCode or
                    AgentClientKind.VsCode or
                    AgentClientKind.OpenCode,
            _ => false,
        };
    }
}
