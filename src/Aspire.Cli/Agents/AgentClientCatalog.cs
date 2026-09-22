// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.VsCode;

namespace Aspire.Cli.Agents;

/// <summary>
/// Associates client identities with their environment implementations without probing or configuring them.
/// </summary>
internal sealed class AgentClientCatalog
{
    public AgentClientCatalog(
        CopilotAgentEnvironmentScanner copilot,
        VsCodeAgentEnvironmentScanner vsCode,
        ClaudeCodeAgentEnvironmentScanner claudeCode,
        OpenCodeAgentEnvironmentScanner openCode)
        : this(
        [
            new(CopilotAgentEnvironmentScanner.CliClientId, "GitHub Copilot CLI", copilot),
            new(CopilotAgentEnvironmentScanner.AppClientId, "GitHub Copilot App", copilot),
            new(VsCodeAgentEnvironmentScanner.ClientId, "VS Code", vsCode),
            new(ClaudeCodeAgentEnvironmentScanner.ClientId, "Claude Code", claudeCode),
            new(OpenCodeAgentEnvironmentScanner.ClientId, "OpenCode", openCode)
        ])
    {
    }

    internal AgentClientCatalog(IEnumerable<AgentClient> clients)
    {
        Clients = Array.AsReadOnly(clients.ToArray());
    }

    public IReadOnlyList<AgentClient> Clients { get; }
}

/// <summary>
/// An immutable client identity shared by discovery, selection, and configuration.
/// </summary>
internal sealed record AgentClient(string Id, string DisplayName, IAgentClientEnvironment Environment)
{
    public override string ToString() => Id;
}
