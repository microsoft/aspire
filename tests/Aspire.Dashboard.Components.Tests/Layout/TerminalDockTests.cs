// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

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

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Removed, "second"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll(".terminal-dock-tab").Count);
            Assert.Equal("first", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
        });
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
