// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies an agent environment (CLI/editor) that Aspire can configure during <c>aspire agent init</c>.
/// </summary>
[Flags]
internal enum AgentEnvironment
{
    /// <summary>No agent environments.</summary>
    None = 0,

    /// <summary>GitHub Copilot CLI.</summary>
    CopilotCli = 1 << 0,

    /// <summary>GitHub Copilot App.</summary>
    CopilotApp = 1 << 1,

    /// <summary>Anthropic Claude Code.</summary>
    ClaudeCode = 1 << 2,

    /// <summary>Visual Studio Code.</summary>
    VsCode = 1 << 3,

    /// <summary>OpenCode.</summary>
    OpenCode = 1 << 4,
}
