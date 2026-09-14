// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes an agent client and the asset kinds it supports.
/// </summary>
internal sealed class AgentClient
{
    public static AgentClient CopilotCli { get; } = new(nameof(CopilotCli), "GitHub Copilot CLI", AgentAssetKind.Skill);

    public static AgentClient CopilotApp { get; } = new(nameof(CopilotApp), "GitHub Copilot App", AgentAssetKind.Skill | AgentAssetKind.Extension);

    public static AgentClient ClaudeCode { get; } = new(nameof(ClaudeCode), "Claude Code", AgentAssetKind.Skill);

    public static AgentClient VsCode { get; } = new(nameof(VsCode), "VS Code", AgentAssetKind.Skill);

    public static AgentClient OpenCode { get; } = new(nameof(OpenCode), "OpenCode", AgentAssetKind.Skill);

    public static IReadOnlyList<AgentClient> All { get; } = [CopilotCli, CopilotApp, ClaudeCode, VsCode, OpenCode];

    private AgentClient(string name, string displayName, AgentAssetKind supportedAssetKinds)
    {
        Name = name;
        DisplayName = displayName;
        SupportedAssetKinds = supportedAssetKinds;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public AgentAssetKind SupportedAssetKinds { get; }

    public override string ToString() => Name;
}
