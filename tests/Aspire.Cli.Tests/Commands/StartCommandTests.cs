// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Commands;
using Aspire.Cli.Processes;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Commands;

public class StartCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task StartCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_AcceptsNoBuildOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --no-build --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_AcceptsFormatOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --format json --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_AcceptsIsolatedOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --isolated --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_AcceptsTimeoutOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --timeout 240 --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_RejectsInvalidTimeoutOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --timeout 0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.Equal(WaitCommandStrings.TimeoutMustBePositive, Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public async Task StartCommand_WhenAppHostStartupTimesOut_DisplaysTimeoutGuidance()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var interactionService = new TestInteractionService();
        var detachedProcessLauncher = new TestDetachedProcessLauncher();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
        });
        services.RemoveAll<IDetachedProcessLauncher>();
        services.AddSingleton<IDetachedProcessLauncher>(detachedProcessLauncher);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider
        {
            AutoAdvanceAmount = TimeSpan.FromSeconds(10)
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --timeout 37");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToDotnetRunAppHost, exitCode);
        Assert.True(detachedProcessLauncher.Process.Killed);
        Assert.DoesNotContain("--timeout", detachedProcessLauncher.Arguments);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, RunCommandStrings.TimeoutWaitingForAppHost, 37),
            Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public void StartCommand_ForwardsUnmatchedTokensToAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start -- --custom-arg value");

        Assert.Empty(result.Errors);
        Assert.Contains("--custom-arg", result.UnmatchedTokens);
        Assert.Contains("value", result.UnmatchedTokens);
    }

    [Fact]
    public async Task StartCommand_WhenMultipleProjectFilesFound_NonInteractive_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create two real apphost project files in the workspace
        var appHost1Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost1");
        await File.WriteAllTextAsync(Path.Combine(appHost1Dir.FullName, "AppHost1.csproj"), "fake");

        var appHost2Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost2");
        await File.WriteAllTextAsync(Path.Combine(appHost2Dir.FullName, "AppHost2.csproj"), "fake");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Use the real ProjectLocator (default) so it discovers both apphosts
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task StartCommand_WhenMultipleProjectFilesFound_JsonFormat_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create two real apphost project files in the workspace
        var appHost1Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost1");
        await File.WriteAllTextAsync(Path.Combine(appHost1Dir.FullName, "AppHost1.csproj"), "fake");

        var appHost2Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost2");
        await File.WriteAllTextAsync(Path.Combine(appHost2Dir.FullName, "AppHost2.csproj"), "fake");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: false);
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

}
