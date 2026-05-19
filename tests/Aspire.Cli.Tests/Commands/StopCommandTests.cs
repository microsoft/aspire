// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class StopCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task StopCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var outOfScopeWorkspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var statusMessages = new ConcurrentQueue<string>();
        interactionService.ShowStatusCallback = statusMessages.Enqueue;

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var appHostPath = Path.Combine(outOfScopeWorkspace.WorkspaceRoot.FullName, "App1", "App1.AppHost", "App1.AppHost.csproj");
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var stoppedActivities = new ConcurrentQueue<Activity>();
        using var listener = CreateProfilingActivityListener(stoppedActivities.Enqueue);

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

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProfilingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activityStopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
