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

    /// <summary>Anthropic Claude Code.</summary>
    ClaudeCode,

    /// <summary>Visual Studio Code.</summary>
    VsCode,

    /// <summary>OpenCode.</summary>
    OpenCode,
}
