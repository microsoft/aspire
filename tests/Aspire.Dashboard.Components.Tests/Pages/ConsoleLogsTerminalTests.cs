// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using Value = Google.Protobuf.WellKnownTypes.Value;

namespace Aspire.Dashboard.Components.Tests.Pages;

// Focused bUnit coverage for the central user-visible render branch in
// ConsoleLogs.razor: when the selected resource has WithTerminal() applied,
// the page must mount BOTH TerminalView and LogViewer (the two are toggled
// via the View dropdown; both stay mounted so neither tears down on flips).
// For non-terminal resources only LogViewer is mounted. The HasTerminal()
// predicate itself has unit coverage in ResourceViewModelExtensionsTerminalTests,
// but only a component-level test proves the page actually re-evaluates the
// flag on selection change and that the render branch wires the correct
// parameters through to TerminalView.
//
// We deliberately stop short of a full end-to-end Playwright test here.
// End-to-end coverage requires DCP terminal-host changes that have not yet
// landed in the repo (the production WebSocket path can't be exercised
// against the unmodified DCP shipped in main). Once those changes land we can
// add a Playwright scenario that exercises a real WithTerminal() AppHost
// through the dashboard UI. Until then this bUnit test locks down the
// render-branch contract.
public partial class ConsoleLogsTests
{
    [Fact]
    public async Task TerminalResource_Selected_RendersBothViews_DefaultsToConsole()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        // Page wires _selectedResourceHasTerminal in SubscribeAsync after the
        // selection update; wait for the dual-mount branch to take effect.
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // Both views are mounted concurrently for terminal-enabled resources
        // so the View dropdown can flip between them without tearing down
        // the JS terminal or the LogViewer subscription. The initial active
        // view is Console — that way any pre-PTY hosting messages (WaitFor)
        // are visible immediately.
        Assert.Single(cut.FindComponents<TerminalView>());
        Assert.Single(cut.FindComponents<LogViewer>());

        var terminalView = cut.FindComponents<TerminalView>()[0].Instance;
        Assert.Equal(terminalResource.DisplayName, terminalView.ResourceName);
        Assert.Equal(0, terminalView.ReplicaIndex);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task SwitchingFromTerminalToNonTerminalResource_TearsDownTerminalView()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var plainResource = ModelTestHelpers.CreateResource(resourceName: "plain-resource", state: KnownResourceState.Running);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource, plainResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // Sanity: terminal resource mounts both views.
        Assert.Single(cut.FindComponents<TerminalView>());
        Assert.Single(cut.FindComponents<LogViewer>());

        // Switch to the plain resource. Use the same ResourceSelect-driven
        // path as ResourceName_SubscribeOnLoadAndChange_* so we exercise the
        // production selection-changed pipeline, not a direct parameter set.
        navigationManager.LocationChanged += (sender, e) =>
        {
            cut.SetParametersAndRender(builder =>
            {
                builder.Add(m => m.ResourceName, "plain-resource");
            });
        };
        var resourceSelect = cut.FindComponent<ResourceSelect>();
        var innerSelect = resourceSelect.Find("fluent-select");
        innerSelect.Change("plain-resource");

        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == plainResource.Name);
        // For a non-terminal resource the TerminalView is not mounted at all
        // (only LogViewer is needed), so the dual-mount branch is skipped.
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count == 0);

        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Single(cut.FindComponents<LogViewer>());

        await Task.CompletedTask;
    }

    [Fact]
    public async Task SwitchingFromTerminalToNonTerminalResource_RestoresSearchFilter()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var plainResource = ModelTestHelpers.CreateResource(resourceName: "plain-resource", state: KnownResourceState.Running);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource, plainResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // Simulate PTY attach so the page auto-flips to the Terminal view,
        // where the log filter is hidden (the filter only applies to
        // LogViewer content, not to a live PTY).
        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);
        Assert.Empty(cut.FindComponents<FluentSearch>());

        navigationManager.LocationChanged += (sender, e) =>
        {
            cut.SetParametersAndRender(builder =>
            {
                builder.Add(m => m.ResourceName, "plain-resource");
            });
        };
        var resourceSelect = cut.FindComponent<ResourceSelect>();
        var innerSelect = resourceSelect.Find("fluent-select");
        innerSelect.Change("plain-resource");

        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == plainResource.Name);
        cut.WaitForState(() => cut.FindComponents<LogViewer>().Count > 0);

        consoleLogsChannel.Writer.TryWrite([
            new ResourceLogLine(1, "first log", IsErrorMessage: false),
            new ResourceLogLine(2, "filtered log", IsErrorMessage: false)
        ]);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".log-content").Count));

        var search = Assert.Single(cut.FindComponents<FluentSearch>());
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("filtered"));

        cut.WaitForAssertion(() =>
        {
            var logViewer = Assert.Single(cut.FindComponents<LogViewer>());
            Assert.Equal("filtered", logViewer.Instance.FilterText);

            var content = Assert.Single(cut.FindAll(".log-content"));
            Assert.Contains("filtered log", content.TextContent);
        });
    }

    [Fact]
    public async Task TerminalResource_PtyAttaches_AutoSwitchesToTerminalView()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // Starting state: page is on Console even though the resource has a
        // terminal — the PTY hasn't attached yet, so pre-PTY hosting messages
        // stay visible.
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Console, instance.ActiveViewForTest);

        // Simulate the JS terminal pushing a toolbar snapshot once the PTY
        // attaches (status moves off "connecting"). The page should auto-flip
        // to the Terminal view because the user hasn't manually picked one.
        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Terminal, instance.ActiveViewForTest);
    }

    [Fact]
    public async Task TerminalResource_UserPicksConsole_PtyAttachDoesNotOverride()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // User explicitly latches on Console. Even though they were already
        // viewing Console, the explicit pick must latch _userPickedView so
        // subsequent auto-switch attempts are ignored.
        await cut.InvokeAsync(() => instance.HandleViewChangedForTestAsync(nameof(ConsoleLogs.ConsoleLogsView.Console)));

        // PTY attaches.
        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));

        // Page must stay on Console because the user already picked it.
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Console, instance.ActiveViewForTest);
    }

    [Fact]
    public async Task TerminalResource_PtyExits_AutoSwitchesBackToConsole()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // PTY attaches → page flips to Terminal.
        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);

        // PTY exits → page flips back to Console so the final log lines and
        // hosting exit messages are visible.
        await cut.InvokeAsync(() => terminalView.OnTerminalExited(terminalId: 1, generation: 1, exitCode: 0));

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Console);
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Console, instance.ActiveViewForTest);
    }

    [Fact]
    public async Task TerminalResource_PtyExitsThenReattaches_AutoSwitchesBackToTerminal()
    {
        // Regression for the "stop → start" cycle in a WithTerminal resource:
        // after the PTY exits and the user restarts the resource, the WS
        // reconnects and a fresh "connecting → connected" edge fires. The
        // page should follow that edge back to the Terminal view, matching
        // the behaviour on the very first attach.
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        var terminalView = cut.FindComponent<TerminalView>().Instance;

        // Initial PTY attach.
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);

        // Resource stops → PTY exits → flip back to Console.
        await cut.InvokeAsync(() => terminalView.OnTerminalExited(terminalId: 1, generation: 1, exitCode: 0));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Console);

        // Resource restarts: the terminal host process is recreated, so the
        // consumer WS goes through `connecting` again before the new PTY
        // reports primary. The JS-side terminal id stays the same — the
        // same Hmp1Client instance reconnects under the existing id.
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "connecting",
            Connected = false,
            IsPrimary = false,
            FontPx = 14,
        }));
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Terminal, instance.ActiveViewForTest);
    }

    [Fact]
    public async Task TerminalResource_ManuallyStopped_AutoSwitchesBackToConsole()
    {
        // Manual Stop from the dashboard kills the producer process directly;
        // the producer never sends an HMP1 Exit frame, so client.onExit on
        // the JS side never fires. The ConsoleLogs page must therefore react
        // to the resource snapshot state transitioning into a stopped
        // KnownResourceState (Exited / Finished / FailedToStart) and flip
        // the view back to Console so the user sees the resource's stop
        // log lines.
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // PTY attaches → page flips to Terminal.
        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);

        // Resource transitions to Exited (manual stop) — push a fresh snapshot
        // with the same name/properties but a stopped KnownResourceState.
        var stoppedTerminalResource = ModelTestHelpers.CreateResource(
            resourceName: "terminal-resource",
            state: KnownResourceState.Exited,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
                [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, "0"),
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "1"),
            });

        resourceChannel.Writer.TryWrite([
            new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, stoppedTerminalResource)
        ]);

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Console);
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Console, instance.ActiveViewForTest);
    }

    [Fact]
    public async Task TerminalResource_ManuallyStopped_StaleToolbarSnapshotDoesNotFlipBack()
    {
        // Regression: after manual Stop the JS side may still have an in-
        // flight toolbar snapshot reporting `primary` (the WS close has
        // not propagated yet, or the snapshot was queued before close).
        // Without any protection that stale `primary` snapshot would look
        // like a fresh attach edge and immediately snap the user back to
        // Terminal. The _selectedTerminalResourceStopped gate suppresses
        // the auto-switch while the resource is in a stopped state, so
        // stale snapshots that arrive after Stop are ignored until the
        // resource actually transitions back out of a stopped state.
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // PTY attaches → page flips to Terminal.
        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);

        // Resource manually stopped.
        var stoppedTerminalResource = ModelTestHelpers.CreateResource(
            resourceName: "terminal-resource",
            state: KnownResourceState.Exited,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
                [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, "0"),
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "1"),
            });

        resourceChannel.Writer.TryWrite([
            new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, stoppedTerminalResource)
        ]);
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Console);

        // Now a stale toolbar push from the JS side arrives reporting the
        // pre-stop primary state. With the gate in place this must NOT
        // flip the view back to Terminal.
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));

        Assert.Equal(ConsoleLogs.ConsoleLogsView.Console, instance.ActiveViewForTest);
    }

    [Fact]
    public async Task TerminalResource_ViewToggle_RenderedDisplayStylesMatchActiveView()
    {
        // The user-visible contract for the view flip is not the _activeView
        // enum but the `display: contents` / `display: none` pair on the two
        // wrapper divs in ConsoleLogs.razor. Lock down that mapping so a
        // future edit that inverts the ternary or swaps the wrappers can't
        // pass the enum-based tests while producing a broken UI.
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // Initial state: page defaults to Console. The Console wrapper (the
        // one containing LogViewer) must be visible; the Terminal wrapper
        // must be hidden. Locate each wrapper by descending from the
        // component's rendered element.
        var (consoleWrapper, terminalWrapper) = FindViewWrappers(cut);
        Assert.Contains("display: contents", consoleWrapper.GetAttribute("style"));
        Assert.Contains("display: none", terminalWrapper.GetAttribute("style"));

        // Flip to Terminal via the user-picked latch path and re-query the
        // wrappers — the render pass must swap the two `display` values.
        await cut.InvokeAsync(() => instance.HandleViewChangedForTestAsync(nameof(ConsoleLogs.ConsoleLogsView.Terminal)));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);

        (consoleWrapper, terminalWrapper) = FindViewWrappers(cut);
        Assert.Contains("display: none", consoleWrapper.GetAttribute("style"));
        Assert.Contains("display: contents", terminalWrapper.GetAttribute("style"));
    }

    [Fact]
    public async Task TerminalResource_CleanExit_StaleToolbarSnapshotDoesNotFlipBack()
    {
        // Regression: after a clean PTY exit the JS side may still have an
        // in-flight toolbar snapshot queued before onExit propagated (a
        // `primary`/`viewer` snapshot). Without a gate that stale snapshot
        // would look like a fresh attach edge and immediately snap the
        // user back to Terminal after we just flipped to Console. The
        // _terminalExitedAwaitingReattach gate suppresses the auto-switch
        // until a real WebSocket reopen produces a genuine `connecting`
        // snapshot (which only fires on a fresh consumer session).
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        // PTY attaches → page flips to Terminal.
        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);

        // Clean exit from the JS side (user typed `exit`) — page flips back
        // to Console.
        await cut.InvokeAsync(() => terminalView.OnTerminalExited(1, 1, 0));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Console);

        // Stale in-flight primary snapshot lands after onExit. Without the
        // reattach gate this would look like a fresh attach edge; with the
        // gate it must be ignored.
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));

        Assert.Equal(ConsoleLogs.ConsoleLogsView.Console, instance.ActiveViewForTest);
    }

    [Fact]
    public async Task TerminalResource_CleanExit_GenuineReconnectAutoSwitchesBackToTerminal()
    {
        // Companion to the stale-snapshot test: a real WebSocket reopen goes
        // through "connecting" before the next non-connecting snapshot. That
        // "connecting" observation clears the reattach gate, so the
        // subsequent primary snapshot is a genuine attach edge and the auto-
        // switch to Terminal fires again — matching the initial-connect
        // behaviour.
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: _ => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [terminalResource]);

        SetupConsoleLogsServices(dashboardClient);
        SetupTerminalViewJsInterop();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "terminal-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "terminal-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == terminalResource.Name);
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        var terminalView = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);

        await cut.InvokeAsync(() => terminalView.OnTerminalExited(1, 1, 0));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Console);

        // Genuine reconnect: WS reopens, JS emits "connecting" first. That
        // clears the reattach gate. (In production a fresh initTerminal
        // would allocate a new TerminalId; the bUnit JS stub returns the
        // same id both times, so we reuse id 1 which matches what the
        // TerminalView cached at init and thus passes its stale-id guard.)
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "connecting",
            Connected = false,
            IsPrimary = false,
            FontPx = 14,
        }));

        // Next snapshot is the fresh attach — must auto-switch back to
        // Terminal.
        await cut.InvokeAsync(() => terminalView.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Status = "primary",
            Connected = true,
            IsPrimary = true,
            FontPx = 14,
        }));

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogs.ConsoleLogsView.Terminal);
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Terminal, instance.ActiveViewForTest);
    }

    private static (AngleSharp.Dom.IElement Console, AngleSharp.Dom.IElement Terminal) FindViewWrappers(
        Bunit.IRenderedComponent<Components.Pages.ConsoleLogs> cut)
    {
        // LogViewer renders <div class="log-overflow ..."> as its root and
        // TerminalView renders <div class="terminal-container">. The two
        // Console/Terminal wrappers are the enclosing divs that carry the
        // `display: contents;` / `display: none;` inline style bound to
        // _activeView. Walk up from each component root until we hit that
        // wrapper — the raw inline style is the user-visible flip contract.
        var logRoot = cut.Find(".log-overflow");
        var terminalRoot = cut.Find(".terminal-container");

        var consoleWrapper = FindWrapperWithDisplayStyle(logRoot);
        var terminalWrapper = FindWrapperWithDisplayStyle(terminalRoot);

        Assert.NotNull(consoleWrapper);
        Assert.NotNull(terminalWrapper);
        return (consoleWrapper!, terminalWrapper!);
    }

    private static AngleSharp.Dom.IElement? FindWrapperWithDisplayStyle(AngleSharp.Dom.IElement start)
    {
        var current = start.ParentElement;
        while (current is not null)
        {
            var style = current.GetAttribute("style") ?? string.Empty;
            if (current.TagName.Equals("DIV", StringComparison.OrdinalIgnoreCase) &&
                style.Contains("display:", StringComparison.Ordinal))
            {
                return current;
            }
            current = current.ParentElement;
        }
        return null;
    }

    private void SetupTerminalViewJsInterop()
    {
        // TerminalView.OnAfterRenderAsync does:
        //   import("/Components/Controls/TerminalView.razor.js")  ->  module
        //   module.initTerminal(elementRef, wsUrl, dotNetRef)     ->  int terminalId
        // Both calls must be matched or bUnit's strict JSInterop throws and
        // the renderer reports an unhandled exception, preventing the test from
        // reaching its assertions. The stubs return harmless defaults — the
        // assertions in these tests are about render-branch selection, not
        // about runtime terminal behaviour.
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();
        module.SetupVoid("refreshLayout", _ => true).SetVoidResult();
        module.SetupVoid("refreshToolbarState", _ => true).SetVoidResult();
        module.Setup<TerminalSizePreset[]>("getSizePresets").SetResult([]);
    }

    private static ResourceViewModel CreateTerminalResource(string resourceName, int replicaIndex, int replicaCount)
    {
        // WithTerminal() stamps these three properties onto the resource
        // snapshot in DashboardServiceData.cs (covered by
        // DashboardServiceDataTerminalTests). The dashboard's HasTerminal()
        // and TryGetTerminalReplicaInfo() helpers both read from this shape,
        // so this mirrors the production wire contract.
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
            [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, replicaIndex.ToString()),
            [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, replicaCount.ToString()),
        };

        return ModelTestHelpers.CreateResource(
            resourceName: resourceName,
            state: KnownResourceState.Running,
            properties: properties);
    }

    private static ResourcePropertyViewModel StringProperty(string name, string value)
    {
        return new ResourcePropertyViewModel(
            name,
            new Value { StringValue = value },
            isValueSensitive: false,
            knownProperty: null,
            sortOrder: 0,
            displayName: null,
            isHighlighted: false);
    }
}
