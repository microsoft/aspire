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
        Assert.Equal(ConsoleLogsView.Console, instance.ActiveViewForTest);

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

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogsView.Terminal);
        Assert.Equal(ConsoleLogsView.Terminal, instance.ActiveViewForTest);
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
        await cut.InvokeAsync(() => instance.HandleViewChangedForTestAsync(nameof(ConsoleLogsView.Console)));

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
        Assert.Equal(ConsoleLogsView.Console, instance.ActiveViewForTest);
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
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogsView.Terminal);

        // PTY exits → page flips back to Console so the final log lines and
        // hosting exit messages are visible.
        await cut.InvokeAsync(() => terminalView.OnTerminalExited(terminalId: 1, exitCode: 0));

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogsView.Console);
        Assert.Equal(ConsoleLogsView.Console, instance.ActiveViewForTest);
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
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogsView.Terminal);

        // Resource stops → PTY exits → flip back to Console.
        await cut.InvokeAsync(() => terminalView.OnTerminalExited(terminalId: 1, exitCode: 0));
        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogsView.Console);

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

        cut.WaitForState(() => instance.ActiveViewForTest == ConsoleLogsView.Terminal);
        Assert.Equal(ConsoleLogsView.Terminal, instance.ActiveViewForTest);
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
