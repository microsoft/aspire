// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents an agent client.
/// </summary>
internal sealed class AgentClient
{
    /// <summary>GitHub Copilot CLI.</summary>
    public static readonly AgentClient CopilotCli = new("GitHub Copilot CLI", [AgentAssetKind.Skill, AgentAssetKind.Extension]);

    /// <summary>Anthropic Claude Code.</summary>
    public static readonly AgentClient ClaudeCode = new("Claude Code", [AgentAssetKind.Skill]);

    /// <summary>Visual Studio Code.</summary>
    public static readonly AgentClient VsCode = new("VS Code", [AgentAssetKind.Skill]);

    /// <summary>OpenCode.</summary>
    public static readonly AgentClient OpenCode = new("OpenCode", [AgentAssetKind.Skill]);

    private AgentClient(string name, IReadOnlyList<AgentAssetKind> supportedAssetKinds)
    {
        Name = name;
        SupportedAssetKinds = supportedAssetKinds;
    }

    public string Name { get; }

    public IReadOnlyList<AgentAssetKind> SupportedAssetKinds { get; }

    public override string ToString() => Name;
}
