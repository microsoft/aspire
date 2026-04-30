// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class StopCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task StopCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task StopCommand_RejectsPositionalResourceArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop myresource");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task StopCommand_WithInvalidExplicitAppHost_ReturnsFailedToFindProject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    throw new ProjectLocatorException("Project file does not exist.", ProjectLocatorFailureReason.ProjectFileDoesntExist)
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --apphost missing-directory");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task StopCommand_WithExplicitAppHost_UsesProjectLocatorResolution()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectLocatorInvoked = false;
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("some-directory");
        var resolvedProjectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Resolved.AppHost", "Resolved.AppHost.csproj"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (projectFile, _, _, _) =>
                {
                    projectLocatorInvoked = true;
                    return Task.FromResult(new AppHostProjectSearchResult(resolvedProjectFile, [resolvedProjectFile]));
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"stop --apphost \"{appHostDirectory.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.True(projectLocatorInvoked);
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        var displayedMessage = Assert.Single(interactionService.DisplayedMessages);
        Assert.Equal(
            string.Format(SharedCommandStrings.AppHostNotRunningAtPath, Path.Combine("Resolved.AppHost", "Resolved.AppHost.csproj")),
            displayedMessage.Message);
    }
}
