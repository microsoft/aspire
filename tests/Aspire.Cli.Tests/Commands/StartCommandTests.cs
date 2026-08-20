// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class StartCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task StartCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_Help_ShowsStartDebugSessionOptionInExtensionContext()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DisableAnsi = true;
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(command.Options, option => ReferenceEquals(option, RootCommand.StartDebugSessionOption));
        Assert.False(RootCommand.StartDebugSessionOption.Hidden);
    }

    [Fact]
    public async Task StartCommand_AcceptsNoBuildOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --no-build --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_AcceptsFormatOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --format json --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_AcceptsIsolatedOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start --isolated --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task StartCommand_RejectsInvalidStartupTimeoutEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ConfigurationCallback += config =>
            {
                config[CliConfigNames.AppHostStartupTimeout] = "0";
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, RunCommandStrings.InvalidAppHostStartupTimeoutEnvironmentVariable, CliConfigNames.AppHostStartupTimeout),
            Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public void StartCommand_ForwardsUnmatchedTokensToAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("start -- --custom-arg value");

        Assert.Empty(result.Errors);
        Assert.Contains("--custom-arg", result.UnmatchedTokens);
        Assert.Contains("value", result.UnmatchedTokens);
    }

    [Fact]
    public async Task StartCommand_DetachedChild_PreservesAppHostArgumentsAfterSingleSeparator()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);
        var expectedAppHostArguments = new[] { "true", "false", string.Empty, "--detach", "--option-shaped" };
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };
        var processFactory = new TestProcessExecutionFactory
        {
            DefaultExitCode = CliExitCodes.FailedToDotnetRunAppHost
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        services.Replace(ServiceDescriptor.Singleton<IProcessExecutionFactory>(processFactory));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
        [
            "start",
            "--apphost", appHostFile.FullName,
            "--no-build",
            "--",
            .. expectedAppHostArguments
        ]);

        Assert.Empty(result.Errors);
        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, await result.InvokeAsync().DefaultTimeout());

        AssertDetachedChildArguments(command, processFactory.LastArguments, expectedAppHostArguments);
    }

    [Fact]
    public async Task StartCommand_WhenMultipleProjectFilesFound_NonInteractive_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task StartCommand_WhenMultipleProjectFilesFound_JsonFormat_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task StartCommand_LaunchFailure_DisplaysBothLogPaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();

        // Create a fake .csproj file so the path exists on disk for the process launcher.
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        // Use TestProjectLocator to bypass msbuild evaluation and return the fake project directly.
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
        });

        services.Replace(ServiceDescriptor.Singleton<IProcessExecutionFactory>(new TestDetachedProcessFactory(() => { })));

        // Replace TimeProvider with one that immediately exceeds the backchannel wait
        // timeout if the fake process ever stops exiting immediately.
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new InstantTimeoutTimeProvider()));

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"start --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);

        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var expectedCliLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, executionContext.LogFilePath);

        // The AppHost log path should have been set on the execution context and
        // BaseCommand's shared error handling should display both paths.
        Assert.NotNull(executionContext.AppHostCliLogFilePath);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message == expectedCliLogMessage);

        var expectedAppHostLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeAppHostLogsAt, executionContext.AppHostCliLogFilePath);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message == expectedAppHostLogMessage);
    }

    [Fact]
    public async Task StartCommand_WhenRunningInExtension_ForwardsExplicitArgumentsInSemanticOrder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("App Host");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        File.WriteAllText(appHostFile.FullName, "<Project />");

        string? workingDirectory = null;
        string? projectFile = null;
        bool? debug = null;
        DebugSessionOptions? options = null;

        using var provider = CliTestHelper.CreateExtensionServiceProvider(workspace, outputHelper, (wd, pf, dbg, debugSessionOptions) =>
        {
            workingDirectory = wd;
            projectFile = pf;
            debug = dbg;
            options = debugSessionOptions;
        });

        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(
        [
            "start",
            "--project", appHostFile.FullName,
            "--debug",
            "--capture-profile",
            "--format=table",
            "--no-build",
            "--isolated=false",
            "--wait-for-debugger",
            "--non-interactive=false",
            "--log-level", "Debug",
            "--start-debug-session",
            "--capture-profile-delay=1",
            "--detach",
            "--unknown-option", "value"
        ]);

        Assert.Empty(result.Errors);
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(workspace.WorkspaceRoot.FullName, workingDirectory);
        Assert.Equal(appHostFile.FullName, projectFile);
        Assert.True(debug);
        Assert.NotNull(options);
        Assert.Equal("run", options.Command);
        Assert.NotNull(options.Args);
        Assert.Equal(
            [
                "--debug",
                "--capture-profile",
                "--no-build",
                "--isolated", "false",
                "--wait-for-debugger",
                "--log-level", "Debug",
                "--capture-profile-delay", "1",
                "--",
                "--detach",
                "--unknown-option", "value"
            ],
            options.Args);
        Assert.Equal("explicit-cli", options.AppHostSelectionOrigin);
    }

    [Fact]
    public async Task StartCommand_WhenRunningInExtensionWithoutAppHost_UsesDefaultDiscoverySelectionOrigin()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        string? projectFile = null;
        bool? debug = null;
        DebugSessionOptions? options = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, testOptions =>
        {
            testOptions.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            testOptions.InteractionServiceFactory = sp =>
            {
                var service = new TestExtensionInteractionService(sp);
                service.StartDebugSessionCallback = (_, pf, dbg, debugSessionOptions) =>
                {
                    projectFile = pf;
                    debug = dbg;
                    options = debugSessionOptions;
                };
                return service;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("start");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Null(projectFile);
        Assert.False(debug);
        Assert.NotNull(options);
        Assert.Equal("run", options.Command);
        Assert.Empty(options.Args!);
        Assert.Equal("default-discovery", options.AppHostSelectionOrigin);
    }

    [Fact]
    public async Task StartCommand_WhenRunningInExtensionWithStartDebugSession_StartsVsCodeDebugSession()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);

        bool? debug = null;
        DebugSessionOptions? options = null;

        using var provider = CliTestHelper.CreateExtensionServiceProvider(workspace, outputHelper, (_, _, dbg, debugSessionOptions) =>
        {
            debug = dbg;
            options = debugSessionOptions;
        });

        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse($"start --apphost {appHostFile.FullName} --start-debug-session");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(debug);
        Assert.NotNull(options);
        Assert.Equal("run", options.Command);
        Assert.NotNull(options.Args);
        Assert.Empty(options.Args);
    }

    [Fact]
    public async Task StartCommand_WhenRunningInExtensionInLinkedWorktree_DoesNotInferIsolation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        TestGitWorktree.WriteLinkedWorktreeMetadata(
            workspace.WorkspaceRoot.FullName,
            Path.Combine(workspace.WorkspaceRoot.FullName, "common", ".git"));
        var appHostFile = CreateAppHostFile(workspace);

        DebugSessionOptions? options = null;
        using var provider = CliTestHelper.CreateExtensionServiceProvider(
            workspace,
            outputHelper,
            (_, _, _, debugSessionOptions) => options = debugSessionOptions);
        var result = provider.GetRequiredService<RootCommand>().Parse($"start --apphost {appHostFile.FullName}");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.NotNull(options);
        Assert.NotNull(options.Args);
        Assert.Empty(options.Args);
    }

    [Fact]
    public async Task StartCommand_WhenRunningInExtensionWithDebugSession_DoesNotStartVsCodeRunSession()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);
        var startDebugSessionCalled = false;
        var detachedLauncherCalled = false;

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        using var provider = CliTestHelper.CreateExtensionServiceProvider(
            workspace,
            outputHelper,
            (_, _, _, _) => startDebugSessionCalled = true,
            configureOptions: testOptions =>
            {
                testOptions.ConfigurationCallback += config => config[KnownConfigNames.ExtensionDebugSessionId] = "existing-session";
                testOptions.ProjectLocatorFactory = _ => projectLocator;
            },
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IProcessExecutionFactory>(new TestDetachedProcessFactory(() => detachedLauncherCalled = true)));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new InstantTimeoutTimeProvider()));
            });
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse($"start --apphost {appHostFile.FullName}");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.False(startDebugSessionCalled);
        Assert.True(detachedLauncherCalled);
    }

    [Theory]
    [InlineData("start --non-interactive --apphost {0}")]
    [InlineData("start --format json --apphost {0}")]
    public async Task StartCommand_WhenRunningInExtensionWithDetachedOnlyOption_DoesNotStartVsCodeRunSession(string commandTemplate)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);
        var startDebugSessionCalled = false;
        var detachedLauncherCalled = false;

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        using var provider = CliTestHelper.CreateExtensionServiceProvider(
            workspace,
            outputHelper,
            (_, _, _, _) => startDebugSessionCalled = true,
            configureOptions: testOptions => testOptions.ProjectLocatorFactory = _ => projectLocator,
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IProcessExecutionFactory>(new TestDetachedProcessFactory(() => detachedLauncherCalled = true)));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new InstantTimeoutTimeProvider()));
            });
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(string.Format(CultureInfo.InvariantCulture, commandTemplate, appHostFile.FullName));
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.False(startDebugSessionCalled);
        Assert.True(detachedLauncherCalled);
    }

    [Fact]
    public void ResolveIsolated_LinkedWorktree_RequiresExplicitIsolation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        TestGitWorktree.WriteLinkedWorktreeMetadata(
            workspace.WorkspaceRoot.FullName,
            Path.Combine(workspace.WorkspaceRoot.FullName, "common", ".git"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("start")));
        Assert.True(AppHostLauncher.ResolveIsolated(command.Parse("start --isolated")));
        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("start --isolated false")));
        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("run")));
        Assert.True(AppHostLauncher.ResolveIsolated(command.Parse("run --isolated")));
        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("run --isolated false")));
    }

    [Theory]
    [InlineData("start", null)]
    [InlineData("start --isolated", true)]
    [InlineData("start --isolated false", false)]
    public void GetExplicitIsolated_PreservesOmittedAndExplicitValues(string commandLine, bool? expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        Assert.Equal(expected, AppHostLauncher.GetExplicitIsolated(command.Parse(commandLine)));
    }

    [Fact]
    public void ResolveIsolated_PrimaryCheckout_DoesNotInferIsolated()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".git"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("start")));
        Assert.True(AppHostLauncher.ResolveIsolated(command.Parse("start --isolated")));
        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("start --isolated false")));
        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("run")));
        Assert.True(AppHostLauncher.ResolveIsolated(command.Parse("run --isolated")));
        Assert.False(AppHostLauncher.ResolveIsolated(command.Parse("run --isolated false")));
    }

    private static FileInfo CreateAppHostFile(TemporaryWorkspace workspace)
    {
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        File.WriteAllText(appHostFile.FullName, "<Project />");

        return appHostFile;
    }

    private static void AssertDetachedChildArguments(RootCommand command, string[]? childArguments, string[] expectedAppHostArguments)
    {
        var forwardedArguments = ExtractForwardedRunArguments(Assert.IsType<string[]>(childArguments));
        var separatorIndex = Array.IndexOf(forwardedArguments, "--");
        var noBuildIndex = Array.IndexOf(forwardedArguments, "--no-build");

        Assert.Equal(1, forwardedArguments.Count(argument => argument == "--"));
        Assert.True(separatorIndex > 0, "Expected a single child/AppHost separator.");
        Assert.True(noBuildIndex > 0, "Expected detached child arguments to include --no-build.");
        Assert.Equal(1, forwardedArguments.Count(argument => argument == "--no-build"));
        Assert.Equal(["--no-build", "--", .. expectedAppHostArguments], forwardedArguments[noBuildIndex..]);
        Assert.DoesNotContain("--detach", forwardedArguments.Take(separatorIndex));
        var childParseResult = command.Parse(["run", .. forwardedArguments[noBuildIndex..]]);

        Assert.Empty(childParseResult.Errors);
        Assert.Equal(expectedAppHostArguments, childParseResult.UnmatchedTokens);
    }

    private static string[] ExtractForwardedRunArguments(string[] childArguments)
    {
        var runIndex = Array.IndexOf(childArguments, "run");
        Assert.True(runIndex >= 0, "Expected detached child arguments to include the run command.");

        return childArguments[runIndex..];
    }

    private sealed class TestDetachedProcessFactory(Action onStart) : IProcessExecutionFactory
    {
        public IProcessExecution CreateExecution(string fileName, string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
        {
            _ = fileName;
            _ = args;
            _ = env;
            _ = workingDirectory;

            Assert.True(options.Detached);
            return new TestDetachedProcessExecution(onStart);
        }

        public IProcessExecution CreateExecution(ProcessStartInfo startInfo, ProcessInvocationOptions options)
        {
            _ = startInfo;

            Assert.True(options.Detached);
            return new TestDetachedProcessExecution(onStart);
        }

        private sealed class TestDetachedProcessExecution(Action onStart) : IProcessExecution
        {
            public string FileName => "test";

            public IReadOnlyList<string> Arguments => [];

            public IReadOnlyDictionary<string, string?> EnvironmentVariables => new Dictionary<string, string?>();

            public int ProcessId => int.MaxValue - 1;

            public DateTimeOffset? StartTime => DateTimeOffset.MinValue;

            public bool HasExited => true;

            public int ExitCode => CliExitCodes.FailedToDotnetRunAppHost;

            public Task<bool> StartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onStart();
                return Task.FromResult(true);
            }

            public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(ExitCode);
            }

            public void Kill(bool entireProcessTree)
            {
                _ = entireProcessTree;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A TimeProvider that causes the backchannel wait loop to time out immediately.
    /// The first call (used for <c>startTime</c>) returns the base time; subsequent
    /// calls return a time 200 seconds later, exceeding the 120-second timeout.
    /// </summary>
    private sealed class InstantTimeoutTimeProvider : TimeProvider
    {
        private int _callCount;
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return Interlocked.Increment(ref _callCount) <= 1
                ? _start
                : _start.AddSeconds(200);
        }
    }
}
