// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public partial class MainLayoutTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TerminalDock_RequiresResourceService(bool isEnabled, bool isDesktop)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var subscriptionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(
            isEnabled: isEnabled,
            terminalChannelProvider: () =>
            {
                subscriptionStarted.TrySetResult();
                return updates;
            },
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalSetupHelpers.SetupTerminalView(this);
        TerminalSetupHelpers.SetupTerminalDock(this);
        SetupMainLayoutServices(dashboardClient: client);

        var cut = RenderComponent<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        var label = Services.GetRequiredService<IStringLocalizer<Resources.TerminalStrings>>()[
            isDesktop ? nameof(Resources.TerminalStrings.MainLayoutToggleTerminalDock) : nameof(Resources.TerminalStrings.TerminalTitle)].Value;
        var shortcuts = Services.GetRequiredService<ShortcutManager>();
        var toggleSelector = isDesktop ? $"fluent-button[aria-label='{label}']" : $"fluent-menu-item[title='{label}']";
        if (!isDesktop)
        {
            await cut.InvokeAsync(() => cut.Find($"#{MainLayout.NavigationButtonId}").Click());
        }

        Assert.Equal(isEnabled ? 1 : 0, cut.FindComponents<TerminalDock>().Count);
        Assert.Equal(isEnabled ? 1 : 0, cut.FindAll(toggleSelector).Count);
        await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock));

        if (isEnabled)
        {
            // Subscription starts on a worker without triggering a render; a render-driven wait can miss it.
            await subscriptionStarted.Task.DefaultTimeout();
            Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
            Assert.Single(cut.FindAll(".terminal-dock"));
            var dock = cut.FindComponent<TerminalDock>().Instance;
            await cut.InvokeAsync(() => client.SetConnectionState(DashboardConnectionState.Disconnected));
            cut.Render();
            Assert.Same(dock, cut.FindComponent<TerminalDock>().Instance);
            Assert.Single(cut.FindAll(toggleSelector));
            await cut.InvokeAsync(() => dock.DisposeAsync().AsTask()).DefaultTimeout();
        }
        else
        {
            Assert.Empty(cut.FindAll(".terminal-dock"));
            Assert.False(subscriptionStarted.Task.IsCompleted);
            Assert.Equal(0, client.TerminalSubscriptionCount);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TerminalDock_RunSelection_OnlySubscribesWhileLive(bool startHistorical, bool isDesktop)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var subscriptionDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(
            isEnabled: true,
            terminalChannelProvider: () => updates,
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>())
        {
            OnTerminalSubscriptionDisposed = () => subscriptionDisposed.TrySetResult()
        };
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new("current", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, null, false, "TestApp", string.Empty, true),
            new("historical", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, "TestApp", string.Empty, false)
        ]);
        // Main layout setup renders the message bar provider, which freezes service registration.
        TerminalSetupHelpers.SetupTerminalView(this);
        TerminalSetupHelpers.SetupTerminalDock(this);
        SetupMainLayoutServices(dashboardRunStore: runStore, dashboardClient: client);
        var selection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        selection.OnSelectRun = runId => client.IsReadOnly = runId is not null;
        if (startHistorical)
        {
            selection.SelectRun("historical");
        }

        var cut = RenderComponent<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        var shortcuts = Services.GetRequiredService<ShortcutManager>();
        var label = Services.GetRequiredService<IStringLocalizer<Resources.TerminalStrings>>()[
            isDesktop ? nameof(Resources.TerminalStrings.MainLayoutToggleTerminalDock) : nameof(Resources.TerminalStrings.TerminalTitle)].Value;
        var toggleSelector = isDesktop ? $"fluent-button[aria-label='{label}']" : $"fluent-menu-item[title='{label}']";
        if (!isDesktop)
        {
            await cut.InvokeAsync(() => cut.Find($"#{MainLayout.NavigationButtonId}").Click());
        }

        if (startHistorical)
        {
            Assert.Empty(cut.FindComponents<TerminalDock>());
            Assert.Empty(cut.FindAll(toggleSelector));
            Assert.Equal(0, client.TerminalSubscriptionCount);
            await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock));
            await cut.InvokeAsync(() => cut.FindComponent<DashboardRunSelect>().Instance.SelectedRunIdChanged.InvokeAsync(null));
        }

        var originalDock = cut.FindComponent<TerminalDock>().Instance;
        Assert.Single(cut.FindAll(toggleSelector));
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "old"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("old", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
            Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
        });

        await cut.InvokeAsync(() => cut.FindComponent<DashboardRunSelect>().Instance.SelectedRunIdChanged.InvokeAsync("historical"));
        await subscriptionDisposed.Task.DefaultTimeout();
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindComponents<TerminalDock>());
            Assert.Empty(cut.FindAll(toggleSelector));
            Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
        });
        await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock));
        Assert.Empty(cut.FindComponents<TerminalDock>());

        // The next subscription receives only the new live snapshot; no terminal from the previous dock survives.
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("new"));
        await cut.InvokeAsync(() => cut.FindComponent<DashboardRunSelect>().Instance.SelectedRunIdChanged.InvokeAsync(null));
        var newDock = cut.FindComponent<TerminalDock>().Instance;
        Assert.NotSame(originalDock, newDock);
        Assert.Single(cut.FindAll(toggleSelector));
        await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("new", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
            Assert.Equal(2, client.TerminalSubscriptionCount);
            Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
        });

        await cut.InvokeAsync(() => newDock.DisposeAsync().AsTask()).DefaultTimeout();
    }

    [Fact]
    public async Task TerminalDock_MobileMenu_CanOpenCollapseAndReopenWithoutKeyboard()
    {
        var client = new TestDashboardClient(
            isEnabled: true,
            terminalChannelProvider: () => Channel.CreateUnbounded<WatchTerminalsUpdate>(),
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalSetupHelpers.SetupTerminalView(this);
        TerminalSetupHelpers.SetupTerminalDock(this);
        SetupMainLayoutServices(dashboardClient: client);

        var cut = RenderComponent<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        var label = Services.GetRequiredService<IStringLocalizer<Resources.TerminalStrings>>()[nameof(Resources.TerminalStrings.TerminalTitle)].Value;
        var dock = cut.FindComponent<TerminalDock>().Instance;
        Assert.Empty(cut.FindAll(".terminal-dock"));

        foreach (var visible in new[] { true, false, true })
        {
            await cut.InvokeAsync(() => cut.Find($"#{MainLayout.NavigationButtonId}").Click());
            var item = cut.Find($"fluent-menu-item[title='{label}']");
            Assert.Equal(label, item.TextContent.Trim());
            Assert.Null(item.GetAttribute("aria-current"));
            await cut.InvokeAsync(() => item.TriggerEvent("onmenuitemchange", new MenuItemEventArgs
            {
                Id = item.Id,
                Text = item.TextContent
            }));

            cut.WaitForAssertion(() =>
            {
                Assert.Empty(cut.FindAll(".mobile-nav-menu"));
                var panel = Assert.Single(cut.FindAll(".terminal-dock"));
                Assert.Equal(visible ? "false" : "true", panel.GetAttribute("aria-hidden"));
                Assert.Equal(!visible, panel.HasAttribute("inert"));
                Assert.Contains(visible ? "visible" : "collapsed", panel.ClassList);
                Assert.Same(dock, cut.FindComponent<TerminalDock>().Instance);
            });
        }

        await cut.InvokeAsync(() => dock.DisposeAsync().AsTask()).DefaultTimeout();
    }
}
