// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes an agent client that Aspire can configure.
/// </summary>
internal sealed class AgentClient
{
    public static AgentClient CopilotCli { get; } = new(nameof(CopilotCli), "GitHub Copilot CLI");

    public static AgentClient CopilotApp { get; } = new(nameof(CopilotApp), "GitHub Copilot App");

    public static AgentClient ClaudeCode { get; } = new(nameof(ClaudeCode), "Claude Code");

    public static AgentClient VsCode { get; } = new(nameof(VsCode), "VS Code");

    public static AgentClient OpenCode { get; } = new(nameof(OpenCode), "OpenCode");

    public static IReadOnlyList<AgentClient> All { get; } = [CopilotCli, CopilotApp, ClaudeCode, VsCode, OpenCode];

    private AgentClient(string name, string displayName)
    {
        Name = name;
        DisplayName = displayName;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public override string ToString() => Name;
}
