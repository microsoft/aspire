// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
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
// the page must mount TerminalView instead of LogViewer (and restore LogViewer
// when switching back to a non-terminal resource). The HasTerminal()
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
    public async Task TerminalResource_Selected_RendersTerminalView_NotLogViewer()
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
        // selection update; wait for that to flip true before asserting the
        // rendered tree, otherwise we can race the initial render.
        cut.WaitForState(() => cut.FindComponents<TerminalView>().Count > 0);

        var terminalViews = cut.FindComponents<TerminalView>();
        var logViewers = cut.FindComponents<LogViewer>();

        Assert.Single(terminalViews);
        Assert.Empty(logViewers);

        var terminalView = terminalViews[0].Instance;
        Assert.Equal(terminalResource.DisplayName, terminalView.ResourceName);
        Assert.Equal(0, terminalView.ReplicaIndex);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task SwitchingFromTerminalToNonTerminalResource_RestoresLogViewer()
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

        // Sanity: starting state is TerminalView, no LogViewer.
        Assert.Single(cut.FindComponents<TerminalView>());
        Assert.Empty(cut.FindComponents<LogViewer>());

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
        // LogViewer should be restored and TerminalView torn down.
        cut.WaitForState(() => cut.FindComponents<LogViewer>().Count > 0);

        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Single(cut.FindComponents<LogViewer>());

        await Task.CompletedTask;
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
