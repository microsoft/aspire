// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Backchannel;

public class AppHostConnectionResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ResolveConnectionAsync_WithExplicitProjectFile_PreservesFastPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectFile = CreateProjectFile(workspace.WorkspaceRoot, "TestAppHost", "TestAppHost.csproj");
        var interactionService = new TestInteractionService();
        var projectLocatorInvoked = false;
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
            {
                projectLocatorInvoked = true;
                throw new ProjectLocatorException("should not be invoked", ProjectLocatorFailureReason.ProjectFileDoesntExist);
            }
        };
        var resolver = new AppHostConnectionResolver(
            new TestAuxiliaryBackchannelMonitor(),
            interactionService,
            projectLocator,
            executionContext,
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            projectFile,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(projectLocatorInvoked);
        Assert.False(result.Success);
        Assert.False(result.IsProjectResolutionError);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.AppHostNotRunningAtPath, Path.Combine("TestAppHost", "TestAppHost.csproj")),
            result.ErrorMessage);
    }

    [Fact]
    public void IsProjectResolutionError_WithNonProjectResolutionExitCode_ReturnsFalse()
    {
        var result = new AppHostConnectionResult
        {
            ErrorMessage = "failed",
            ExitCode = ExitCodeConstants.FailedToCreateNewProject
        };

        Assert.False(result.IsProjectResolutionError);
    }

    [Fact]
    public async Task ResolveConnectionAsync_WithExplicitDirectoryAndMultipleAppHosts_ReturnsDirectorySpecificError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("Apps");
        var interactionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                throw new ProjectLocatorException("multiple", ProjectLocatorFailureReason.MultipleProjectFilesFound)
        };
        var resolver = new AppHostConnectionResolver(
            new TestAuxiliaryBackchannelMonitor(),
            interactionService,
            projectLocator,
            executionContext,
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            new FileInfo(appHostDirectory.FullName),
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.IsProjectResolutionError);
        Assert.Equal(ExitCodeConstants.FailedToFindProject, result.ExitCode);
        Assert.Equal(InteractionServiceStrings.ProjectOptionSpecifiedDirectoryContainsMultipleAppHosts, result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveConnectionAsync_WithExplicitDirectoryAndNoAppHosts_ReturnsDirectorySpecificError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("Apps");
        var interactionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                throw new ProjectLocatorException("missing", ProjectLocatorFailureReason.ProjectFileDoesntExist)
        };
        var resolver = new AppHostConnectionResolver(
            new TestAuxiliaryBackchannelMonitor(),
            interactionService,
            projectLocator,
            executionContext,
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            new FileInfo(appHostDirectory.FullName),
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.IsProjectResolutionError);
        Assert.Equal(ExitCodeConstants.FailedToFindProject, result.ExitCode);
        Assert.Equal(InteractionServiceStrings.ProjectOptionSpecifiedDirectoryContainsNoAppHosts, result.ErrorMessage);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        var settingsDirectory = workingDirectory.CreateSubdirectory(".aspire");
        var hivesDirectory = settingsDirectory.CreateSubdirectory("hives");
        var cacheDirectory = settingsDirectory.CreateSubdirectory("cache");
        var sdksDirectory = workingDirectory.CreateSubdirectory("sdks");
        var logsDirectory = workingDirectory.CreateSubdirectory("logs");
        var logFilePath = Path.Combine(logsDirectory.FullName, "test.log");

        return new CliExecutionContext(
            workingDirectory,
            hivesDirectory,
            cacheDirectory,
            sdksDirectory,
            logsDirectory,
            logFilePath,
            homeDirectory: workingDirectory);
    }

    private static FileInfo CreateProjectFile(DirectoryInfo workingDirectory, string directoryName, string fileName)
    {
        var directory = workingDirectory.CreateSubdirectory(directoryName);
        var projectFile = new FileInfo(Path.Combine(directory.FullName, fileName));
        File.WriteAllText(projectFile.FullName, "<Project />");
        return projectFile;
    }
}
