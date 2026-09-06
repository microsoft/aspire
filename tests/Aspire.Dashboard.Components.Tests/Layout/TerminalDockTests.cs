// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Grpc.Core;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using FluentMessageIntent = Microsoft.FluentUI.AspNetCore.Components.MessageIntent;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class TerminalDockTests : DashboardTestContext
{
    [Fact]
    public async Task WatchUpdates_ReplaceSnapshotAndPreservePanelUntilActivated()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();

        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second"));
        cut.WaitForAssertion(() => Assert.Equal("first", cut.Find(".terminal-dock-tab.active").TextContent.Trim()));

        await cut.Find(".terminal-dock-new").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-dock-panel")));
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Added, "third"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".terminal-dock-tab").Count);
            Assert.Single(cut.FindAll(".terminal-dock-panel"));
            Assert.Empty(cut.FindAll(".terminal-dock-tab.active"));
        });

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "second"));
        cut.WaitForAssertion(() => Assert.Equal("second", cut.Find(".terminal-dock-tab.active").TextContent.Trim()));

        // A reconnect snapshot can omit the selected terminal without sending its individual removal.
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("replacement"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("replacement", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
            Assert.Single(cut.FindComponents<TerminalView>());
        });

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
        await Services.GetRequiredService<ShortcutManager>().OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock);
    }

    [Fact]
    public async Task CloseInactiveTab_DoesNotChangeSelection()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second", "third"));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".terminal-dock-tab").Count));

        await cut.FindAll(".terminal-dock-tab-close")[1].ClickAsync(new());
        Assert.Equal(["second"], client.ClosedTerminals.ToArray());
        Assert.Equal("first", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
        Assert.Empty(Services.GetRequiredService<INotificationService>().GetNotifications());

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Removed, "second"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll(".terminal-dock-tab").Count);
            Assert.Equal("first", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTab_TimesOut_NotifiesEvenAfterTabRemoval(bool removeWhileWaiting)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(
            terminalChannelProvider: () => updates,
            closeTerminal: (_, _) => completion.Task);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var toasts = new ConcurrentQueue<ToastParameters>();
        Services.GetRequiredService<IToastService>().OnShow += (_, parameters, _) => toasts.Enqueue(parameters);
        var notifications = Services.GetRequiredService<INotificationService>();
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        var snapshot = TerminalSetupHelpers.Snapshot("terminal-id");
        snapshot.Snapshot.Terminals[0].Title = "Setup shell";
        await updates.Writer.WriteAsync(snapshot);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-dock-tab")));

        var close = cut.Find(".terminal-dock-tab-close").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(["terminal-id"], client.ClosedTerminals.ToArray()));
        Assert.Empty(notifications.GetNotifications());

        if (removeWhileWaiting)
        {
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Removed, "terminal-id"));
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".terminal-dock-tab")));
        }

        completion.SetException(new RpcException(new Status(StatusCode.DeadlineExceeded, "Terminal disposal timed out.")));
        await close.DefaultTimeout();

        var notification = Assert.Single(notifications.GetNotifications()).Entry;
        Assert.Equal("Terminal close timed out", notification.Title);
        Assert.Equal("Timed out waiting for terminal 'Setup shell' to shut down. Cleanup is continuing in the background.", notification.Body);
        Assert.Equal(FluentMessageIntent.Warning, notification.Intent);
        var toast = Assert.Single(toasts);
        Assert.Equal(ToastIntent.Warning, toast.Intent);
        Assert.Equal(notification.Body, toast.Title);
        Assert.Equal(1, notifications.UnreadCount);
    }

    [Fact]
    public async Task HideDock_DoesNotCloseTerminals()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".terminal-dock-tab").Count));

        await cut.Find(".terminal-dock-collapse").ClickAsync(new());

        Assert.Single(cut.FindAll(".terminal-dock.collapsed"));
        Assert.Empty(client.ClosedTerminals);
        Assert.Empty(Services.GetRequiredService<INotificationService>().GetNotifications());
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        Assert.Single(cut.FindAll(".terminal-dock.visible"));
        Assert.Equal(2, cut.FindAll(".terminal-dock-tab").Count);
    }

    [Fact]
    public async Task CloseTab_ComponentDisposed_CancelsWaitWithoutNotification()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(
            terminalChannelProvider: () => updates,
            closeTerminal: (_, cancellationToken) =>
            {
                started.SetResult(cancellationToken);
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("terminal"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-dock-tab")));

        var close = cut.Find(".terminal-dock-tab-close").ClickAsync(new());
        var token = await started.Task.DefaultTimeout();
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        await close.DefaultTimeout();

        Assert.True(token.IsCancellationRequested);
        Assert.Empty(Services.GetRequiredService<INotificationService>().GetNotifications());
    }

    [Theory]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task CloseTab_ResponseAfterComponentDisposal_DoesNotNotify(StatusCode statusCode)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(
            terminalChannelProvider: () => updates,
            closeTerminal: (_, _) => completion.Task);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var toasts = new ConcurrentQueue<ToastParameters>();
        Services.GetRequiredService<IToastService>().OnShow += (_, parameters, _) => toasts.Enqueue(parameters);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("terminal"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-dock-tab")));

        var close = cut.Find(".terminal-dock-tab-close").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Single(client.ClosedTerminals));
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        completion.SetException(new RpcException(new Status(statusCode, "Close interrupted.")));
        await close.DefaultTimeout();

        Assert.Empty(Services.GetRequiredService<INotificationService>().GetNotifications());
        Assert.Empty(toasts);
    }

    [Fact]
    public async Task RecoverySnapshot_ClosesWindowForMissingTerminal()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "detached"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<TerminalView>()));

        await cut.Find(".terminal-dock-detach").ClickAsync(new());
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".terminal-dock-detached"));
            Assert.Empty(cut.FindComponents<TerminalView>());
        });

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("remaining"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("remaining", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
        });

        // The snapshot renders before window cleanup, and the JS call does not itself cause another render.
        // Wait for that side effect independently of bUnit's render-triggered assertions.
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => JSInterop.Invocations.Any(i => i.Identifier == "closeTerminalWindow"),
            "The removed terminal's detached window was not closed.");
        var close = Assert.Single(JSInterop.Invocations, i => i.Identifier == "closeTerminalWindow");
        Assert.Equal("detached", close.Arguments[0]);
    }
}
