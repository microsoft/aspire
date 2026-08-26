// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Backchannel;

public class AppHostConnectionResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ResolveConnectionAsync_WithExplicitProjectFile_PreservesFastPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectFile = CreateProjectFile(workspace.WorkspaceRoot, "TestAppHost", "TestAppHost.csproj");
        // Key the socket off the symlink-resolved path, matching how a running AppHost computes
        // its socket id (its working directory is reported physically by the OS). On macOS the
        // temp workspace lives under /var -> /private/var, so the unresolved and resolved paths
        // differ and the resolver must resolve symlinks to find this socket.
        var resolvedProjectPath = PathNormalizer.ResolveSymlinks(projectFile.FullName);
        var socketPath = CreateMatchingSocketFile(resolvedProjectPath, workspace.WorkspaceRoot, int.MaxValue - 1);
        var resolver = new AppHostConnectionResolver(
            new TestAuxiliaryBackchannelMonitor(),
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

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
    public async Task ResolveConnectionAsync_WithExplicitDirectoryAndOnlyUnbuildableAppHosts_ReturnsProjectResolutionError()
    {
        // Connection commands (stop, logs, describe, ps) decide between "the AppHost is not running" and
        // "we could not resolve a project" purely from the exit code. A resolution failure that is not
        // classified as one produces success-shaped output for a command that resolved nothing.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("Apps");
        var interactionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                throw new ProjectLocatorException(ErrorStrings.AppHostsMayNotBeBuildable, ProjectLocatorFailureReason.AppHostsMayNotBeBuildable)
        };
        var resolver = new AppHostConnectionResolver(
            new TestAuxiliaryBackchannelMonitor(),
            interactionService,
            projectLocator,
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var result = await resolver.ResolveConnectionAsync(
            new FileInfo(appHostDirectory.FullName),
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.IsProjectResolutionError);
        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
        Assert.Equal(InteractionServiceStrings.UnbuildableAppHostsDetected, result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveConnectionAsync_WithExplicitDirectoryAndNoAppHosts_ReturnsDirectorySpecificError()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket-other", new TestAppHostAuxiliaryBackchannel { IsInScope = false });

        var resolver = new AppHostConnectionResolver(
            monitor,
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateNonInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

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

    [Fact]
    public async Task ResolveConnectionAsync_WithSymlinkedProjectPath_ResolvesToCanonicalSocketKey()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17618.
        // A running AppHost keys its backchannel socket off the symlink-resolved path
        // (its process working directory is already physical, e.g. /tmp -> /private/tmp
        // on macOS). The explicit --apphost lookup must resolve symlinks the same way or
        // it computes a different appHostId and reports "no running AppHost" even though
        // one is running. We assert the orphaned socket keyed off the canonical path is
        // found (and pruned) when the resolver is handed a symlinked project path.
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);

        // Real project file under a "real" directory.
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var realProjectFile = new FileInfo(Path.Combine(realDirectory.FullName, "TestAppHost.csproj"));
        File.WriteAllText(realProjectFile.FullName, "<Project />");

        // Symlink "link" -> "real"; the project is addressed through the symlink.
        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);
        var projectFileViaSymlink = new FileInfo(Path.Combine(symlinkDirectory, "TestAppHost.csproj"));

        // The producer keys its socket off the canonical (symlink-resolved) path, so create
        // the orphaned socket using that same canonical path with a dead PID.
        var canonicalPath = PathNormalizer.ResolveSymlinks(projectFileViaSymlink.FullName);
        var socketPath = CreateMatchingSocketFile(canonicalPath, workspace.WorkspaceRoot, int.MaxValue - 1);

        var resolver = new AppHostConnectionResolver(
            new TestAuxiliaryBackchannelMonitor(),
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var result = await resolver.ResolveConnectionAsync(
            projectFileViaSymlink,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        // The socket was located via the symlink-resolved key and pruned because its PID is dead.
        // Before the fix the resolver hashed the unresolved symlink path, never matched this
        // socket, and left it on disk.
        Assert.False(File.Exists(socketPath));
    }

    [Fact]
    public async Task ResolveConnectionAsync_RestrictedToWorktreeWithAppHostOutsideWorkingDirectory_PromptsForSelection()
    {
        // `aspire stop` restricts selection to the current worktree, but an AppHost that merely
        // lives outside the working directory (running `aspire stop` from repo/tests while the
        // AppHost is at repo/AppHost) is still in that worktree and must stay selectable.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".git"));
        var workingDirectory = workspace.WorkspaceRoot.CreateSubdirectory("tests");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            homeDirectory: workspace.WorkspaceRoot);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost", "AppHost.csproj");
        var connection = CreateOutOfScopeConnection(appHostPath);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket-sibling", connection);
        var interactionService = new TestInteractionService();

        var resolver = new AppHostConnectionResolver(
            monitor,
            interactionService,
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var result = await resolver.ResolveConnectionAsync(
            projectFile: null,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken,
            restrictToCurrentWorktree: true);

        Assert.True(result.Success);
        Assert.Same(connection, result.Connection);
        Assert.Equal(SharedCommandStrings.NoInScopeAppHostsShowingAll, Assert.Single(interactionService.DisplayedMessages).Message);
    }

    [Fact]
    public async Task ResolveConnectionAsync_RestrictedToWorktreeWithAppHostInDifferentWorktree_ReturnsWorktreeSpecificError()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = CreateLinkedWorktreeAppHostPath(workspace.WorkspaceRoot);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket-worktree", CreateOutOfScopeConnection(appHostPath));
        var interactionService = new TestInteractionService
        {
            PromptForSelectionCallback = (_, _, _, _) =>
                throw new InvalidOperationException("AppHosts in another worktree must not be offered when selection is restricted.")
        };

        var resolver = new AppHostConnectionResolver(
            monitor,
            interactionService,
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var result = await resolver.ResolveConnectionAsync(
            projectFile: null,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken,
            restrictToCurrentWorktree: true);

        Assert.False(result.Success);
        // "Use 'aspire run' to start one first" would be wrong: an AppHost is running, just not here.
        Assert.Equal(SharedCommandStrings.AppHostNotRunningInCurrentWorktree, result.ErrorMessage);
        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
    }

    [Fact]
    public async Task ResolveConnectionAsync_UnrestrictedWithAppHostInDifferentWorktree_PromptsForSelection()
    {
        // Only `aspire stop` opts into the worktree restriction; every other connection command
        // keeps offering whatever is running.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = CreateLinkedWorktreeAppHostPath(workspace.WorkspaceRoot);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);

        var connection = CreateOutOfScopeConnection(appHostPath);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket-worktree", connection);

        var resolver = new AppHostConnectionResolver(
            monitor,
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var result = await resolver.ResolveConnectionAsync(
            projectFile: null,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Same(connection, result.Connection);
    }

    [Fact]
    public async Task ResolveConnectionAsync_NonInteractiveRestrictedToWorktree_ReturnsNotFoundError()
    {
        // A host that cannot prompt gets the same not-found answer it always did, even when the
        // AppHost would have been selectable interactively.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".git"));
        var workingDirectory = workspace.WorkspaceRoot.CreateSubdirectory("tests");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            homeDirectory: workspace.WorkspaceRoot);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost", "AppHost.csproj");
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket-sibling", CreateOutOfScopeConnection(appHostPath));

        var resolver = new AppHostConnectionResolver(
            monitor,
            new TestInteractionService(),
            new TestProjectLocator(),
            executionContext,
            TestHelpers.CreateNonInteractiveHostEnvironment(),
            NullLogger<AppHostConnectionResolver>.Instance,
            new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var result = await resolver.ResolveConnectionAsync(
            projectFile: null,
            "Scanning",
            "Select",
            SharedCommandStrings.AppHostNotRunning,
            TestContext.Current.CancellationToken,
            restrictToCurrentWorktree: true);

        Assert.False(result.Success);
        Assert.Equal(SharedCommandStrings.AppHostNotRunning, result.ErrorMessage);
        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            homeDirectory: workingDirectory);
    }

    private static TestAppHostAuxiliaryBackchannel CreateOutOfScopeConnection(string appHostPath)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = Environment.ProcessId
            }
        };
    }

    /// <summary>
    /// Creates a linked worktree nested under <paramref name="primaryCheckout"/> and returns an
    /// AppHost path inside it. The primary checkout gets a real <c>.git</c> directory so the
    /// ancestor walk stops there instead of escaping into whatever repo owns the temp directory.
    /// </summary>
    private static string CreateLinkedWorktreeAppHostPath(DirectoryInfo primaryCheckout)
    {
        var commonGitDirectory = Path.Combine(primaryCheckout.FullName, ".git");
        Directory.CreateDirectory(commonGitDirectory);
        var worktreeRoot = Directory.CreateDirectory(Path.Combine(primaryCheckout.FullName, ".worktrees", "feature")).FullName;
        TestGitWorktree.WriteLinkedWorktreeMetadata(worktreeRoot, commonGitDirectory);
        return Path.Combine(worktreeRoot, "AppHost", "AppHost.csproj");
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
