// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.Agents;

public class AgentEnvironmentApplicatorTests
{
    [Fact]
    public async Task ApplyAsync_InvokesCallback()
    {
        var callbackInvoked = false;
        var applicator = new AgentEnvironmentApplicator(
            "Test Environment",
            _ =>
            {
                callbackInvoked = true;
                return Task.CompletedTask;
            });

        await applicator.ApplyAsync(CancellationToken.None);

        Assert.True(callbackInvoked);
    }

    [Fact]
    public async Task ApplyAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken receivedToken = default;
        var applicator = new AgentEnvironmentApplicator(
            "Test Environment",
            ct =>
            {
                receivedToken = ct;
                return Task.CompletedTask;
            });

        await applicator.ApplyAsync(cts.Token);

        Assert.Equal(cts.Token, receivedToken);
    }

    [Fact]
    public void Applicator_HasRequiredProperties()
    {
        var applicator = new AgentEnvironmentApplicator(
            "My Description",
            _ => Task.CompletedTask);

        Assert.Equal("My Description", applicator.Description);
        Assert.Equal(McpInitPromptGroup.AgentEnvironments, applicator.PromptGroup);
        Assert.Equal(0, applicator.Priority);
    }

    [Fact]
    public void Applicator_AllowsCustomPromptGroup()
    {
        var applicator = new AgentEnvironmentApplicator(
            "My Description",
            _ => Task.CompletedTask,
            promptGroup: McpInitPromptGroup.AdditionalOptions,
            priority: 5);

        Assert.Equal("My Description", applicator.Description);
        Assert.Equal(McpInitPromptGroup.AdditionalOptions, applicator.PromptGroup);
        Assert.Equal(5, applicator.Priority);
    }
}
