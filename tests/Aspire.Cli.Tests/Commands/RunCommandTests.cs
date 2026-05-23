// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
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
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using StreamJsonRpc;

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
        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
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

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
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
        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
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

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
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
        var message = AppHostLauncher.GetDetachedFailureMessage(CliExitCodes.FailedToBuildArtifacts);

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
        public Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Project file does not exist.", Aspire.Cli.Projects.ProjectLocatorFailureReason.ProjectFileDoesntExist);
        }

        public Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Project file does not exist.", Aspire.Cli.Projects.ProjectLocatorFailureReason.ProjectFileDoesntExist);
        }

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
        Assert.Equal(CliExitCodes.FailedToTrustCertificates, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenBackchannelDisconnectsDuringStartup_WaitsForAppHostExitAndSurfacesWrappedError()
    {
        // Covers the catastrophic-disconnect path: the backchannel itself died (e.g. AppHost
        // crashed mid-startup) so the GetDashboardUrlsAsync call surfaces a ConnectionLostException.
        // We want to give the dying AppHost a chance to write a final error to its own captured
        // output before we surface the CLI-side wrapper, so the CLI waits on pendingRun (with a
        // Ctrl+C-aware status) and then reports both the AppHost narrative and the wrapped fault.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var dashboardRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appHostCanExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, cancellationToken) =>
            {
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetDashboardUrlsAsyncCallback = _ =>
                    {
                        dashboardRequested.TrySetResult();
                        throw new ConnectionLostException("Backchannel dropped while fetching dashboard URLs.");
                    }
                });

                await dashboardRequested.Task.WaitAsync(cancellationToken);
                interactionService.DisplayLines(
                [
                    (OutputLineStream.StdErr, "Endpoint 'http' must specify a port when isProxied is false.")
                ]);

                await appHostCanExit.Task.WaitAsync(cancellationToken);

                return CliExitCodes.FailedToDotnetRunAppHost;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        var pendingCommand = result.InvokeAsync();

        // The backchannel connection died, so the CLI is waiting on pendingRun for the AppHost to
        // surface its real exit code/output. The command must not complete until the AppHost does.
        await dashboardRequested.Task.DefaultTimeout();
        appHostCanExit.SetResult();

        var exitCode = await pendingCommand.DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.Contains(RunCommandStrings.AppHostConnectionLostWaitingForExit, interactionService.ShownStatuses);
        Assert.Contains(interactionService.DisplayedLines, line => line.Line == "Endpoint 'http' must specify a port when isProxied is false.");
        Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("An unexpected error occurred", StringComparison.Ordinal));
        Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("Backchannel dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCommand_WhenDashboardRpcHandlerFaultsButConnectionStaysAlive_SurfacesImmediatelyWithoutWaiting()
    {
        // Covers the server-side-handler-fault path: the RPC channel is alive and the AppHost is
        // still running, but GetDashboardUrlsAsync's server-side handler threw. There is no
        // catastrophic exit to wait for - the RPC payload is already the real cause, so the CLI
        // must surface the wrapped error immediately rather than hanging on pendingRun. This
        // preserves pre-PR behavior for this failure shape.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var dashboardRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appHostCanExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, cancellationToken) =>
            {
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetDashboardUrlsAsyncCallback = _ =>
                    {
                        dashboardRequested.TrySetResult();
                        throw new IOException("Dashboard URL handler threw.");
                    }
                });

                await appHostCanExit.Task.WaitAsync(cancellationToken);
                return CliExitCodes.FailedToDotnetRunAppHost;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        var pendingCommand = result.InvokeAsync();

        // The command must surface the wrapped error and return without waiting for the AppHost
        // to exit, since the channel is still alive and the AppHost is not necessarily exiting.
        var exitCode = await pendingCommand.DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.DoesNotContain(RunCommandStrings.AppHostConnectionLostWaitingForExit, interactionService.ShownStatuses);
        Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("An unexpected error occurred", StringComparison.Ordinal));
        Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("Dashboard URL handler threw", StringComparison.Ordinal));

        // Release the stub project task so the background callback can complete and not leak.
        appHostCanExit.SetResult();
    }

    [Fact]
    public async Task RunCommand_WhenAppHostRunFaultsDuringStartup_ReturnsFailureExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var dashboardRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, cancellationToken) =>
            {
                var outputCollector = new OutputCollector();
                context.OutputCollector = outputCollector;
                outputCollector.AppendError("MSB3277: Found conflicts between different versions of a dependency.");
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetDashboardUrlsAsyncCallback = async ct =>
                    {
                        dashboardRequested.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        return new DashboardUrlsState { DashboardHealthy = true };
                    }
                });

                await dashboardRequested.Task.WaitAsync(cancellationToken);
                outputCollector.AppendError("System.InvalidOperationException: AppHost failed before returning an exit code.");

                throw new InvalidOperationException("RunAsync failed before returning an exit code.");
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.Contains(interactionService.DisplayedLines, line => line.Line.Contains("AppHost failed before returning an exit code", StringComparison.Ordinal));
        Assert.DoesNotContain(interactionService.DisplayedLines, line => line.Line.Contains("MSB3277", StringComparison.Ordinal));
        Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("An unexpected error occurred", StringComparison.Ordinal));
        Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("RunAsync failed before returning an exit code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCommand_DetachedEarlyExit_PropagatesExitCodeWithoutUnexpectedErrorWrapper()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        const int detachedExitCode = 42;
        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, cancellationToken) =>
            {
                var outputCollector = new OutputCollector();
                context.OutputCollector = outputCollector;
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetDashboardUrlsAsyncCallback = _ => Task.FromResult(new DashboardUrlsState { DashboardHealthy = true })
                });

                // Simulate a detached-start child AppHost that exits cleanly during the
                // early-exit observation window after the backchannel handshake completes.
                await Task.Delay(50, cancellationToken);
                return detachedExitCode;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
            options.ConfigurationCallback += config => config[KnownConfigNames.CliRunDetached] = "true";
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(detachedExitCode, exitCode);
        // The detached early-exit case is an expected outcome (the AppHost intentionally
        // exited after handshake) so it must not be wrapped with the generic
        // "An unexpected error occurred" template - the exit code carries the narrative.
        Assert.DoesNotContain(interactionService.DisplayedErrors, error => error.Contains("An unexpected error occurred", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCommand_WhenCancelledDuringStartupRpc_CompletesSuccessfully()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cts = new CancellationTokenSource();
        var runCanExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var interactionService = new TestInteractionService();
        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, _) =>
            {
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetDashboardUrlsAsyncCallback = ct =>
                    {
                        cts.Cancel();
                        return Task.FromCanceled<DashboardUrlsState>(ct);
                    }
                });

                await runCanExit.Task;
                return CliExitCodes.Success;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        try
        {
            var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Empty(interactionService.DisplayedErrors);
        }
        finally
        {
            runCanExit.TrySetResult();
        }
    }

    [Fact]
    public async Task RunCommand_WhenStartupRpcThrowsUnrelatedCancellationAfterUserCancellation_DoesNotTreatRunAsSuccessful()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cts = new CancellationTokenSource();
        using var unrelatedCts = new CancellationTokenSource();
        var runCanExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var interactionService = new TestInteractionService();
        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, _) =>
            {
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetDashboardUrlsAsyncCallback = _ =>
                    {
                        cts.Cancel();
                        unrelatedCts.Cancel();
                        return Task.FromCanceled<DashboardUrlsState>(unrelatedCts.Token);
                    }
                });

                await runCanExit.Task;
                return CliExitCodes.Success;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        try
        {
            var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

            Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
            Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("unexpected error", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            runCanExit.TrySetResult();
        }
    }

    [Fact]
    public async Task RunCommand_WhenAppHostExitsDuringStartup_DisplaysCapturedAppHostOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var dashboardRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, cancellationToken) =>
            {
                var outputCollector = new OutputCollector();
                context.OutputCollector = outputCollector;
                outputCollector.AppendError("MSB3277: Found conflicts between different versions of a dependency.");
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetDashboardUrlsAsyncCallback = async ct =>
                    {
                        dashboardRequested.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        return new DashboardUrlsState { DashboardHealthy = true };
                    }
                });

                await dashboardRequested.Task.WaitAsync(cancellationToken);
                outputCollector.AppendOutput("Build succeeded.");
                outputCollector.AppendError("System.InvalidOperationException: Service 'frontend' needs to specify a port for endpoint 'http' since it isn't using a proxy.");

                return CliExitCodes.FailedToDotnetRunAppHost;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.Contains(interactionService.DisplayedMessages, message => message.Message == $"{RunCommandStrings.RecentAppHostStartupOutput}:");
        Assert.Contains(interactionService.DisplayedLines, line => line.Line.Contains("Service 'frontend' needs to specify a port", StringComparison.Ordinal));
        Assert.DoesNotContain(interactionService.DisplayedLines, line => line.Line.Contains("Build succeeded", StringComparison.Ordinal));
        Assert.DoesNotContain(interactionService.DisplayedLines, line => line.Line.Contains("MSB3277", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCommand_WhenAppHostExitsBeforeBackchannelConnects_DisplaysCapturedAppHostOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var connectingToAppHost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        interactionService.ShowStatusCallback = status =>
        {
            if (status == RunCommandStrings.ConnectingToAppHost)
            {
                connectingToAppHost.TrySetResult();
            }
        };

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, cancellationToken) =>
            {
                var outputCollector = new OutputCollector();
                context.OutputCollector = outputCollector;
                outputCollector.AppendError("MSB3277: Found conflicts between different versions of a dependency.");
                context.BuildCompletionSource?.TrySetResult(true);

                await connectingToAppHost.Task.WaitAsync(cancellationToken);
                outputCollector.AppendError("System.InvalidOperationException: Service 'frontend' needs to specify a port for endpoint 'http' since it isn't using a proxy.");

                return CliExitCodes.FailedToDotnetRunAppHost;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.Contains(interactionService.DisplayedMessages, message => message.Message == $"{RunCommandStrings.RecentAppHostStartupOutput}:");
        Assert.Contains(interactionService.DisplayedLines, line => line.Line.Contains("Service 'frontend' needs to specify a port", StringComparison.Ordinal));
        Assert.DoesNotContain(interactionService.DisplayedLines, line => line.Line.Contains("MSB3277", StringComparison.Ordinal));
        Assert.DoesNotContain(interactionService.DisplayedErrors, error => error.Contains("Timed out waiting for AppHost server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCommand_WhenAppHostExitsDuringStartup_CancelsAndObservesLogCapture()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var dashboardRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logCaptureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logCaptureCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectFactory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, cancellationToken) =>
            {
                context.BuildCompletionSource?.TrySetResult(true);
                context.BackchannelCompletionSource?.TrySetResult(new TestAppHostBackchannel
                {
                    GetAppHostLogEntriesAsyncCallback = ct => CaptureLogsUntilCancelledAsync(logCaptureStarted, logCaptureCancelled, ct),
                    GetDashboardUrlsAsyncCallback = async ct =>
                    {
                        dashboardRequested.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        return new DashboardUrlsState { DashboardHealthy = true };
                    }
                });

                await dashboardRequested.Task.WaitAsync(cancellationToken);
                await logCaptureStarted.Task.WaitAsync(cancellationToken);

                return CliExitCodes.FailedToDotnetRunAppHost;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        await logCaptureCancelled.Task.DefaultTimeout();
        Assert.DoesNotContain(interactionService.DisplayedMessages, message => message.Message == "No longer receiving logs from AppHost.");

        static async IAsyncEnumerable<BackchannelLogEntry> CaptureLogsUntilCancelledAsync(
            TaskCompletionSource started,
            TaskCompletionSource cancelled,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                }
            }

            yield break;
        }
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
        public Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("No project file found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.NoProjectFileFound);
        }

        public Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("No project file found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.NoProjectFileFound);
        }

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
        public Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Multiple project files found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.MultipleProjectFilesFound);
        }

        public Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            throw new Aspire.Cli.Projects.ProjectLocatorException("Multiple project files found.", Aspire.Cli.Projects.ProjectLocatorFailureReason.MultipleProjectFilesFound);
        }

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

    private static async IAsyncEnumerable<BackchannelLogEntry> EmptyLogEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
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
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenAppHostReturnsCancelled_CompletesSuccessfully()
    {
        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                return Task.FromResult(CliExitCodes.Cancelled);
            };

            return runner;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.AppHostBackchannelFactory = _ => new TestAppHostBackchannel
            {
                GetDashboardUrlsAsyncCallback = _ => Task.FromResult(new DashboardUrlsState
                {
                    DashboardHealthy = true,
                    BaseUrlWithLoginToken = "http://localhost:5000/login?t=abcd"
                })
            };
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task RunCommand_WithCaptureProfile_TreatsRequestedStopAsSuccess()
    {
        var appHostExitCode = new TaskCompletionSource<int>();
        var requestStopCalled = new TaskCompletionSource();

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                return await appHostExitCode.Task.WaitAsync(ct);
            };

            return runner;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.AppHostBackchannelFactory = _ => new TestAppHostBackchannel
            {
                RequestStopAsyncCalled = requestStopCalled,
                RequestStopAsyncCallback = () =>
                {
                    appHostExitCode.SetResult(137);
                    return Task.CompletedTask;
                },
                GetAppHostLogEntriesAsyncCallback = EmptyLogEntriesAsync
            };
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --capture-profile");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(requestStopCalled.Task.IsCompleted, "Capture mode should stop the AppHost after startup.");
    }

    [Fact]
    public async Task RunCommand_WithCaptureProfile_PreservesExitCodeWhenRunCompletesBeforeStop()
    {
        var requestStopCalled = new TaskCompletionSource();

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                return Task.FromResult(123);
            };

            return runner;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.AppHostBackchannelFactory = _ => new TestAppHostBackchannel
            {
                RequestStopAsyncCalled = requestStopCalled,
                GetAppHostLogEntriesAsyncCallback = EmptyLogEntriesAsync
            };
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --capture-profile");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(123, exitCode);
        Assert.False(requestStopCalled.Task.IsCompleted, "Capture mode should not mask an AppHost exit before the stop request.");
    }

    [Fact]
    public async Task RunCommand_WithCaptureProfile_PropagatesFailureExitCodeAfterStop()
    {
        var appHostExitCode = new TaskCompletionSource<int>();
        var requestStopCalled = new TaskCompletionSource();

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                return await appHostExitCode.Task.WaitAsync(ct);
            };

            return runner;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.AppHostBackchannelFactory = _ => new TestAppHostBackchannel
            {
                RequestStopAsyncCalled = requestStopCalled,
                RequestStopAsyncCallback = () =>
                {
                    // Simulate an AppHost that crashes during shutdown rather than terminating
                    // via a known teardown signal. Capture mode must surface that failure.
                    appHostExitCode.SetResult(CliExitCodes.FailedToDotnetRunAppHost);
                    return Task.CompletedTask;
                },
                GetAppHostLogEntriesAsyncCallback = EmptyLogEntriesAsync
            };
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --capture-profile");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.True(requestStopCalled.Task.IsCompleted, "Capture mode should stop the AppHost after startup.");
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
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public void RenderAppHostSummary_RendersLogsPathAsClickableFileLink()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = int.MaxValue;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [run].log");
        var executionContext = workspace.CreateExecutionContext(logFilePath: logFilePath);

        var interactionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLoggerFactory.Instance);

        RunCommand.RenderAppHostSummary(
            interactionService,
            "AppHost.csproj",
            dashboardUrl: "http://localhost:1234",
            codespacesUrl: null,
            logFilePath,
            isExtensionHost: false);

        var outputString = output.ToString();
        var fileUri = new Uri(Path.GetFullPath(logFilePath)).AbsoluteUri;

        Assert.Contains("Logs", outputString);
        TerminalLinkAssert.ContainsLink(outputString, fileUri, logFilePath);
    }

    [Fact]
    public async Task RunCommand_WhenDashboardFailsToStart_ContinuesWithWarning()
    {

        var backchannelFactory = (IServiceProvider sp) =>
        {
            var backchannel = new TestAppHostBackchannel();
            // Configure the backchannel to return unhealthy dashboard state
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
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.InteractionServiceFactory = (sp) => testInteractionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);

        // Simulate CTRL-C - the command should continue past the unhealthy dashboard
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout(TestConstants.LongTimeoutDuration);

        // The command should handle cancellation gracefully
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(testInteractionService.DisplayedCancellations);

        // Verify a warning was displayed (not an error)
        var m = Assert.Single(testInteractionService.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, m.Emoji);
        Assert.Equal(RunCommandStrings.DashboardFailedToStart, m.Message);
        Assert.Empty(testInteractionService.DisplayedErrors);
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

        Assert.Equal(CliExitCodes.Success, exitCode);
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

        Assert.Equal(CliExitCodes.Success, exitCode);
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
        var result = command.Parse("run --start-debug-session");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);

        // Wait for the build to be called before cancelling
        await buildCalledTcs.Task.DefaultTimeout();
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(buildCalled, "Build should be called when extension has build-dotnet-using-cli capability.");
    }

    [Fact]
    public async Task RunCommand_WhenExtensionNoDebugBuildFails_DoesNotRunAppHost()
    {
        var buildCalled = false;
        var runCalled = false;

        var extensionBackchannel = new TestExtensionBackchannel();
        extensionBackchannel.HasCapabilityAsyncCallback = (capability, ct) => Task.FromResult(capability == KnownCapabilities.BuildDotnetUsingCli);

        var extensionInteractionServiceFactory = (IServiceProvider sp) => new TestExtensionInteractionService(sp);

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) =>
            {
                buildCalled = true;
                return 1;
            };
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                runCalled = true;
                return Task.FromResult(0);
            };
            return runner;
        };

        var projectLocatorFactory = (IServiceProvider sp) => new TestProjectLocator();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ExtensionBackchannelFactory = _ => extensionBackchannel;
            options.InteractionServiceFactory = extensionInteractionServiceFactory;
            options.ConfigurationCallback += config =>
            {
                config["ASPIRE_EXTENSION_DEBUG_SESSION_ID"] = "test-session-id";
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode);
        Assert.True(buildCalled, "Build should be called before launching the AppHost in extension no-debug mode.");
        Assert.False(runCalled, "AppHost should not be launched when the pre-build fails.");
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
    public async Task RunCommand_WhenDefaultWatchEnabledFeatureFlagIsTrueAndBuildFails_ReturnsBuildFailure()
    {
        var runCalled = false;

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) =>
            {
                options.StandardErrorCallback?.Invoke("error CS0103: The name 'MissingSymbol' does not exist in the current context");
                return 1;
            };

            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());

            runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                runCalled = true;
                return Task.FromResult(0);
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

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode);
        Assert.False(runCalled, "The AppHost should not be started when the initial build fails in watch mode.");
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

        var executionContext = workspace.CreateExecutionContext();

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

        var executionContext = workspace.CreateExecutionContext();

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

        var executionContext = workspace.CreateExecutionContext();

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

        var executionContext = workspace.CreateExecutionContext();

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

        var executionContext = workspace.CreateExecutionContext();

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

        var executionContext = workspace.CreateExecutionContext();

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

        var executionContext = workspace.CreateExecutionContext();

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
        public Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            var appHostFile = new FileInfo("/tmp/apphost.cs");
            return Task.FromResult<List<AppHostProjectCandidate>>([new(appHostFile, "test")]);
        }

        public Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
        {
            return Task.FromResult<List<FileInfo>>([new FileInfo("/tmp/apphost.cs")]);
        }

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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

                // After issue #17197 the AppHost MSBuild inspection is fetched once and cached,
                // so the IsAspireHost shape and UserSecretsId both come back in the same response.
                runner.GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, ct) =>
                {
                    var json = $$$"""
                        {
                          "Properties": {
                            "IsAspireHost": "true",
                            "AspireHostingSDKVersion": "{{{VersionHelper.GetDefaultTemplateVersion()}}}",
                            "UserSecretsId": "{{{originalUserSecretsId}}}"
                          },
                          "Items": {}
                        }
                        """;
                    return (0, JsonDocument.Parse(json));
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
            Assert.Equal(CliExitCodes.Success, exitCode);

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
        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
    }

    [Fact]
    public void RunCommand_ForwardsUnmatchedTokensToAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run -- --custom-arg value");

        Assert.Empty(result.Errors);
        Assert.Contains("--custom-arg", result.UnmatchedTokens);
        Assert.Contains("value", result.UnmatchedTokens);
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

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(startDebugSessionCalled, "StartDebugSessionAsync should not be called in non-interactive mode.");
    }

    [Fact]
    public async Task RunCommand_AllowsNoBuildInActiveExtensionDebugSession()
    {
        var startDebugSessionCalled = false;
        var runNoBuildValue = false;

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
                runNoBuildValue = noBuild;
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
            options.ConfigurationCallback += config => config[KnownConfigNames.ExtensionDebugSessionId] = "existing-session";
            options.ProjectLocatorFactory = projectLocatorFactory;
            options.AppHostBackchannelFactory = backchannelFactory;
            options.DotNetCliRunnerFactory = runnerFactory;
            options.ExtensionBackchannelFactory = _ => extensionBackchannel;
            options.InteractionServiceFactory = extensionInteractionServiceFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --no-build");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(startDebugSessionCalled, "StartDebugSessionAsync should not be called from an active extension debug session.");
        Assert.True(runNoBuildValue);
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
