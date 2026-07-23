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
    public async Task TerminalResource_Live_Selected_RendersBothViews_DefaultsToTerminal()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1, state: KnownResourceState.Running);
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
        // the JS terminal or the LogViewer subscription.
        Assert.Single(cut.FindComponents<TerminalView>());
        Assert.Single(cut.FindComponents<LogViewer>());

        // The resource is live (Running), so its PTY is active — the page
        // defaults to the Terminal view because that's the surface the user
        // navigated to see.
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Terminal, instance.ActiveViewForTest);

        var terminalView = cut.FindComponents<TerminalView>()[0].Instance;
        Assert.Equal(terminalResource.DisplayName, terminalView.ResourceName);
        Assert.Equal(0, terminalView.ReplicaIndex);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task TerminalResource_NotLive_Selected_RendersBothViews_DefaultsToConsole()
    {
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        // A terminal-enabled resource that is still Waiting has no live PTY yet,
        // so pre-PTY hosting messages (WaitFor, startup failures) belong on the
        // Console view. The page must NOT flip to Terminal until the resource is
        // running.
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1, state: KnownResourceState.Waiting);
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

        // Both views mount regardless of state so a later Running transition
        // doesn't need to re-create the terminal, but the non-live resource
        // stays on Console.
        Assert.Single(cut.FindComponents<TerminalView>());
        Assert.Single(cut.FindComponents<LogViewer>());
        Assert.Equal(ConsoleLogs.ConsoleLogsView.Console, instance.ActiveViewForTest);

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

        // Switch to the Terminal view via the user picker so the log filter
        // is hidden (the filter only applies to LogViewer content, not to
        // a live PTY). Then verify that navigating to a non-terminal
        // resource restores the filter UI.
        await cut.InvokeAsync(() => instance.HandleViewChangedForTestAsync(nameof(ConsoleLogs.ConsoleLogsView.Terminal)));
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
    public async Task TerminalResource_ViewToggle_RenderedDisplayStylesMatchActiveView()
    {
        // The user-visible contract for the view flip is not the _activeView
        // enum but the `display: contents` / `display: none` pair on the two
        // wrapper divs in ConsoleLogs.razor. Lock down that mapping so a
        // future edit that inverts the ternary or swaps the wrappers can't
        // pass the enum-based tests while producing a broken UI.
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        // Use a non-live (Waiting) terminal resource so the page starts on the
        // Console view; a Running resource would default straight to Terminal
        // (its PTY is live) and we want to exercise the Console→Terminal flip.
        var terminalResource = CreateTerminalResource("terminal-resource", replicaIndex: 0, replicaCount: 1, state: KnownResourceState.Waiting);
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
    public void TerminalView_InitialRender_ReconnectsWhenResourceChangesDuringInitialization()
    {
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var initTerminal = module.Setup<int>("initTerminal", _ => true);
        var reconnectTerminal = module.Setup<int>("reconnectTerminal", _ => true);
        reconnectTerminal.SetResult(2);

        var cut = RenderComponent<TerminalView>(builder =>
        {
            builder.Add(p => p.ResourceName, "first-resource");
            builder.Add(p => p.ReplicaIndex, 0);
        });

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.ResourceName, "second-resource");
            builder.Add(p => p.ReplicaIndex, 1);
        });

        initTerminal.SetResult(1);

        cut.WaitForAssertion(() =>
        {
            var init = Assert.Single(initTerminal.Invocations);
            var reconnect = Assert.Single(reconnectTerminal.Invocations);
            var initUrl = Assert.IsType<string>(init.Arguments[1]);
            var reconnectUrl = Assert.IsType<string>(reconnect.Arguments[1]);

            Assert.Contains("resource=first-resource", initUrl);
            Assert.Contains("replica=0", initUrl);
            Assert.Equal(1, reconnect.Arguments[0]);
            Assert.Contains("resource=second-resource", reconnectUrl);
            Assert.Contains("replica=1", reconnectUrl);
        });
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
        module.Setup<int>("reconnectTerminal", _ => true).SetResult(2);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();
        module.SetupVoid("refreshLayout", _ => true).SetVoidResult();
        module.SetupVoid("refreshToolbarState", _ => true).SetVoidResult();
        module.Setup<TerminalSizePreset[]>("getSizePresets").SetResult([]);
    }

    private static ResourceViewModel CreateTerminalResource(string resourceName, int replicaIndex, int replicaCount, KnownResourceState state = KnownResourceState.Running)
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
            state: state,
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
