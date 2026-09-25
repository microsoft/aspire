// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Tests.Telemetry;

public class AgentTelemetryInvocationTests
{
    [Theory]
    [InlineData("agent telemetry")]
    [InlineData("agent telemetry --event-type skill_invocation")]
    [InlineData("agent telemetry --skill-name aspire --timestamp 2026-01-01T00:00:00Z")]
    public void Matches_ReturnsTrue_ForAgentTelemetryInvocation(string commandLine)
    {
        Assert.True(AgentTelemetryInvocation.Matches(commandLine.Split(' ')));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("agent mcp")]
    [InlineData("agent init")]
    [InlineData("telemetry")]
    [InlineData("run")]
    [InlineData("--debug agent telemetry")]
    [InlineData("config set agent telemetry")]
    public void Matches_ReturnsFalse_ForOtherInvocations(string commandLine)
    {
        Assert.False(AgentTelemetryInvocation.Matches(commandLine.Split(' ')));
    }

    [Fact]
    public void Matches_ReturnsFalse_ForEmptyArgs()
    {
        Assert.False(AgentTelemetryInvocation.Matches([]));
    }

    [Fact]
    public void Matches_ReturnsFalse_ForNullArgs()
    {
        Assert.False(AgentTelemetryInvocation.Matches(null));
    }
}
