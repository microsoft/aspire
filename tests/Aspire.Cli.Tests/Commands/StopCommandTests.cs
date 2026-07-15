// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Layout;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Aspire.Hosting;
using Aspire.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class StopCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task StopCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task StopCommand_RejectsPositionalResourceArgument()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop myresource");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task StopCommand_WithInvalidExplicitAppHost_ReturnsFailedToFindProject()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    throw new ProjectLocatorException("Project file does not exist.", ProjectLocatorFailureReason.ProjectFileDoesntExist)
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --apphost missing-directory");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task StopCommand_WithExplicitAppHost_UsesProjectLocatorResolution()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectLocatorInvoked = false;
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("some-directory");
        var resolvedProjectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Resolved.AppHost", "Resolved.AppHost.csproj"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (projectFile, _, _, _) =>
                {
                    projectLocatorInvoked = true;
                    return Task.FromResult(new AppHostProjectSearchResult(resolvedProjectFile, [resolvedProjectFile]));
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"stop --apphost \"{appHostDirectory.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.True(projectLocatorInvoked);
        Assert.Equal(CliExitCodes.Success, exitCode);
        var displayedMessage = Assert.Single(interactionService.DisplayedMessages);
        Assert.Equal(
            string.Format(SharedCommandStrings.AppHostNotRunningAtPath, Path.Combine("Resolved.AppHost", "Resolved.AppHost.csproj")),
            displayedMessage.Message);
    }

    [Fact]
    public async Task StopCommand_AllIncludesEachAppHostPathInMessages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var statusMessages = new ConcurrentQueue<string>();
        interactionService.ShowStatusCallback = statusMessages.Enqueue;

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "AppHost.cs");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "AppHost.cs");
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(appHostPath1, int.MaxValue - 1));
        monitor.AddConnection("hash2", "socket.hash2", CreateConnection(appHostPath2, int.MaxValue - 2));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var expectedPath1 = Path.Combine("App1", "AppHost.cs");
        var expectedPath2 = Path.Combine("App2", "AppHost.cs");
        var displayedText = GetDisplayedText(interactionService, statusMessages);
        Assert.Contains(displayedText, message => message.Contains(expectedPath1, StringComparison.Ordinal));
        Assert.Contains(displayedText, message => message.Contains(expectedPath2, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopCommand_AllIncludesProcessIdWhenAppHostPathsCollide()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var statusMessages = new ConcurrentQueue<string>();
        interactionService.ShowStatusCallback = statusMessages.Enqueue;

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost", "App1.AppHost.csproj");
        var processId1 = int.MaxValue - 3;
        var processId2 = int.MaxValue - 4;
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(appHostPath, processId1));
        monitor.AddConnection("hash2", "socket.hash2", CreateConnection(appHostPath, processId2));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var expectedPath = "App1.AppHost.csproj";
        var expectedIdentifier1 = string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostIdentifierWithProcessId, expectedPath, processId1);
        var expectedIdentifier2 = string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostIdentifierWithProcessId, expectedPath, processId2);
        var displayedText = GetDisplayedText(interactionService, statusMessages);
        Assert.Contains(displayedText, message => message.Contains(expectedIdentifier1, StringComparison.Ordinal));
        Assert.Contains(displayedText, message => message.Contains(expectedIdentifier2, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopCommand_SingleAppHostIncludesIdentifierInStatusAndSuccessMessages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var statusMessages = new ConcurrentQueue<string>();
        interactionService.ShowStatusCallback = statusMessages.Enqueue;

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost", "App1.AppHost.csproj");
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(appHostPath, int.MaxValue - 5));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var expectedPath = Path.Combine("App1", "App1.AppHost", "App1.AppHost.csproj");
        Assert.Contains(statusMessages, message => message == string.Format(CultureInfo.CurrentCulture, StopCommandStrings.StoppingAppHost, expectedPath));
        Assert.Contains(interactionService.DisplayedSuccess, message => message == string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostStoppedSuccessfully, expectedPath));
    }

    [Fact]
    public async Task StopCommand_SingleOutOfScopeAppHostUsesFullPathInMessages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outOfScopeDir = workspace.CreateDirectory("out-of-scope");
        var interactionService = new TestInteractionService();
        var statusMessages = new ConcurrentQueue<string>();
        interactionService.ShowStatusCallback = statusMessages.Enqueue;

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var appHostPath = Path.Combine(outOfScopeDir.FullName, "App1", "App1.AppHost", "App1.AppHost.csproj");
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(appHostPath, int.MaxValue - 6, isInScope: false));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        Assert.Contains(statusMessages, message => message == string.Format(CultureInfo.CurrentCulture, StopCommandStrings.StoppingAppHost, appHostPath));
        Assert.Contains(interactionService.DisplayedSuccess, message => message == string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostStoppedSuccessfully, appHostPath));
    }

    [Fact]
    public async Task StopCommand_AllEmitsProfilingActivities()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stoppedActivities = new ConcurrentQueue<Activity>();

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var processId1 = int.MaxValue - 7;
        var processId2 = int.MaxValue - 8;
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(appHostPath1, processId1));
        monitor.AddConnection("hash2", "socket.hash2", CreateConnection(appHostPath2, processId2));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.ConfigurationCallback += config =>
            {
                config[KnownConfigNames.ProfilingEnabled] = "true";
            };
        });
        using var provider = services.BuildServiceProvider();
        var profilingTelemetry = provider.GetRequiredService<ProfilingTelemetry>();
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStopped: stoppedActivities.Enqueue);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var stopCommandActivity = Assert.Single(stoppedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.StopCommand);
        Assert.Equal(true, stopCommandActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostStopAll));
        Assert.Equal(2, stopCommandActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostStopCount));
        Assert.Equal(CliExitCodes.Success, stopCommandActivity.GetTagItem(TelemetryConstants.Tags.ProcessExitCode));

        var stopAppHostActivities = stoppedActivities.Where(activity => activity.OperationName == ProfilingTelemetry.Activities.StopAppHost).ToArray();
        Assert.Equal(2, stopAppHostActivities.Length);
        var expectedProcessIds = new[] { processId1, processId2 }.Order().ToArray();
        Assert.Equal(
            expectedProcessIds,
            stopAppHostActivities
                .Select(activity => Assert.IsType<int>(activity.GetTagItem(TelemetryConstants.Tags.ProcessPid)))
                .Order()
                .ToArray());
        Assert.All(stopAppHostActivities, activity => Assert.Equal(CliExitCodes.Success, activity.GetTagItem(TelemetryConstants.Tags.ProcessExitCode)));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("stop --all")]
    public async Task StopCommand_NoRunningAppHosts_ReturnsSuccess(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(commandLine);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var displayedMessage = Assert.Single(interactionService.DisplayedMessages);
        Assert.Equal(SharedCommandStrings.AppHostNotRunning, displayedMessage.Message);
    }

    [Fact]
    public async Task StopCommand_ForceInvokesDcpCleanupForResolvedAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory();
        var expectedWorkloadId = AppHostWorkloadId.Create(appHostFile, OperatingSystem.IsWindows());

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"stop --force --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertDcpCleanupInvocation(processFactory, expectedWorkloadId);
        Assert.Contains(interactionService.DisplayedMessages, message => message.Message == string.Format(SharedCommandStrings.AppHostNotRunningAtPath, Path.Combine("AppHost", "AppHost.csproj")));
        Assert.Contains(interactionService.DisplayedSuccess, message => message == string.Format(CultureInfo.CurrentCulture, StopCommandStrings.PersistentResourcesCleaned, appHostFile.Name));
    }

    [Fact]
    public async Task StopCommand_ForceInvokesDcpCleanupFromDiscoveredLayoutWhenBundleUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory();
        var expectedWorkloadId = AppHostWorkloadId.Create(appHostFile, OperatingSystem.IsWindows());

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        }, useBundleLayout: false);

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"stop --force --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertDcpCleanupInvocation(processFactory, expectedWorkloadId);
    }

    [Fact]
    public async Task StopCommand_ForceWithoutAppHostUsesRunningAppHostPathBeforeProjectDiscovery()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var runningAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("RunningAppHost");
        var runningAppHostFile = new FileInfo(Path.Combine(runningAppHostDirectory.FullName, "Running.AppHost.csproj"));
        await File.WriteAllTextAsync(runningAppHostFile.FullName, "<Project />");
        var discoveredAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("DiscoveredAppHost");
        var discoveredAppHostFile = new FileInfo(Path.Combine(discoveredAppHostDirectory.FullName, "Discovered.AppHost.csproj"));
        await File.WriteAllTextAsync(discoveredAppHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory();
        var expectedWorkloadId = AppHostWorkloadId.Create(runningAppHostFile, OperatingSystem.IsWindows());
        var projectLocatorInvoked = false;

        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(runningAppHostFile.FullName, int.MaxValue - 11));

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
            {
                projectLocatorInvoked = true;
                return Task.FromResult(new AppHostProjectSearchResult(discoveredAppHostFile, [discoveredAppHostFile]));
            }
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --force");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(projectLocatorInvoked);
        AssertDcpCleanupInvocation(processFactory, expectedWorkloadId);
    }

    [Fact]
    public async Task StopCommand_ForceNonInteractiveWithoutRunningAppHostFallsBackToProjectDiscovery()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory();
        var expectedWorkloadId = AppHostWorkloadId.Create(appHostFile, OperatingSystem.IsWindows());

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AuxiliaryBackchannelMonitorFactory = _ => new TestAuxiliaryBackchannelMonitor();
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --force");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertDcpCleanupInvocation(processFactory, expectedWorkloadId);
        Assert.Empty(interactionService.DisplayedErrors);
        Assert.Contains(interactionService.DisplayedMessages, message => message.Message == SharedCommandStrings.AppHostNotRunning);
    }

    [Fact]
    public async Task StopCommand_ForceNonInteractiveWithOnlyOutOfScopeAppHostsFallsBackToProjectDiscovery()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory();
        var expectedWorkloadId = AppHostWorkloadId.Create(appHostFile, OperatingSystem.IsWindows());

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var outOfScopeAppHostFile = Path.Combine(workspace.WorkspaceRoot.FullName, "OtherWorkspace", "Other.AppHost.csproj");
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(outOfScopeAppHostFile, int.MaxValue - 12, isInScope: false));

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --force");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertDcpCleanupInvocation(processFactory, expectedWorkloadId);
        Assert.Empty(interactionService.DisplayedErrors);
        Assert.Contains(interactionService.DisplayedMessages, message => message.Message == SharedCommandStrings.AppHostNotRunning);
    }

    [Fact]
    public async Task StopCommand_ForceReturnsFailureWhenDcpCleanupFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory
        {
            DefaultExitCode = 42,
            AttemptCallback = (_, _) => (42, "cleanup failed")
        };

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"stop --force --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.Contains(interactionService.DisplayedErrors, error => error.Contains("cleanup failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopCommand_ForceReturnsFailureWhenDcpIsUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory();

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        }, createDcpExecutable: false);

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"stop --force --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.Empty(processFactory.CreatedExecutions);
        Assert.Contains(StopCommandStrings.DcpCleanupUnavailable, interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task StopCommand_ForceReportsUnknownAppHostPathAfterNormalStop()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = null,
            IsInScope = true
        };
        monitor.AddConnection("hash1", connection.SocketPath, connection);
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(null, []))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --force");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
        Assert.Contains(StopCommandStrings.CouldNotDetermineAppHostPath, interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task StopCommand_ForceUsesStoppedConnectionPathForCleanupWhenProjectDiscoveryFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var processFactory = new TestProcessExecutionFactory();
        var expectedWorkloadId = AppHostWorkloadId.Create(appHostFile, OperatingSystem.IsWindows());

        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(appHostFile.FullName, int.MaxValue - 10));

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                throw new ProjectLocatorException("Multiple project files found.", ProjectLocatorFailureReason.MultipleProjectFilesFound)
        };

        var services = CreateStopForceServices(workspace, interactionService, processFactory, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --force");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertDcpCleanupInvocation(processFactory, expectedWorkloadId);
    }

    [Fact]
    public async Task StopCommand_ForceAndAllAreMutuallyExclusive()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop --force --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, StopCommandStrings.AllAndProjectMutuallyExclusive, "--all", "--force"),
            Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public async Task StopCommand_DeletesSocketFile_AfterSuccessfulStop()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17587: 'aspire stop' is the command
        // that leaks the socket, so it must delete the socket file once the AppHost has been confirmed stopped.
        // Otherwise a later command rediscovers the stale socket and tries to connect to a dead process.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();

        // The AppHost is reported with a process id that does not exist, so ProcessShutdownService observes
        // termination immediately and the stop reaches the socket-cleanup branch.
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var socketPath = Path.Combine(workspace.WorkspaceRoot.FullName, "a.sock");
        File.WriteAllText(socketPath, "");
        Assert.True(File.Exists(socketPath));

        var connection = CreateConnection(appHostPath, int.MaxValue - 9);
        connection.SocketPath = socketPath;

        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", socketPath, connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(socketPath));
    }

    private IServiceCollection CreateStopForceServices(
        TemporaryWorkspace workspace,
        TestInteractionService interactionService,
        TestProcessExecutionFactory processFactory,
        Action<CliServiceCollectionTestOptions>? configure = null,
        bool createDcpExecutable = true,
        bool useBundleLayout = true)
    {
        var layoutRoot = workspace.WorkspaceRoot.CreateSubdirectory("layout");
        var dcpDirectory = layoutRoot.CreateSubdirectory("dcp");
        if (createDcpExecutable)
        {
            File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), "");
        }

        var layout = new LayoutConfiguration
        {
            LayoutPath = layoutRoot.FullName,
            Components = new LayoutComponents { Dcp = "dcp" }
        };

        return CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.DotNetCliExecutionFactoryFactory = _ => processFactory;
            if (useBundleLayout)
            {
                options.BundleServiceFactory = _ => new TestBundleService(isBundle: true) { Layout = layout };
            }
            else
            {
                options.BundleServiceFactory = _ => new NullBundleService();
                options.LayoutDiscoveryFactory = _ => new FixedLayoutDiscovery(layout);
            }
            configure?.Invoke(options);
        });
    }

    private static void AssertDcpCleanupInvocation(TestProcessExecutionFactory processFactory, string expectedWorkloadId)
    {
        var execution = Assert.Single(processFactory.CreatedExecutions.OfType<TestProcessExecution>(), execution =>
            execution.Arguments.Count == 2 &&
            execution.Arguments[0] == "cleanup" &&
            execution.Arguments[1] == expectedWorkloadId);

        Assert.EndsWith(BundleDiscovery.GetDcpExecutableName(), execution.FileName, StringComparison.Ordinal);
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnection(string appHostPath, int processId, bool isInScope = true)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            Hash = $"hash-{processId.ToString(CultureInfo.InvariantCulture)}",
            SocketPath = $"socket.{processId.ToString(CultureInfo.InvariantCulture)}",
            IsInScope = isInScope,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = processId
            }
        };
    }

    private static string[] GetDisplayedText(TestInteractionService interactionService, ConcurrentQueue<string> statusMessages)
    {
        return interactionService.DisplayedMessages.Select(message => message.Message)
            .Concat(interactionService.DisplayedSuccess)
            .Concat(interactionService.DisplayedErrors)
            .Concat(statusMessages)
            .ToArray();
    }

}
