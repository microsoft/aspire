// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Processes;
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
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

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task StartCommand_LaunchFailure_DisplaysBothLogPaths()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        // Replace TimeProvider with one that immediately exceeds the backchannel wait
        // timeout so the test doesn't wait for a real process to exit.
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
    public async Task StartCommand_WhenRunningInExtensionWithoutDebugSession_StartsVsCodeRunSession()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);

        string? workingDirectory = null;
        string? projectFile = null;
        bool? debug = null;
        DebugSessionOptions? options = null;

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, testOptions =>
        {
            testOptions.ProjectLocatorFactory = _ => projectLocator;
            testOptions.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            testOptions.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: false);
            };
            testOptions.InteractionServiceFactory = sp =>
            {
                var service = new TestExtensionInteractionService(sp);
                service.StartDebugSessionCallback = (wd, pf, dbg, debugSessionOptions) =>
                {
                    workingDirectory = wd;
                    projectFile = pf;
                    debug = dbg;
                    options = debugSessionOptions;
                };
                return service;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var captureProfileOutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "profile.zip");
        var result = command.Parse($"start --apphost {appHostFile.FullName} --isolated --no-build --debug --log-level Debug --wait-for-debugger --capture-profile --capture-profile-output {captureProfileOutputPath} --capture-profile-delay 1 -- --custom-arg value");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(workspace.WorkspaceRoot.FullName, workingDirectory);
        Assert.Equal(appHostFile.FullName, projectFile);
        Assert.False(debug);
        Assert.NotNull(options);
        Assert.Equal("run", options.Command);
        Assert.NotNull(options.Args);
        Assert.Equal(["--isolated", "--no-build", "--debug", "--log-level", "Debug", "--wait-for-debugger", "--capture-profile", "--capture-profile-output", captureProfileOutputPath, "--capture-profile-delay", "1", "--", "--custom-arg", "value"], options.Args);
    }

    [Fact]
    public async Task StartCommand_WhenRunningInExtensionWithStartDebugSession_StartsVsCodeDebugSession()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);

        bool? debug = null;
        DebugSessionOptions? options = null;

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, testOptions =>
        {
            testOptions.ProjectLocatorFactory = _ => projectLocator;
            testOptions.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            testOptions.InteractionServiceFactory = sp =>
            {
                var service = new TestExtensionInteractionService(sp);
                service.StartDebugSessionCallback = (_, _, dbg, debugSessionOptions) =>
                {
                    debug = dbg;
                    options = debugSessionOptions;
                };
                return service;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse($"start --apphost {appHostFile.FullName} --start-debug-session");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(debug);
        Assert.NotNull(options);
        Assert.Equal("run", options.Command);
        Assert.Null(options.Args);
    }

    [Fact]
    public async Task StartCommand_WhenRunningInExtensionWithDebugSession_DoesNotStartVsCodeRunSession()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);
        var startDebugSessionCalled = false;
        var detachedLauncherCalled = false;

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, testOptions =>
        {
            testOptions.ConfigurationCallback += config => config[KnownConfigNames.ExtensionDebugSessionId] = "existing-session";
            testOptions.ProjectLocatorFactory = _ => projectLocator;
            testOptions.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            testOptions.InteractionServiceFactory = sp =>
            {
                var service = new TestExtensionInteractionService(sp);
                service.StartDebugSessionCallback = (_, _, _, _) => startDebugSessionCalled = true;
                return service;
            };
        });

        services.Replace(ServiceDescriptor.Singleton<IDetachedProcessLauncher>(new TestDetachedProcessLauncher(() => detachedLauncherCalled = true)));
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new InstantTimeoutTimeProvider()));

        using var provider = services.BuildServiceProvider();
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateAppHostFile(workspace);
        var startDebugSessionCalled = false;
        var detachedLauncherCalled = false;

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, testOptions =>
        {
            testOptions.ProjectLocatorFactory = _ => projectLocator;
            testOptions.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            testOptions.InteractionServiceFactory = sp =>
            {
                var service = new TestExtensionInteractionService(sp);
                service.StartDebugSessionCallback = (_, _, _, _) => startDebugSessionCalled = true;
                return service;
            };
        });

        services.Replace(ServiceDescriptor.Singleton<IDetachedProcessLauncher>(new TestDetachedProcessLauncher(() => detachedLauncherCalled = true)));
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new InstantTimeoutTimeProvider()));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(string.Format(CultureInfo.InvariantCulture, commandTemplate, appHostFile.FullName));
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.False(startDebugSessionCalled);
        Assert.True(detachedLauncherCalled);
    }

    private static FileInfo CreateAppHostFile(TemporaryWorkspace workspace)
    {
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        File.WriteAllText(appHostFile.FullName, "<Project />");

        return appHostFile;
    }

    private sealed class TestDetachedProcessLauncher(Action onStart) : IDetachedProcessLauncher
    {
        public Process Start(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Func<string, bool>? shouldRemoveEnvironmentVariable = null,
            IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null)
        {
            onStart();
            return new Process { StartInfo = new ProcessStartInfo(fileName) };
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
