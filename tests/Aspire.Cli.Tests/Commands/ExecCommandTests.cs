// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class ExecCommandTests
{
    private readonly ITestOutputHelper _outputHelper;
    public ExecCommandTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
    }

    [Fact]
    public async Task ExecCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, _outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var invokeConfiguration = new InvocationConfiguration();
        invokeConfiguration.Output = new TestOutputTextWriter(_outputHelper);

        var result = command.Parse("exec --help");

        var exitCode = await result.InvokeAsync(invokeConfiguration).DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task ExecCommand_WhenNoProjectFileFound_ReturnsFailedToFindProject()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, _outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.ExecCommandEnabled];
            options.ProjectLocatorFactory = _ => new NoProjectFileProjectLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("exec --resource api cmd");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task ExecCommand_WhenMultipleProjectFilesFound_ReturnsFailedToFindProject()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, _outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.ExecCommandEnabled];
            options.ProjectLocatorFactory = _ => new MultipleProjectFilesProjectLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("exec --resource api cmd");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task ExecCommand_WhenProjectFileDoesNotExist_ReturnsFailedToFindProject()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, _outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.ExecCommandEnabled];
            options.ProjectLocatorFactory = _ => new ProjectFileDoesNotExistLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("exec --resource api cmd");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task ExecCommand_WhenFeatureFlagEnabled_CommandAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, _outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.ExecCommandEnabled];
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var invokeConfiguration = new InvocationConfiguration();
        var testOutputWriter = new TestOutputTextWriter(_outputHelper);
        invokeConfiguration.Output = testOutputWriter;

        var result = command.Parse("exec --help");

        var exitCode = await result.InvokeAsync(invokeConfiguration).DefaultTimeout();

        // Should succeed because exec command is registered when feature flag is enabled
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task ExecCommand_WhenTargetResourceNotSpecified_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, _outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var invokeConfiguration = new InvocationConfiguration();
        var testOutputWriter = new TestOutputTextWriter(_outputHelper);
        invokeConfiguration.Output = testOutputWriter;

        var result = command.Parse("exec --apphost test.csproj echo hello");

        var exitCode = await result.InvokeAsync(invokeConfiguration).DefaultTimeout();
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);

        // attempt to find app host should not happen
        Assert.DoesNotContain(testOutputWriter.Logs, x => x.Contains(InteractionServiceStrings.FindingAppHosts));
    }

    [Fact]
    public async Task ExecCommand_ExecutesSuccessfully()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, _outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.ExecCommandEnabled];
            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, runnerOptions, cancellationToken) =>
                {
                    var backchannel = new TestAppHostBackchannel();
                    backchannelCompletionSource?.SetResult(backchannel);
                    return Task.FromResult(0);
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("exec --apphost test.csproj --resource myresource --command echo");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    private sealed class NoProjectFileProjectLocator : Aspire.Cli.Projects.IProjectLocator
    {
        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("No project file found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.NoProjectFileFound);
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("No project file found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.NoProjectFileFound);
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }

    private sealed class MultipleProjectFilesProjectLocator : Aspire.Cli.Projects.IProjectLocator
    {
        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Multiple project files found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.MultipleProjectFilesFound);
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Multiple project files found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.MultipleProjectFilesFound);
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }

    private sealed class ProjectFileDoesNotExistLocator : Aspire.Cli.Projects.IProjectLocator
    {
        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Project file does not exist.", Aspire.Cli.Projects.ProjectLocatorFailureReason.ProjectFileDoesntExist);
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Project file does not exist.", Aspire.Cli.Projects.ProjectLocatorFailureReason.ProjectFileDoesntExist);
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }
}
