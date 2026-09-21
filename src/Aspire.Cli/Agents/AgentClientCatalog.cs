// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// The supported clients, independent of installation detection.
/// </summary>
internal sealed class AgentClientCatalog
{
    public IReadOnlyList<AgentClientDescriptor> Clients { get; } = Array.AsReadOnly<AgentClientDescriptor>(
    [
        new(AgentClientKind.CopilotCli, "copilot-cli", "GitHub Copilot CLI"),
        new(AgentClientKind.CopilotApp, "copilot-app", "GitHub Copilot App"),
        new(AgentClientKind.VsCode, "vscode", "VS Code"),
        new(AgentClientKind.ClaudeCode, "claude-code", "Claude Code"),
        new(AgentClientKind.OpenCode, "opencode", "OpenCode")
    ]);

    public AgentClientDescriptor Get(AgentClientKind client) => Clients.Single(descriptor => descriptor.Kind == client);
}

/// <summary>
/// Stable selection and presentation metadata for a logical client.
/// </summary>
internal sealed record AgentClientDescriptor(AgentClientKind Kind, string Id, string DisplayName)
{
    public override string ToString() => Id;
}
