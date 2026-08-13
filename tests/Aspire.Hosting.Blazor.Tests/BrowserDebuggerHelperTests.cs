// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Blazor.Tests;

public class BrowserDebuggerHelperTests
{
    [Theory]
    [InlineData("Exited")]
    [InlineData("Finished")]
    [InlineData("FailedToStart")]
    [InlineData("Terminated")]
    public async Task WatchForDebuggerStopAsync_ResetsSessionForTerminalState(string terminalState)
    {
        using var notificationService = ResourceNotificationServiceTestHelpers.Create();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(notificationService)
            .BuildServiceProvider();
        var commandTarget = new ContainerResource("command-target");
        var debuggerResource = new ContainerResource("debugger");

        await notificationService.PublishUpdateAsync(
            debuggerResource,
            snapshot => snapshot with { State = KnownResourceStates.Starting }).DefaultTimeout();
        var callbackCount = 0;
        var watcherTask = BrowserDebuggerHelper.WatchForDebuggerStopAsync(
            serviceProvider,
            commandTarget,
            debuggerResource,
            CancellationToken.None,
            () => Interlocked.Increment(ref callbackCount));

        await notificationService.PublishUpdateAsync(
            debuggerResource,
            snapshot => snapshot with { State = terminalState }).DefaultTimeout();

        await watcherTask.DefaultTimeout();

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task WatchForDebuggerStopAsync_ResetsSessionWhenStartedResourceReturnsToNotStarted()
    {
        using var notificationService = ResourceNotificationServiceTestHelpers.Create();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(notificationService)
            .BuildServiceProvider();
        var commandTarget = new ContainerResource("command-target");
        var debuggerResource = new ContainerResource("debugger");

        await notificationService.PublishUpdateAsync(
            debuggerResource,
            snapshot => snapshot with { State = KnownResourceStates.Starting }).DefaultTimeout();

        var callbackCount = 0;
        var watcherTask = BrowserDebuggerHelper.WatchForDebuggerStopAsync(
            serviceProvider,
            commandTarget,
            debuggerResource,
            CancellationToken.None,
            () => Interlocked.Increment(ref callbackCount));

        await notificationService.PublishUpdateAsync(
            debuggerResource,
            snapshot => snapshot with { State = KnownResourceStates.NotStarted }).DefaultTimeout();

        await watcherTask.DefaultTimeout();

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task WatchForDebuggerStopAsync_CancellationCompletesWithoutResettingSession()
    {
        using var notificationService = ResourceNotificationServiceTestHelpers.Create();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(notificationService)
            .BuildServiceProvider();
        var commandTarget = new ContainerResource("command-target");
        var debuggerResource = new ContainerResource("debugger");
        using var watcherCts = new CancellationTokenSource();
        var callbackCount = 0;
        var watcherTask = BrowserDebuggerHelper.WatchForDebuggerStopAsync(
            serviceProvider,
            commandTarget,
            debuggerResource,
            watcherCts.Token,
            () => Interlocked.Increment(ref callbackCount));

        await watcherCts.CancelAsync();

        await watcherTask.DefaultTimeout();
        Assert.Equal(0, callbackCount);
    }
}