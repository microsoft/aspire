// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class ExtensionInternalCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ExtensionInternalCommandIsHidden()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<ExtensionInternalCommand>();
        
        Assert.True(command.Hidden);
    }

    [Fact]
    public async Task ExtensionInternalCommand_WithHelpArgument_ReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("extension --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ExtensionInternalCommand_WithNoSubcommand_ReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("extension");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task GetAppHostsCommand_WithSingleProject_ReturnsSuccessWithValidJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "MyApp.AppHost.csproj"));
        
        var capturedOutput = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new SingleProjectFileProjectLocator(projectFile);
            options.OutputTextWriter = capturedOutput;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("extension get-apphosts");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Join all captured output and deserialize
        var allOutput = string.Join(string.Empty, capturedOutput.Logs);

        // Verify JSON is valid and deserializable
        AppHostProjectSearchResultPoco? searchResult;
        try
        {
            searchResult = JsonSerializer.Deserialize(allOutput, BackchannelJsonSerializerContext.Default.AppHostProjectSearchResultPoco);
        }
        catch (JsonException ex)
        {
            outputHelper.WriteLine($"Failed to deserialize JSON. Raw output: {allOutput}");
            throw new JsonException($"Failed to deserialize JSON: {allOutput}", ex);
        }

        Assert.NotNull(searchResult);
        Assert.Equal(projectFile.FullName, searchResult.SelectedProjectFile);
        Assert.Single(searchResult.AllProjectFileCandidates);
        Assert.Equal(projectFile.FullName, searchResult.AllProjectFileCandidates[0]);
    }

    [Fact]
    public async Task GetAppHostsCommand_WithMultipleProjects_ReturnsSuccessWithAllCandidates()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile1 = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App1.AppHost.csproj"));
        var projectFile2 = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App2.AppHost.csproj"));

        var capturedOutput = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new MultipleProjectsProjectLocator([projectFile1, projectFile2]);
            options.OutputTextWriter = capturedOutput;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("extension get-apphosts");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Join all captured output and deserialize
        var allOutput = string.Join(string.Empty, capturedOutput.Logs);

        // Verify JSON is valid and deserializable
        AppHostProjectSearchResultPoco? searchResult;
        try
        {
            searchResult = JsonSerializer.Deserialize(allOutput, BackchannelJsonSerializerContext.Default.AppHostProjectSearchResultPoco);
        }
        catch (JsonException ex)
        {
            outputHelper.WriteLine($"Failed to deserialize JSON. Raw output: {allOutput}");
            throw new JsonException($"Failed to deserialize JSON: {allOutput}", ex);
        }

        Assert.NotNull(searchResult);
        Assert.Null(searchResult.SelectedProjectFile);
        Assert.Equal(2, searchResult.AllProjectFileCandidates.Count);
        Assert.Contains(projectFile1.FullName, searchResult.AllProjectFileCandidates);
        Assert.Contains(projectFile2.FullName, searchResult.AllProjectFileCandidates);
    }

    [Fact]
    public async Task GetAppHostsCommand_WithNoProjects_ReturnsFailureExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new NoProjectFileProjectLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("extension get-apphosts");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task GetAppHostsCommand_WhenProjectLocatorThrows_ReturnsFailureExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new ThrowingProjectLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("extension get-apphosts");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task GetAppHostsCommand_WithHelpArgument_ReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("extension get-apphosts --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    // Test helper classes

    private sealed class SingleProjectFileProjectLocator : IProjectLocator
    {
        private readonly FileInfo _projectFile;

        public SingleProjectFileProjectLocator(FileInfo projectFile)
        {
            _projectFile = projectFile;
        }

        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile, 
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            var result = new AppHostProjectSearchResult(_projectFile, [_projectFile]);
            return Task.FromResult(result);
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            return Task.FromResult<FileInfo?>(_projectFile);
        }

        public Task<AppHostProjectSearchResult> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile, 
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindSolutionFileAsync(
            FileInfo? solutionFile,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }

    private sealed class MultipleProjectsProjectLocator : IProjectLocator
    {
        private readonly List<FileInfo> _projectFiles;

        public MultipleProjectsProjectLocator(List<FileInfo> projectFiles)
        {
            _projectFiles = projectFiles;
        }

        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile, 
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            var result = new AppHostProjectSearchResult(null, _projectFiles);
            return Task.FromResult(result);
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            return Task.FromResult<FileInfo?>(null);
        }

        public Task<AppHostProjectSearchResult> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile, 
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindSolutionFileAsync(
            FileInfo? solutionFile,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }

    private sealed class NoProjectFileProjectLocator : IProjectLocator
    {
        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile, 
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new ProjectLocatorException("No AppHost project found.", ProjectLocatorFailureReason.NoProjectFileFound);
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new ProjectLocatorException("No AppHost project found.", ProjectLocatorFailureReason.NoProjectFileFound);
        }

        public Task<AppHostProjectSearchResult> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile, 
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile, 
            bool createSettingsFile, 
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindSolutionFileAsync(
            FileInfo? solutionFile,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }

    private sealed class ThrowingProjectLocator : IProjectLocator
    {
        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile,
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Something went wrong");
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(
            FileInfo? projectFile,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Something went wrong");
        }

        public Task<AppHostProjectSearchResult> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile,
            MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindServiceProjectFileAsync(
            FileInfo? projectFile,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> UseOrFindSolutionFileAsync(
            FileInfo? solutionFile,
            bool createSettingsFile,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }
}
