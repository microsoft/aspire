// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies a detected agent client independently of its configuration environment.
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
