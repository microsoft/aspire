// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.Agents;

public class AgentClientTests
{
    [Fact]
    public void Name_ReturnsDisplayName()
    {
        Assert.Equal("GitHub Copilot CLI", AgentClient.CopilotCli.Name);
        Assert.Equal("Claude Code", AgentClient.ClaudeCode.Name);
        Assert.Equal("VS Code", AgentClient.VsCode.Name);
        Assert.Equal("OpenCode", AgentClient.OpenCode.Name);
    }
}
