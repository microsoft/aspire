// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Shared.UserSecrets;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Commands;

public class RunCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RunCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenNoProjectFileFound_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new NoProjectFileProjectLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenMultipleProjectFilesFound_NonInteractive_ReturnsFailedToFindProject()
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
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenMultipleProjectFilesFound_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new MultipleProjectFilesProjectLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenProjectFileDoesNotExist_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new ProjectFileDoesNotExistLocator();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --apphost /tmp/doesnotexist.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task RunCommand_WithDetachFlag_DoesNotShowUpdateNotification()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testNotifier = new TestCliUpdateNotifier();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new NoProjectFileProjectLocator();
            options.CliUpdateNotifierFactory = _ => testNotifier;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --detach");

        await result.InvokeAsync().DefaultTimeout();

        Assert.False(testNotifier.NotifyWasCalled, "Update notification should not be shown when --detach is used");
    }

    [Fact]
    public async Task RunCommand_WithoutDetachFlag_ShowsUpdateNotification()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testNotifier = new TestCliUpdateNotifier();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new NoProjectFileProjectLocator();
            options.CliUpdateNotifierFactory = _ => testNotifier;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        await result.InvokeAsync().DefaultTimeout();

        Assert.True(testNotifier.NotifyWasCalled, "Update notification should be shown when --detach is not used");
    }

    [Fact]
    public void GetDetachedFailureMessage_ReturnsBuildSpecificMessage_ForBuildFailureExitCode()
    {
        var message = AppHostLauncher.GetDetachedFailureMessage(ExitCodeConstants.FailedToBuildArtifacts);

        Assert.Equal(RunCommandStrings.AppHostFailedToBuild, message);
    }

    [Fact]
    public void GetDetachedFailureMessage_ReturnsExitCodeMessage_ForUnknownExitCode()
    {
        var message = AppHostLauncher.GetDetachedFailureMessage(123);

        Assert.Contains("123", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateChildLogFilePath_UsesDetachChildNamingWithoutProcessId()
    {
        var logsDirectory = Path.Combine(Path.GetTempPath(), "aspire-cli-tests");
        var now = new DateTimeOffset(2026, 02, 12, 18, 00, 00, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var path = AppHostLauncher.GenerateChildLogFilePath(logsDirectory, timeProvider);
        var fileName = Path.GetFileName(path);

        Assert.StartsWith(logsDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("cli_20260212T180000000_detach-child_", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".log", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain($"_{Environment.ProcessId}", fileName, StringComparison.Ordinal);
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

    [Fact]
    public async Task RunCommand_WhenCertificateServiceThrows_ReturnsNonZeroExitCode()
    {
        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();

            // Fake apphost information to return a compatable app host.
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateServiceFactory = _ => new ThrowingCertificateService();
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ProjectLocatorFactory = projectLocatorFactory;
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToTrustCertificates, exitCode);
    }

    private sealed class ThrowingCertificateService : Aspire.Cli.Certificates.ICertificateService
    {
        public Task<Aspire.Cli.Certificates.EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Certificates.CertificateServiceException("Failed to trust certificates");
        }
    }

    private sealed class NoProjectFileProjectLocator : IProjectLocator
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

    private sealed class MultipleProjectFilesProjectLocator : IProjectLocator
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private async IAsyncEnumerable<BackchannelLogEntry> ReturnLogEntriesUntilCancelledAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var logEntryIndex = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);
            // Simulate log entries being returned
            yield return new BackchannelLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                LogLevel = LogLevel.Information,
                Message = $"Test log entry {logEntryIndex++}",
                EventId = new EventId(),
                CategoryName = "TestCategory"
            };
        }
    }

    [Fact]
    public async Task RunCommand_CompletesSuccessfully()
    {
        var getResourceStatesAsyncCalled = new TaskCompletionSource();

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();

            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;

            return backchannel;

        };

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            // Fake the build command to always succeed.
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;

            // Fake apphost information to return a compatable app host.
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            // public Task<int> RunAsync(FileInfo projectFile, bool watch, bool noBuild, string[] args, IDictionary<string, string>? env, TaskCompletionSource<AppHostCliBackchannel>? backchannelCompletionSource, CancellationToken cancellationToken)
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                // Make a backchannel and return it, but don't return from the run call until the backchannel
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                // Just simulate the process running until the user cancels.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);

                return 0;
            };

            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);

        // Simulate CTRL-C.
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task RunCommand_WithNoResources_CompletesSuccessfully()
    {
        var getResourceStatesAsyncCalled = new TaskCompletionSource();
        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();

            // Return empty resources using an empty enumerable
            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;

            return backchannel;
        };

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            };

            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);

        // Simulate CTRL-C.
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout(TestConstants.LongTimeoutDuration);
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenDashboardFailsToStart_ReturnsNonZeroExitCodeWithClearErrorMessage()
    {

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            // Configure the backchannel to throw DashboardStartupException when GetDashboardUrlsAsync is called
            backchannel.GetDashboardUrlsAsyncCallback = (ct) =>
            {
                return Task.FromResult(new DashboardUrlsState
                {
                    DashboardHealthy = false,
                    BaseUrlWithLoginToken = null,
                    CodespacesUrlWithLoginToken = null
                });
            };
            return backchannel;
        };

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            // Fake the build command to always succeed.
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;

            // Fake apphost information to return a compatible app host.
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            // Configure the runner to establish a backchannel but simulate dashboard failure
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                // Set up the backchannel
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                // Just simulate the process running until the user cancels.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);

                return 0;
            };

            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.InteractionServiceFactory = (sp) => new TestInteractionService();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert that the command returns the expected failure exit code
        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
    }

    [Fact]
    public async Task AppHostHelper_BuildAppHostAsync_IncludesRelativePathInStatusMessage()
    {
        var testInteractionService = new TestInteractionService();
        testInteractionService.ShowStatusCallback = (statusText) =>
        {
            Assert.Contains(
                $"{InteractionServiceStrings.BuildingAppHost} src{Path.DirectorySeparatorChar}MyApp.AppHost{Path.DirectorySeparatorChar}MyApp.AppHost.csproj",
                statusText
            );
        };

        var testRunner = new TestDotNetCliRunner();
        testRunner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectoryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "src", "MyApp.AppHost");
        var appHostDirectory = Directory.CreateDirectory(appHostDirectoryPath);
        var appHostProjectPath = Path.Combine(appHostDirectory.FullName, "MyApp.AppHost.csproj");
        var appHostProjectFile = new FileInfo(appHostProjectPath);
        File.WriteAllText(appHostProjectFile.FullName, "<Project></Project>");

        var options = new ProcessInvocationOptions();
        await AppHostHelper.BuildAppHostAsync(testRunner, testInteractionService, appHostProjectFile, noRestore: false, options, workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task RunCommand_SkipsBuild_WhenBuildDotNetUsingCliCapabilityIsAvailable()
    {
        var buildCalled = false;

        var extensionBackchannel = new TestExtensionBackchannel();
        extensionBackchannel.GetCapabilitiesAsyncCallback = ct => Task.FromResult(new[] { "devkit" });

        var appHostBackchannel = new TestAppHostBackchannel();
        appHostBackchannel.GetDashboardUrlsAsyncCallback = (ct) => Task.FromResult(new DashboardUrlsState
        {
            DashboardHealthy = true,
            BaseUrlWithLoginToken = "http://localhost/dashboard",
            CodespacesUrlWithLoginToken = null
        });
        appHostBackchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;

        var backchannelFactory = (IServiceProvider sp) => appHostBackchannel;

        var extensionInteractionServiceFactory = (IServiceProvider sp) => new TestExtensionInteractionService(sp);

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) =>
            {
                buildCalled = true;
                return 0;
            };
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            };
            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ExtensionBackchannelFactory = _ => extensionBackchannel;
            options.InteractionServiceFactory = extensionInteractionServiceFactory;
            options.ConfigurationCallback += config =>
            {
                // Set debug session ID so the run command doesn't return early
                config["ASPIRE_EXTENSION_DEBUG_SESSION_ID"] = "test-session-id";
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);
        cts.Cancel();
        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(buildCalled, "Build should be skipped when extension DevKit capability is available.");
    }

    [Fact]
    public async Task RunCommand_SkipsBuild_WhenRunningInExtension_AndNoBuildInCliCapability()
    {
        var buildCalled = false;

        var extensionBackchannel = new TestExtensionBackchannel();
        extensionBackchannel.GetCapabilitiesAsyncCallback = ct => Task.FromResult(Array.Empty<string>());

        var appHostBackchannel = new TestAppHostBackchannel();
        appHostBackchannel.GetDashboardUrlsAsyncCallback = (ct) => Task.FromResult(new DashboardUrlsState
        {
            DashboardHealthy = true,
            BaseUrlWithLoginToken = "http://localhost/dashboard",
            CodespacesUrlWithLoginToken = null
        });
        appHostBackchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;

        var backchannelFactory = (IServiceProvider sp) => appHostBackchannel;

        var extensionInteractionServiceFactory = (IServiceProvider sp) => new TestExtensionInteractionService(sp);

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) =>
            {
                buildCalled = true;
                return 0;
            };
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            };
            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ExtensionBackchannelFactory = _ => extensionBackchannel;
            options.InteractionServiceFactory = extensionInteractionServiceFactory;
            options.ConfigurationCallback += config =>
            {
                // Set debug session ID so the run command doesn't return early
                config["ASPIRE_EXTENSION_DEBUG_SESSION_ID"] = "test-session-id";
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);
        cts.Cancel();
        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(buildCalled, "Build should be skipped when running in extension.");
    }

    [Fact]
    public async Task RunCommand_Builds_WhenExtensionHasBuildDotnetUsingCliCapability()
    {
        var buildCalled = false;
        var buildCalledTcs = new TaskCompletionSource();

        var extensionBackchannel = new TestExtensionBackchannel();
        extensionBackchannel.GetCapabilitiesAsyncCallback = ct => Task.FromResult(new[] { "build-dotnet-using-cli" });

        var appHostBackchannel = new TestAppHostBackchannel();
        appHostBackchannel.GetDashboardUrlsAsyncCallback = (ct) => Task.FromResult(new DashboardUrlsState
        {
            DashboardHealthy = true,
            BaseUrlWithLoginToken = "http://localhost/dashboard",
            CodespacesUrlWithLoginToken = null
        });
        appHostBackchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;

        var backchannelFactory = (IServiceProvider sp) => appHostBackchannel;

        var extensionInteractionServiceFactory = (IServiceProvider sp) => new TestExtensionInteractionService(sp);

        var runnerFactory = (IServiceProvider sp) => {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => {
                buildCalled = true;
                buildCalledTcs.TrySetResult();
                return 0;
            };
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) => {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            };
            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ExtensionBackchannelFactory = _ => extensionBackchannel;
            options.InteractionServiceFactory = extensionInteractionServiceFactory;
            options.ConfigurationCallback += config =>
            {
                // Set debug session ID so the run command doesn't return early
                config["ASPIRE_EXTENSION_DEBUG_SESSION_ID"] = "test-session-id";
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        // Pass --start-debug-session to avoid watch mode (which skips build)
        var result = command.Parse("run --start-debug-session");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);

        // Wait for the build to be called before cancelling
        await buildCalledTcs.Task.DefaultTimeout();
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(buildCalled, "Build should be called when extension has build-dotnet-using-cli capability.");
    }

    [Fact]
    public async Task RunCommand_WhenSingleFileAppHostAndDefaultWatchEnabled_DoesNotUseWatchMode()
    {
        var watchModeUsed = false;

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            // Fake the build command to always succeed.
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;

            // Fake apphost information to return a compatible app host.
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                watchModeUsed = watch;
                // Make a backchannel and return it
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                // Don't run indefinitely for the test
                await Task.Delay(100, ct);
                return 0;
            };

            return runner;
        };

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;
            return backchannel;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new SingleFileAppHostProjectLocator();
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.EnabledFeatures = [KnownFeatures.DefaultWatchEnabled];
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

        Assert.False(watchModeUsed, "Expected watch mode to be disabled for single file apps even when DefaultWatchEnabled feature flag is true");
    }

    [Fact]
    public async Task RunCommand_WhenDefaultWatchEnabledFeatureFlagIsTrue_UsesWatchMode()
    {
        var watchModeUsed = false;

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            // Fake the build command to always succeed.
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;

            // Fake apphost information to return a compatible app host.
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                watchModeUsed = watch;
                // Make a backchannel and return it
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                // Don't run indefinitely for the test
                await Task.Delay(100, ct);
                return 0;
            };

            return runner;
        };

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;
            return backchannel;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.EnabledFeatures = [KnownFeatures.DefaultWatchEnabled];
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

        Assert.True(watchModeUsed, "Expected watch mode to be enabled when defaultWatchEnabled feature flag is true");
    }

    [Fact]
    public async Task RunCommand_WhenDefaultWatchEnabledFeatureFlagIsFalse_DoesNotUseWatchMode()
    {
        var watchModeUsed = false;

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            // Fake the build command to always succeed.
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;

            // Fake apphost information to return a compatible app host.
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                watchModeUsed = watch;
                // Make a backchannel and return it
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                // Don't run indefinitely for the test
                await Task.Delay(100, ct);
                return 0;
            };

            return runner;
        };

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;
            return backchannel;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.DisabledFeatures = [KnownFeatures.DefaultWatchEnabled];
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

        Assert.False(watchModeUsed, "Expected watch mode to be disabled when defaultWatchEnabled feature flag is false");
    }

    [Fact]
    public async Task RunCommand_WhenDefaultWatchEnabledFeatureFlagNotSet_DefaultsToFalse()
    {
        var watchModeUsed = false;

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            // Fake the build command to always succeed.
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;

            // Fake apphost information to return a compatible app host.
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                watchModeUsed = watch;
                // Make a backchannel and return it
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                // Don't run indefinitely for the test
                await Task.Delay(100, ct);
                return 0;
            };

            return runner;
        };

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;
            return backchannel;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            // Don't explicitly set the feature flag
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

        Assert.False(watchModeUsed, "Expected watch mode to be disabled by default when defaultWatchEnabled feature flag is not set");
    }

    [Fact]
    public async Task DotNetCliRunner_RunAsync_WhenWatchIsTrue_IncludesNonInteractiveFlag()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DotNetCliRunner>>();

        var options = new ProcessInvocationOptions();

        var executionContext = new CliExecutionContext(
            workingDirectory: workspace.WorkspaceRoot, hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("hives"), cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("cache"), sdksDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), logFilePath: "test.log"
        );

        var runner = DotNetCliRunnerTestHelper.Create(
            provider,
            executionContext,
            (args, env, workingDirectory, options) =>
            {
                // Verify that --non-interactive is included when watch mode is enabled
                Assert.Contains("watch", args);
                Assert.Contains("--non-interactive", args);

                // Verify the order: watch should come before --non-interactive
                var watchIndex = Array.IndexOf(args, "watch");
                var nonInteractiveIndex = Array.IndexOf(args, "--non-interactive");
                Assert.True(watchIndex < nonInteractiveIndex);
            },
            0,
            logger: logger
        );

        var exitCode = await runner.RunAsync(
            projectFile: projectFile,
            watch: true, // This should add --non-interactive
            noBuild: false,
            noRestore: false,
            args: ["--operation", "inspect"],
            env: new Dictionary<string, string>(),
            null,
            options,
            CancellationToken.None
        );

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DotNetCliRunner_RunAsync_WhenWatchIsFalse_DoesNotIncludeNonInteractiveFlag()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DotNetCliRunner>>();

        var options = new ProcessInvocationOptions();

        var executionContext = new CliExecutionContext(
            workingDirectory: workspace.WorkspaceRoot, hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("hives"), cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("cache"), sdksDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), logFilePath: "test.log"
        );

        var runner = DotNetCliRunnerTestHelper.Create(
            provider,
            executionContext,
            (args, env, workingDirectory, options) =>
            {
                // Verify that --non-interactive is NOT included when watch mode is disabled
                Assert.Contains("run", args);
                Assert.DoesNotContain("watch", args);
                Assert.DoesNotContain("--non-interactive", args);
            },
            0,
            logger: logger
        );

        var exitCode = await runner.RunAsync(
            projectFile: projectFile,
            watch: false, // This should NOT add --non-interactive
            noBuild: false,
            noRestore: false,
            args: ["--operation", "inspect"],
            env: new Dictionary<string, string>(),
            null,
            options,
            CancellationToken.None
        );

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DotNetCliRunner_RunAsync_WhenWatchIsTrueAndDebugIsTrue_IncludesVerboseFlag()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DotNetCliRunner>>();

        var options = new ProcessInvocationOptions { Debug = true };

        var executionContext = new CliExecutionContext(
            workingDirectory: workspace.WorkspaceRoot, hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("hives"), cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("cache"), sdksDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), logFilePath: "test.log"
        );

        var runner = DotNetCliRunnerTestHelper.Create(
            provider,
            executionContext,
            (args, env, workingDirectory, options) =>
            {
                // Verify that --verbose is included when watch mode and debug are both enabled
                Assert.Contains("watch", args);
                Assert.Contains("--verbose", args);

                // Verify the order: watch should come before --verbose
                var watchIndex = Array.IndexOf(args, "watch");
                var verboseIndex = Array.IndexOf(args, "--verbose");
                Assert.True(watchIndex < verboseIndex);
            },
            0,
            logger: logger
        );

        var exitCode = await runner.RunAsync(
            projectFile: projectFile,
            watch: true, // This should add --verbose when debug is true
            noBuild: false,
            noRestore: false,
            args: ["--operation", "inspect"],
            env: new Dictionary<string, string>(),
            null,
            options,
            CancellationToken.None
        );

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DotNetCliRunner_RunAsync_WhenWatchIsTrueAndDebugIsFalse_DoesNotIncludeVerboseFlag()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DotNetCliRunner>>();

        var options = new ProcessInvocationOptions { Debug = false };

        var executionContext = new CliExecutionContext(
            workingDirectory: workspace.WorkspaceRoot, hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("hives"), cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("cache"), sdksDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), logFilePath: "test.log"
        );

        var runner = DotNetCliRunnerTestHelper.Create(
            provider,
            executionContext,
            (args, env, workingDirectory, options) =>
            {
                // Verify that --verbose is NOT included when debug is false
                Assert.Contains("watch", args);
                Assert.DoesNotContain("--verbose", args);
            },
            0,
            logger: logger
        );

        var exitCode = await runner.RunAsync(
            projectFile: projectFile,
            watch: true, // This should NOT add --verbose when debug is false
            noBuild: false,
            noRestore: false,
            args: ["--operation", "inspect"],
            env: new Dictionary<string, string>(),
            null,
            options,
            CancellationToken.None
        );

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DotNetCliRunner_RunAsync_WhenWatchIsFalseAndDebugIsTrue_DoesNotIncludeVerboseFlag()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DotNetCliRunner>>();

        var options = new ProcessInvocationOptions { Debug = true };

        var executionContext = new CliExecutionContext(
            workingDirectory: workspace.WorkspaceRoot, hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("hives"), cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("cache"), sdksDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), logFilePath: "test.log"
        );

        var runner = DotNetCliRunnerTestHelper.Create(
            provider,
            executionContext,
            (args, env, workingDirectory, options) =>
            {
                // Verify that --verbose is NOT included when watch is false even if debug is true
                Assert.Contains("run", args);
                Assert.DoesNotContain("watch", args);
                Assert.DoesNotContain("--verbose", args);
            },
            0,
            logger: logger
        );

        var exitCode = await runner.RunAsync(
            projectFile: projectFile,
            watch: false, // This should NOT add --verbose because it's not in watch mode
            noBuild: false,
            noRestore: false,
            args: ["--operation", "inspect"],
            env: new Dictionary<string, string>(),
            null,
            options,
            CancellationToken.None
        );

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DotNetCliRunner_RunAsync_WhenWatchIsTrue_SetsSuppressLaunchBrowserEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DotNetCliRunner>>();

        var options = new ProcessInvocationOptions();

        var executionContext = new CliExecutionContext(
            workingDirectory: workspace.WorkspaceRoot, hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("hives"), cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("cache"), sdksDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), logFilePath: "test.log"
        );

        var runner = DotNetCliRunnerTestHelper.Create(
            provider,
            executionContext,
            (args, env, workingDirectory, options) =>
            {
                // Verify that DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER is set when watch mode is enabled
                Assert.NotNull(env);
                Assert.True(env.ContainsKey("DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"));
                Assert.Equal("true", env["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"]);
            },
            0,
            logger: logger
        );

        var exitCode = await runner.RunAsync(
            projectFile: projectFile,
            watch: true, // This should set DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=true
            noBuild: false,
            noRestore: false,
            args: ["--operation", "inspect"],
            env: new Dictionary<string, string>(),
            null,
            options,
            CancellationToken.None
        );

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DotNetCliRunner_RunAsync_WhenWatchIsFalse_DoesNotSetSuppressLaunchBrowserEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DotNetCliRunner>>();

        var options = new ProcessInvocationOptions();

        var executionContext = new CliExecutionContext(
            workingDirectory: workspace.WorkspaceRoot, hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("hives"), cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("cache"), sdksDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory: new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), logFilePath: "test.log"
        );

        var runner = DotNetCliRunnerTestHelper.Create(
            provider,
            executionContext,
            (args, env, workingDirectory, options) =>
            {
                // Verify that DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER is NOT set when watch mode is disabled
                if (env != null)
                {
                    Assert.False(env.ContainsKey("DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"));
                }
            },
            0,
            logger: logger
        );

        var exitCode = await runner.RunAsync(
            projectFile: projectFile,
            watch: false, // This should NOT set DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER
            noBuild: false,
            noRestore: false,
            args: ["--operation", "inspect"],
            env: new Dictionary<string, string>(),
            null,
            options,
            CancellationToken.None
        );

        Assert.Equal(0, exitCode);
    }

    private sealed class SingleFileAppHostProjectLocator : Aspire.Cli.Projects.IProjectLocator
    {
        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AppHostProjectSearchResult(new FileInfo("/tmp/apphost.cs"), [new FileInfo("/tmp/apphost.cs")]));
        }

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
        {
            // Return a .cs file to simulate single file AppHost
            return Task.FromResult<FileInfo?>(new FileInfo("/tmp/apphost.cs"));
        }

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
    }

    [Fact]
    public async Task RunCommand_WithNoBuildOption_SkipsBuildAndPassesNoBuildAndNoRestoreToRunner()
    {
        var buildCalled = false;
        var noBuildPassedToRunner = false;
        var noRestorePassedToRunner = false;

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;
            return backchannel;
        };

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) =>
            {
                buildCalled = true;
                return 0;
            };
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                noBuildPassedToRunner = noBuild;
                noRestorePassedToRunner = noRestore;
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);
                await Task.Delay(100, ct);
                return 0;
            };

            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --no-build");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

        Assert.False(buildCalled, "Build should be skipped when --no-build is specified");
        Assert.True(noBuildPassedToRunner, "noBuild=true should be passed to the runner when --no-build is specified");
        Assert.True(noRestorePassedToRunner, "noRestore=true should be passed to the runner when --no-build is specified (--no-build implies --no-restore)");
    }

    [Fact]
    public async Task RunCommand_WithIsolatedOption_SetsRandomizePortsAndIsolatesUserSecrets()
    {
        var tcs = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalUserSecretsId = Guid.NewGuid().ToString();

        // Set up user secrets file to simulate existing secrets
        var originalSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(originalUserSecretsId);
        var originalSecretsDir = Path.GetDirectoryName(originalSecretsPath)!;
        Directory.CreateDirectory(originalSecretsDir);
        File.WriteAllText(originalSecretsPath, """{"TestSecret": "TestValue"}""");

        try
        {
            var backchannelFactory = (IServiceProvider sp) =>
            {
                var backchannel = new TestAppHostBackchannel();
                backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;
                return backchannel;
            };

            var runnerFactory = (IServiceProvider sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
                runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

                // Return UserSecretsId when GetProjectItemsAndPropertiesAsync is called
                runner.GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, ct) =>
                {
                    var json = $$$"""{"Properties": {"UserSecretsId": "{{{originalUserSecretsId}}}"}}""";
                    var doc = JsonDocument.Parse(json);
                    return (0, doc);
                };

                runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
                {
                    // Capture environment variables
                    tcs.SetResult(env?.ToDictionary() ?? []);

                    var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                    backchannelCompletionSource!.SetResult(backchannel);
                    return 0;
                };

                return runner;
            };

            var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

            using var workspace = TemporaryWorkspace.Create(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.ProjectLocatorFactory = projectLocatorFactory;
                options.AppHostBackchannelFactory = backchannelFactory;
                options.DotNetCliRunnerFactory = runnerFactory;
            });

            using var provider = services.BuildServiceProvider();
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("run --isolated");

            using var cts = new CancellationTokenSource();
            var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);

            // Give the command time to start and set up
            var capturedEnv = await tcs.Task.DefaultTimeout();

            // Simulate CTRL-C
            cts.Cancel();

            var exitCode = await pendingRun.DefaultTimeout();
            Assert.Equal(ExitCodeConstants.Success, exitCode);

            // Verify DcpPublisher__RandomizePorts is set to true for isolated mode
            Assert.True(capturedEnv.ContainsKey("DcpPublisher__RandomizePorts"), "DcpPublisher__RandomizePorts should be set in isolated mode");
            Assert.Equal("true", capturedEnv["DcpPublisher__RandomizePorts"]);

            // Verify DOTNET_USER_SECRETS_ID is set to a different value (isolated secrets)
            Assert.True(capturedEnv.ContainsKey("DOTNET_USER_SECRETS_ID"), "DOTNET_USER_SECRETS_ID should be set in isolated mode with user secrets");
            Assert.NotEqual(originalUserSecretsId, capturedEnv["DOTNET_USER_SECRETS_ID"]);

            // Verify the isolated secrets ID is a valid GUID
            Assert.True(Guid.TryParse(capturedEnv["DOTNET_USER_SECRETS_ID"], out _), "Isolated user secrets ID should be a valid GUID");
        }
        finally
        {
            // Clean up the original secrets file we created
            if (File.Exists(originalSecretsPath))
            {
                File.Delete(originalSecretsPath);
            }
            if (Directory.Exists(originalSecretsDir) && !Directory.EnumerateFileSystemEntries(originalSecretsDir).Any())
            {
                Directory.Delete(originalSecretsDir);
            }
        }
    }

    [Fact]
    public async Task RunCommand_WithNoBuildAndWatchModeEnabled_ReturnsInvalidCommandError()
    {
        // This test verifies that when --no-build is specified and watch mode is enabled
        // (via feature flag), the CLI returns an error because this combination is not supported
        // (dotnet watch doesn't support --no-build)

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        // Create a features factory that enables DefaultWatchEnabled
        var featuresFactory = (IServiceProvider sp) => new TestFeatures()
            .SetFeature(KnownFeatures.DefaultWatchEnabled, true);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.FeatureFlagsFactory = featuresFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --no-build");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Should return InvalidCommand error because --no-build is not supported with watch mode enabled
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public void RunCommand_ForwardsArgumentsAfterDoubleDashToAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run -- --custom-arg value");

        Assert.Empty(result.Errors);
        Assert.Equal(["--custom-arg", "value"], AppHostLauncher.GetAppHostArguments(result));
    }

    [Fact]
    public void RunCommand_PreservesLiteralDoubleDashAppHostArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run -- arg1 -- arg2");

        Assert.Empty(result.Errors);
        Assert.Equal(["arg1", "--", "arg2"], AppHostLauncher.GetAppHostArguments(result));
    }

    [Fact]
    public async Task RunCommand_PassesArgumentsAfterDoubleDashToDotNetRunner()
    {
        var capturedArgs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            backchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;
            return backchannel;
        };

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                capturedArgs.SetResult(args);
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            };
            return runner;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run -- arg1=value1 --custom-arg");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);

        var args = await capturedArgs.Task.DefaultTimeout();
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(["arg1=value1", "--custom-arg"], args);
    }

    [Fact]
    public async Task RunCommand_WhenDelegatingToExtension_ForwardsArgumentsAfterDoubleDash()
    {
        DebugSessionOptions? capturedOptions = null;

        var extensionInteractionServiceFactory = (IServiceProvider sp) =>
        {
            var service = new TestExtensionInteractionService(sp);
            service.StartDebugSessionCallback = (_, _, _, options) =>
            {
                capturedOptions = options;
            };
            return service;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = extensionInteractionServiceFactory;
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run -- arg1=value1 --no-build");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedOptions);
        Assert.Equal("run", capturedOptions.Command);
        var debugSessionArgs = capturedOptions.Args;
        Assert.NotNull(debugSessionArgs);
        Assert.Equal(["--", "arg1=value1", "--no-build"], debugSessionArgs);
    }

    [Fact]
    public async Task CaptureAppHostLogsAsync_WritesCategoryWithAppHostPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test.log");
        var errorWriter = new TestStartupErrorWriter();
        using var fileLoggerProvider = new FileLoggerProvider(logFilePath, errorWriter);

        var entries = new BackchannelLogEntry[]
        {
            new()
            {
                Timestamp = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero),
                LogLevel = LogLevel.Information,
                Message = "Application started",
                EventId = new EventId(),
                CategoryName = "Microsoft.Hosting.Lifetime"
            },
            new()
            {
                Timestamp = new DateTimeOffset(2026, 3, 16, 12, 0, 1, TimeSpan.Zero),
                LogLevel = LogLevel.Warning,
                Message = "Slow response",
                EventId = new EventId(),
                CategoryName = "Microsoft.AspNetCore.Server.Kestrel"
            },
            new()
            {
                Timestamp = new DateTimeOffset(2026, 3, 16, 12, 0, 2, TimeSpan.Zero),
                LogLevel = LogLevel.Error,
                Message = "Connection refused",
                EventId = new EventId(),
                CategoryName = "SimpleCategory"
            },
        };

        var backchannel = new TestAppHostBackchannel();
        backchannel.GetAppHostLogEntriesAsyncCallback = YieldEntries;
        var interactionService = new TestInteractionService();

        await RunCommand.CaptureAppHostLogsAsync(fileLoggerProvider, backchannel, interactionService, CancellationToken.None);

        fileLoggerProvider.Dispose();

        var lines = await File.ReadAllLinesAsync(logFilePath);
        var nonEmptyLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        Assert.Collection(nonEmptyLines,
            line => Assert.Equal("[2026-03-16 12:00:00.000] [INFO] [AppHost/Lifetime] Application started", line),
            line => Assert.Equal("[2026-03-16 12:00:01.000] [WARN] [AppHost/Kestrel] Slow response", line),
            line => Assert.Equal("[2026-03-16 12:00:02.000] [FAIL] [AppHost/SimpleCategory] Connection refused", line));

        async IAsyncEnumerable<BackchannelLogEntry> YieldEntries([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                yield return entry;
            }
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunCommand_NonInteractive_SkipsExtensionDelegation()
    {
        // When `aspire start` spawns `aspire run --non-interactive`, the child process
        // may inherit ASPIRE_EXTENSION_* env vars from the parent terminal. Without the
        // --non-interactive guard, the child would delegate to the extension via
        // StartDebugSessionAsync and exit immediately instead of launching the AppHost.
        var startDebugSessionCalled = false;

        var extensionBackchannel = new TestExtensionBackchannel();
        extensionBackchannel.GetCapabilitiesAsyncCallback = ct => Task.FromResult(Array.Empty<string>());

        var appHostBackchannel = new TestAppHostBackchannel();
        appHostBackchannel.GetDashboardUrlsAsyncCallback = (ct) => Task.FromResult(new DashboardUrlsState
        {
            DashboardHealthy = true,
            BaseUrlWithLoginToken = "http://localhost/dashboard",
            CodespacesUrlWithLoginToken = null
        });
        appHostBackchannel.GetAppHostLogEntriesAsyncCallback = ReturnLogEntriesUntilCancelledAsync;

        var backchannelFactory = (IServiceProvider sp) => appHostBackchannel;

        var extensionInteractionServiceFactory = (IServiceProvider sp) =>
        {
            var service = new TestExtensionInteractionService(sp);
            service.StartDebugSessionCallback = (_, _, _, _) =>
            {
                startDebugSessionCalled = true;
            };
            return service;
        };

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            };
            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ExtensionBackchannelFactory = _ => extensionBackchannel;
            options.InteractionServiceFactory = extensionInteractionServiceFactory;
            // Deliberately NOT setting ASPIRE_EXTENSION_DEBUG_SESSION_ID —
            // without --non-interactive, this would trigger the early return.
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        // Parse with --non-interactive to simulate the child of `aspire start`
        var result = command.Parse("run --non-interactive");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(startDebugSessionCalled, "StartDebugSessionAsync should not be called in non-interactive mode.");
    }

    [Fact]
    public void DetachedChildEnvironmentFilter_PreservesDebugSessionVariables()
    {
        // Extension variables use the ASPIRE_EXTENSION_ prefix and should be filtered
        Assert.True(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.ExtensionEndpoint));
        Assert.True(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.ExtensionDebugSessionId));

        // DEBUG_SESSION variables should NOT be filtered
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionInfo));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionRunMode));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionPort));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionToken));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionServerCertificate));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DcpInstanceIdPrefix));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunCommand_RecordsRunAppHostTelemetryActivity(bool detached, bool isolated)
    {
        using var fixture = new TelemetryFixture();

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateServiceFactory = _ => new ThrowingCertificateService();
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.TelemetryFactory = _ => fixture.Telemetry;

            if (detached)
            {
                options.ConfigurationCallback += config =>
                {
                    config[KnownConfigNames.CliRunDetached] = "true";
                };
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var args = isolated ? "run --isolated" : "run";
        var result = command.Parse(args);

        await result.InvokeAsync().DefaultTimeout();

        Assert.NotNull(fixture.CapturedActivity);
        Assert.Equal(TelemetryConstants.Activities.RunAppHost, fixture.CapturedActivity.OperationName);

        var tags = fixture.CapturedActivity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(KnownLanguageId.CSharp, tags[TelemetryConstants.Tags.AppHostLanguage]);
        Assert.Equal(detached, tags[TelemetryConstants.Tags.AppHostDetached]);
        Assert.Equal(isolated, tags[TelemetryConstants.Tags.AppHostIsolated]);
        Assert.Equal("certificate_trust_failed", tags[TelemetryConstants.Tags.ErrorType]);
    }

}
