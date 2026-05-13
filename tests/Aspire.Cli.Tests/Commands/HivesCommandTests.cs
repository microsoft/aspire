// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class HivesCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task HivesListCommand_ListsHives()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", "local"));
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", "pr-15573"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("hives list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Contains(outputWriter.Logs, l => l.Contains("local"));
        Assert.Contains(outputWriter.Logs, l => l.Contains("pr-15573"));
    }

    [Fact]
    public async Task HivesDeleteCommand_RemovesHive()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hivePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", "pr-15573");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("hives delete pr-15573");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(Directory.Exists(hivePath));
    }

    [Fact]
    public async Task HivesDeleteCommand_RejectsPathTraversal()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outsidePath = Path.Combine(workspace.WorkspaceRoot.FullName, "outside");
        Directory.CreateDirectory(outsidePath);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("hives delete ../outside");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.True(Directory.Exists(outsidePath));
    }
}
