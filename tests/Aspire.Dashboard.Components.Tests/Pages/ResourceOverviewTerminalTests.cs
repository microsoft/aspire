// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewTerminalTests : DashboardTestContext
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FocusAndMinimize_PreserveResourcePaneCardsAndTerminalConnection(bool initiallyFocused)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell", autoFit: true)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        Services.AddSingleton<IVolumePathLauncher, VolumePathLauncher>();
        Services.AddSingleton<IInstrumentUnitResolver, TestInstrumentUnitResolver>();
        Services.AddSingleton<ConsoleLogsManager>();
        TerminalsSetupHelpers.SetupResourcesPage(this, viewport, client);
        var navigation = Services.GetRequiredService<NavigationManager>();
        const string overview = "/resources/shell?pane=tags#output";
        const string focused = "/resources/shell?pane=tags&terminal=true#output";
        navigation.NavigateTo(initiallyFocused ? focused : overview);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "shell");
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<TerminalView>()));
        var page = cut.FindComponent<ResourceOverview>();
        var viewer = cut.FindComponent<TerminalView>().Instance;
        var cards = page.FindAll(".overview-card").Count;
        Assert.True(cards > 1);
        Assert.Equal(initiallyFocused, page.Find(".resource-overview").ClassList.Contains("terminal-focused"));
        var documentation = page.Find(".overview-terminal-card a.overview-card-action");
        Assert.Equal("https://aka.ms/aspire/dashboard-terminal", documentation.GetAttribute("href"));
        Assert.Equal("_blank", documentation.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", documentation.GetAttribute("rel"));
        Assert.Equal("More information", documentation.GetAttribute("aria-label"));
        Assert.Equal("More information", documentation.GetAttribute("data-tooltip"));
        var terminalActionLabel = initiallyFocused ? "Minimize terminal" : "Expand terminal";
        var terminalAction = page.Find(".overview-terminal-card .overview-card-action:not([href])");
        Assert.Equal(terminalActionLabel, terminalAction.GetAttribute("aria-label"));
        Assert.Equal(terminalActionLabel, terminalAction.GetAttribute("data-tooltip"));

        if (!initiallyFocused)
        {
            page.Find(".overview-terminal-card .overview-card-action:not([href])").Click();
            Assert.Equal("http://localhost" + focused, navigation.Uri);
            cut.Render();
        }

        Assert.Contains("terminal-focused", page.Find(".resource-overview").ClassList);
        Assert.Equal(ResourceTab.Terminal, cut.Instance.CurrentTab);
        var minimize = page.Find(".overview-terminal-card .overview-card-action:not([href])");
        Assert.Equal("Minimize terminal", minimize.GetAttribute("aria-label"));
        Assert.Equal("Minimize terminal", minimize.GetAttribute("data-tooltip"));
        Assert.Equal(2, page.FindAll(".overview-terminal-card .overview-card-actions .overview-card-action").Count);
        minimize.Click();
        Assert.Equal("http://localhost" + overview, navigation.Uri);
        cut.Render();

        Assert.False(page.Find(".resource-overview").ClassList.Contains("terminal-focused"));
        Assert.Equal(ResourceTab.Overview, cut.Instance.CurrentTab);
        Assert.Equal(["shell"], cut.Instance.SelectedResourceNames);
        Assert.Equal(cards, page.FindAll(".overview-card").Count);
        Assert.Equal("https://aka.ms/aspire/dashboard-terminal",
            page.Find(".overview-terminal-card .overview-card-actions [href]").GetAttribute("href"));
        Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
        Assert.Single(JSInterop.Invocations, i => i.Identifier == "initTerminal");
        Assert.Empty(client.ClosedTerminals);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "refreshLayout");

        // Router parameter updates on browser Back/Forward restore focus without replacing the viewer.
        navigation.NavigateTo(focused);
        cut.Render();
        Assert.Contains("terminal-focused", page.Find(".resource-overview").ClassList);
        Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
        await cut.InvokeAsync(() => page.Instance.DisposeAsync().AsTask());
    }
}
