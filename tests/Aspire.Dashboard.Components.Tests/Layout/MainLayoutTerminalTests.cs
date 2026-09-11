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
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public partial class MainLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalDock_RunSelection_OnlySubscribesWhileLive(bool startHistorical)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var subscriptionDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(terminalChannelProvider: () => updates)
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
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        var shortcuts = Services.GetRequiredService<ShortcutManager>();
        var label = Services.GetRequiredService<IStringLocalizer<Resources.Layout>>()[nameof(Resources.Layout.MainLayoutToggleTerminalDock)].Value;

        if (startHistorical)
        {
            Assert.Empty(cut.FindComponents<TerminalDock>());
            Assert.Empty(cut.FindAll($"fluent-button[aria-label='{label}']"));
            Assert.Equal(0, client.TerminalSubscriptionCount);
            await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock));
            await cut.InvokeAsync(() => cut.FindComponent<DashboardRunSelect>().Instance.SelectedRunIdChanged.InvokeAsync(null));
        }

        var originalDock = cut.FindComponent<TerminalDock>().Instance;
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
            Assert.Empty(cut.FindAll($"fluent-button[aria-label='{label}']"));
            Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
        });
        await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock));
        Assert.Empty(cut.FindComponents<TerminalDock>());

        // The next subscription receives only the new live snapshot; no terminal from the previous dock survives.
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("new"));
        await cut.InvokeAsync(() => cut.FindComponent<DashboardRunSelect>().Instance.SelectedRunIdChanged.InvokeAsync(null));
        var newDock = cut.FindComponent<TerminalDock>().Instance;
        Assert.NotSame(originalDock, newDock);
        await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("new", cut.Find(".terminal-dock-tab.active").TextContent.Trim());
            Assert.Equal(2, client.TerminalSubscriptionCount);
            Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
        });

        await cut.InvokeAsync(() => newDock.DisposeAsync().AsTask()).DefaultTimeout();
    }
}
