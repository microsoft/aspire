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
    [Fact]
    public void Recording_DoesNotMountTerminalAndShowsConsole()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell", autoFit: true)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            isReadOnly: true);
        Services.AddSingleton<IVolumePathLauncher, VolumePathLauncher>();
        Services.AddSingleton<IInstrumentUnitResolver, TestInstrumentUnitResolver>();
        Services.AddSingleton<ConsoleLogsManager>();
        TerminalsSetupHelpers.SetupResourcesPage(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/shell");

        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "shell");
            builder.CloseComponent();
        }));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#overview-console-title")));
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Empty(cut.FindAll(".overview-terminal-card"));
    }

    [Theory]
    [InlineData("?pane=tags#output")]
    [InlineData("?pane=tags&terminal=true#output")]
    public async Task OpenTerminal_UsesDedicatedRouteAndPreservesViewState(string viewState)
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
        var overview = "/resources/shell" + viewState;
        var terminalPage = "/terminals/resource/shell" + viewState;
        navigation.NavigateTo(overview);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "shell");
            builder.CloseComponent();
        }));
        var page = cut.FindComponent<ResourceOverview>();
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<TerminalView>().Instance.Embedded));
        Assert.Equal(navigation.ToAbsoluteUri(overview).AbsoluteUri, navigation.Uri);
        Assert.Collection(page.FindAll(".overview-terminal-card .overview-card-actions .overview-card-action"),
            documentation => Assert.Equal("https://aka.ms/aspire/dashboard-terminal", documentation.GetAttribute("href")),
            window => Assert.Equal("Open in new window", window.GetAttribute("aria-label")),
            openTerminal =>
            {
                Assert.Equal("Expand terminal", openTerminal.GetAttribute("aria-label"));
                Assert.Equal(terminalPage, openTerminal.GetAttribute("href"));
            });
        await cut.InvokeAsync(() => page.Instance.DisposeAsync().AsTask());
    }
}
