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
    [Theory]
    [InlineData(400, 900, 120, 900, 400)]
    [InlineData(-1, 900, 120, 900, 120)]
    [InlineData(1400, 1600, 120, 1200, 1200)]
    [InlineData(1200, 600, 120, 600, 600)]
    [InlineData(320, 90, 90, 90, 90)]
    public async Task ResizeDock_UpdatesAccessibleBoundsWithoutRemountingTerminals(
        int requestedHeight, int viewportHeight, int minimum, int maximum, int expectedHeight)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<TerminalView>().Count));
        var terminals = cut.FindComponents<TerminalView>().Select(view => view.Instance).ToArray();

        await cut.InvokeAsync(() => cut.Instance.SetHeightAsync(requestedHeight, viewportHeight));
        var dock = cut.Find(".terminal-dock");
        var handle = cut.Find("[role=separator]");
        Assert.Equal($"height: {expectedHeight}px;", dock.GetAttribute("style"));
        Assert.Equal("0", handle.GetAttribute("tabindex"));
        Assert.Equal("horizontal", handle.GetAttribute("aria-orientation"));
        Assert.Equal("Terminals", handle.GetAttribute("aria-label"));
        Assert.Equal(dock.Id, handle.GetAttribute("aria-controls"));
        Assert.Equal(minimum.ToString(), handle.GetAttribute("aria-valuemin"));
        Assert.Equal(maximum.ToString(), handle.GetAttribute("aria-valuemax"));
        Assert.Equal(expectedHeight.ToString(), handle.GetAttribute("aria-valuenow"));
        Assert.Equal($"{expectedHeight} pixels high", handle.GetAttribute("aria-valuetext"));
        Assert.Equal("ArrowUp ArrowDown Shift+ArrowUp Shift+ArrowDown Home End", handle.GetAttribute("aria-keyshortcuts"));
        Assert.Equal("Use Up or Down to resize, Shift for larger steps, Home for minimum height, and End for maximum height.",
            cut.Find($"#{handle.GetAttribute("aria-describedby")}").TextContent);
        Assert.Equal(terminals, cut.FindComponents<TerminalView>().Select(view => view.Instance).ToArray());
        Assert.Equal("first", cut.Find("[role=tab][aria-selected=true]").TextContent.Trim());
        Assert.Empty(client.ClosedTerminals);
    }

    [Fact]
    public async Task ResizeDock_AfterDisposal_DoesNotUpdateState()
    {
        var client = new TestDashboardClient();
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        var height = cut.Find(".terminal-dock").GetAttribute("style");

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        await cut.InvokeAsync(() => cut.Instance.SetHeightAsync(500, 800));

        Assert.Equal(height, cut.Find(".terminal-dock").GetAttribute("style"));
        Assert.Equal(["registerResizeHandle", "unregisterResizeHandle"], JSInterop.Invocations
            .Where(invocation => invocation.Identifier is "registerResizeHandle" or "unregisterResizeHandle")
            .Select(invocation => invocation.Identifier));
    }

    [Fact]
    public async Task WatchUpdates_ReplaceSnapshotAndSelectAppHostTerminals()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();

        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        Assert.Equal("No terminals", cut.Find(".terminal-dock-panel-heading").TextContent);
        Assert.Equal(["Open terminal in a new window", "Hide terminal panel (Shift+`)"],
            cut.FindAll(".terminal-dock-tabstrip fluent-button").Select(button => button.GetAttribute("aria-label")));
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second"));
        cut.WaitForAssertion(() => Assert.Equal("first", cut.Find(".terminal-dock-tab.active").TextContent.Trim()));

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Added, "third"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".terminal-dock-tab").Count);
            Assert.Equal("first", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
            Assert.Empty(cut.FindAll(".terminal-dock-panel"));
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
        Assert.Equal(["registerTabNavigation", "unregisterTabNavigation"], JSInterop.Invocations
            .Where(invocation => invocation.Identifier is "registerTabNavigation" or "unregisterTabNavigation")
            .Select(invocation => invocation.Identifier));
        await Services.GetRequiredService<ShortcutManager>().OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock);
    }

    [Theory]
    [InlineData(false, "second", new[] { "first", "second" }, "second")]
    [InlineData(true, "second", new[] { "first", "second" }, "second")]
    [InlineData(false, "removed", new[] { "first" }, "first")]
    [InlineData(true, "removed", new[] { "first" }, "first")]
    [InlineData(false, "removed", new string[0], null)]
    [InlineData(true, "removed", new string[0], null)]
    public async Task RecoverySnapshot_RevealsDockWithoutResurrectingRemovedTerminals(
        bool previouslyOpened, string activatedId, string[] ids, string? selectedId)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        if (previouslyOpened)
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second", "removed"));
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[role=tab]").Count));
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
        }

        var recovery = TerminalSetupHelpers.Snapshot(ids);
        recovery.Snapshot.ActivatedTerminalId = activatedId;
        await updates.Writer.WriteAsync(recovery);
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find(".terminal-dock").HasAttribute("inert"));
            Assert.Empty(cut.FindAll(".terminal-dock.collapsed"));
            Assert.Equal(ids, cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
            Assert.Equal(ids.Length, cut.FindComponents<TerminalView>().Count);
            if (selectedId is null)
            {
                Assert.Equal("No terminals", cut.Find(".terminal-dock-panel-heading").TextContent);
            }
            else
            {
                Assert.Equal(selectedId, cut.Find("[role=tab][aria-selected=true]").TextContent.Trim());
            }
        });

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Added, "later"));
        cut.WaitForAssertion(() => Assert.Equal(ids.Append("later"), cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim())));
        Assert.Empty(client.ClosedTerminals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverySnapshot_WithoutActivationDoesNotRevealDock(bool previouslyOpened)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        if (previouslyOpened)
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
        }

        var renderCount = cut.RenderCount;
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("replacement"));
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.RenderCount > renderCount);
            if (previouslyOpened)
            {
                Assert.True(cut.Find(".terminal-dock.collapsed").HasAttribute("inert"));
                Assert.Equal("replacement", cut.Find("[role=tab][aria-selected=true]").TextContent.Trim());
            }
            else
            {
                Assert.Empty(cut.FindAll(".terminal-dock"));
            }
        });
    }

    [Fact]
    public async Task SelectTab_UpdatesAccessibleSelectionWithoutRemountingPanes()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second", "third"));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[role=tab]").Count));
        var terminals = cut.FindComponents<TerminalView>().Select(view => view.Instance).ToArray();
        Assert.Equal("Terminals", cut.Find("[role=tablist]").GetAttribute("aria-label"));

        foreach (var selected in new[] { 2, 0, 1 })
        {
            await cut.FindAll("[role=tab]")[selected].ClickAsync(new());
            var tabs = cut.FindAll("[role=tab]");
            var panes = cut.FindAll("[role=tabpanel]");
            var closeButtons = cut.FindAll(".terminal-dock-tab-close");
            for (var i = 0; i < tabs.Count; i++)
            {
                Assert.Equal(i == selected ? "0" : "-1", tabs[i].GetAttribute("tabindex"));
                Assert.Equal(i == selected ? "true" : "false", tabs[i].GetAttribute("aria-selected"));
                Assert.Equal(panes[i].Id, tabs[i].GetAttribute("aria-controls"));
                Assert.Equal(tabs[i].Id, panes[i].GetAttribute("aria-labelledby"));
                Assert.Equal(i != selected, panes[i].HasAttribute("inert"));
                Assert.Equal(i != selected ? "true" : "false", panes[i].GetAttribute("aria-hidden"));
                Assert.Equal(i == selected ? "0" : "-1", closeButtons[i].GetAttribute("tabindex"));
                Assert.Equal($"Close terminal '{tabs[i].TextContent.Trim()}'", closeButtons[i].GetAttribute("aria-label"));
                Assert.Equal("button", tabs[i].GetAttribute("type"));
            }

            Assert.Equal(terminals, cut.FindComponents<TerminalView>().Select(view => view.Instance).ToArray());
        }

        Assert.Equal(["initTerminal", "initTerminal", "initTerminal"], JSInterop.Invocations
            .Where(invocation => invocation.Identifier is "initTerminal" or "disposeTerminal" or "reconnectTerminal")
            .Select(invocation => invocation.Identifier));
        Assert.Empty(client.ClosedTerminals);
    }

    [Theory]
    [InlineData(0, "second", false)]
    [InlineData(1, "third", false)]
    [InlineData(2, "second", false)]
    [InlineData(0, "second", true)]
    [InlineData(1, "third", true)]
    [InlineData(2, "second", true)]
    public async Task CloseActiveTab_WaitsForWatchRemovalAndSelectsAdjacentTab(int selected, string next, bool useSnapshot)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(
            terminalChannelProvider: () => updates,
            closeTerminal: (_, _) => completion.Task);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        string[] ids = ["first", "second", "third"];
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot(ids));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[role=tab]").Count));
        await cut.FindAll("[role=tab]")[selected].ClickAsync(new());

        var close = cut.FindAll(".terminal-dock-tab-close")[selected].ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal([ids[selected]], client.ClosedTerminals.ToArray()));
        Assert.Equal(3, cut.FindAll("[role=tab]").Count);
        Assert.Equal(ids[selected], cut.Find("[role=tab][aria-selected=true]").TextContent.Trim());

        await updates.Writer.WriteAsync(useSnapshot
            ? TerminalSetupHelpers.Snapshot(ids.Where(id => id != ids[selected]).ToArray())
            : TerminalSetupHelpers.Change(TerminalChangeType.Removed, ids[selected]));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll("[role=tab]").Count);
            Assert.Equal(next, cut.Find("[role=tab][aria-selected=true]").TextContent.Trim());
            Assert.Equal("0", cut.Find("[role=tab][aria-selected=true]").GetAttribute("tabindex"));
        });
        completion.SetResult();
        await close.DefaultTimeout();
    }

    [Fact]
    public async Task LastTabRemoved_EmptyDockCanReceiveAnotherAppHostTerminal()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "first"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role=tab]")));

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Removed, "first"));
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[role=tablist]"));
            Assert.Empty(cut.FindAll("[role=tabpanel]"));
            Assert.Equal("No terminals", cut.Find(".terminal-dock-panel-heading").TextContent);
            Assert.Equal(["Open terminal in a new window", "Hide terminal panel (Shift+`)"],
                cut.FindAll(".terminal-dock-tabstrip fluent-button").Select(button => button.GetAttribute("aria-label")));
        });

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Added, "second"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("second", cut.Find("[role=tab][aria-selected=true]").TextContent.Trim());
            Assert.Empty(cut.FindAll(".terminal-dock-panel"));
        });
        Assert.Empty(client.ClosedTerminals);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HideDock_IsInertWithoutClosingOrRemountingTerminals(bool hideWithShortcut, bool reopenFromAppHost)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".terminal-dock-tab").Count));
        var terminals = cut.FindComponents<TerminalView>().Select(view => view.Instance).ToArray();
        var height = cut.Find(".terminal-dock").GetAttribute("style");
        Assert.False(cut.Find(".terminal-dock").HasAttribute("inert"));
        Assert.Equal("false", cut.Find(".terminal-dock").GetAttribute("aria-hidden"));

        if (hideWithShortcut)
        {
            await Services.GetRequiredService<ShortcutManager>().OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock);
        }
        else
        {
            await cut.Find(".terminal-dock-collapse").ClickAsync(new());
        }

        var collapsed = Assert.Single(cut.FindAll(".terminal-dock.collapsed"));
        Assert.True(collapsed.HasAttribute("inert"));
        Assert.Equal("true", collapsed.GetAttribute("aria-hidden"));
        Assert.Equal(height, collapsed.GetAttribute("style"));
        Assert.Equal(terminals, cut.FindComponents<TerminalView>().Select(view => view.Instance).ToArray());
        Assert.Empty(client.ClosedTerminals);
        Assert.Empty(Services.GetRequiredService<INotificationService>().GetNotifications());

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "first", "Updated while hidden"));
        cut.WaitForAssertion(() => Assert.Equal("Updated while hidden", cut.Find(".terminal-dock-tab-title").TextContent));
        Assert.True(cut.Find(".terminal-dock").HasAttribute("inert"));

        if (reopenFromAppHost)
        {
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "second"));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-dock.visible")));
        }
        else
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
        }

        var visible = Assert.Single(cut.FindAll(".terminal-dock.visible"));
        Assert.False(visible.HasAttribute("inert"));
        Assert.Equal("false", visible.GetAttribute("aria-hidden"));
        Assert.Equal(height, visible.GetAttribute("style"));
        Assert.Equal(2, cut.FindAll(".terminal-dock-tab").Count);
        Assert.Equal(terminals, cut.FindComponents<TerminalView>().Select(view => view.Instance).ToArray());
        Assert.Equal(["initTerminal", "initTerminal"], JSInterop.Invocations
            .Where(invocation => invocation.Identifier is "initTerminal" or "disposeTerminal" or "reconnectTerminal")
            .Select(invocation => invocation.Identifier));
        Assert.Empty(client.ClosedTerminals);
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
