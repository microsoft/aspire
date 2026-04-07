// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class McpCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task McpCommand_WithoutSubcommand_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("mcp");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task McpCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("mcp --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task McpStartCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("mcp start --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task McpCommandExistsInRootCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var mcpCommand = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "mcp");

        Assert.NotNull(mcpCommand);
        Assert.Equal("mcp", mcpCommand.Name);
    }

    [Fact]
    public async Task McpCommandHasStartSubcommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var mcpCommand = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "mcp");

        Assert.NotNull(mcpCommand);
        var startCommand = mcpCommand.Subcommands.FirstOrDefault(c => c.Name == "start");
        Assert.NotNull(startCommand);
        Assert.Equal("start", startCommand.Name);
    }

    [Fact]
    public async Task McpCommandIsVisible()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var mcpCommand = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "mcp");

        Assert.NotNull(mcpCommand);
        Assert.False(mcpCommand.Hidden, "The mcp command should be visible (contains tools and call subcommands)");
    }

    [Fact]
    public async Task AgentCommand_WithoutSubcommand_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task AgentCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AgentMcpCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent mcp --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AgentInitCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AgentCommandExistsInRootCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var agentCommand = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "agent");

        Assert.NotNull(agentCommand);
        Assert.Equal("agent", agentCommand.Name);
        Assert.False(agentCommand.Hidden, "The agent command should not be hidden");
    }

    [Fact]
    public async Task AgentCommandHasMcpSubcommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var agentCommand = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "agent");

        Assert.NotNull(agentCommand);
        var mcpCommand = agentCommand.Subcommands.FirstOrDefault(c => c.Name == "mcp");
        Assert.NotNull(mcpCommand);
        Assert.Equal("mcp", mcpCommand.Name);
    }

    [Fact]
    public async Task AgentCommandHasInitSubcommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var agentCommand = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "agent");

        Assert.NotNull(agentCommand);
        var initCommand = agentCommand.Subcommands.FirstOrDefault(c => c.Name == "init");
        Assert.NotNull(initCommand);
        Assert.Equal("init", initCommand.Name);
    }
}
