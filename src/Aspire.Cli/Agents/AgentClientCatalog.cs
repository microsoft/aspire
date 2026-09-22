// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Lists configuration environments without probing or configuring them.
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
            new(CopilotAgentEnvironmentScanner.ClientId, AgentCommandStrings.Environment_Copilot, copilot),
            new(VsCodeAgentEnvironmentScanner.ClientId, AgentCommandStrings.Environment_VsCode, vsCode),
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
/// An immutable configuration-environment identity shared by discovery, selection, and setup.
/// </summary>
internal sealed record AgentClient(string Id, string DisplayName, IAgentEnvironmentScanner Environment)
{
    public override string ToString() => Id;
}
