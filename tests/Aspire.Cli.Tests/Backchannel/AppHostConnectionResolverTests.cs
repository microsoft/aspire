// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
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
            TestHelpers.CreateInteractiveHostEnvironment(),
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
    public async Task ResolveConnectionAsync_WithExplicitProjectFile_DeletesDeadPidSocketAndReturnsNotRunning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectFile = CreateProjectFile(workspace.WorkspaceRoot, "TestAppHost", "TestAppHost.csproj");
        var socketPath = CreateMatchingSocketFile(projectFile.FullName, workspace.WorkspaceRoot, int.MaxValue - 1);
        var resolver = new AppHostConnectionResolver(
            new TestAuxiliaryBackchannelMonitor(),
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            projectFile,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.AppHostNotRunningAtPath, Path.Combine("TestAppHost", "TestAppHost.csproj")),
            result.ErrorMessage);
        Assert.False(File.Exists(socketPath));
    }

    [Fact]
    public void IsProjectResolutionError_WithNonProjectResolutionExitCode_ReturnsFalse()
    {
        var result = new AppHostConnectionResult
        {
            ErrorMessage = "failed",
            ExitCode = CliExitCodes.FailedToCreateNewProject
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
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            new FileInfo(appHostDirectory.FullName),
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.IsProjectResolutionError);
        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
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
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            new FileInfo(appHostDirectory.FullName),
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.IsProjectResolutionError);
        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
        Assert.Equal(InteractionServiceStrings.ProjectOptionSpecifiedDirectoryContainsNoAppHosts, result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveConnectionAsync_NonInteractiveWithOnlyOutOfScopeAppHosts_ReturnsNotFoundError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket-other", new TestAppHostAuxiliaryBackchannel { IsInScope = false });

        var resolver = new AppHostConnectionResolver(
            monitor,
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateNonInteractiveHostEnvironment(),
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            projectFile: null,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.IsProjectResolutionError);
        Assert.Equal(SharedCommandStrings.AppHostNotRunning, result.ErrorMessage);
        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
    }

    [Fact]
    public async Task ResolveConnectionAsync_NonInteractiveWithMultipleInScopeAppHosts_ReturnsActionableError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket-one", new TestAppHostAuxiliaryBackchannel { IsInScope = true });
        monitor.AddConnection("hash2", "socket-two", new TestAppHostAuxiliaryBackchannel { IsInScope = true });

        var resolver = new AppHostConnectionResolver(
            monitor,
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateNonInteractiveHostEnvironment(),
            NullLogger.Instance);

        var result = await resolver.ResolveConnectionAsync(
            projectFile: null,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.IsProjectResolutionError);
        Assert.Equal(SharedCommandStrings.MultipleAppHostsNonInteractive, result.ErrorMessage);
        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            homeDirectory: workingDirectory);
    }

    private static FileInfo CreateProjectFile(DirectoryInfo workingDirectory, string directoryName, string fileName)
    {
        var directory = workingDirectory.CreateSubdirectory(directoryName);
        var projectFile = new FileInfo(Path.Combine(directory.FullName, fileName));
        File.WriteAllText(projectFile.FullName, "<Project />");
        return projectFile;
    }

    private static string CreateMatchingSocketFile(string appHostPath, DirectoryInfo homeDirectory, int pid)
    {
        var backchannelsDir = Path.Combine(homeDirectory.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory.FullName);
        var appHostId = Path.GetFileName(prefix);
        var socketPath = Path.Combine(
            backchannelsDir,
            $"{appHostId}a1b2C3d4.{pid.ToString(CultureInfo.InvariantCulture)}");
        File.WriteAllText(socketPath, "");
        return socketPath;
    }
}
