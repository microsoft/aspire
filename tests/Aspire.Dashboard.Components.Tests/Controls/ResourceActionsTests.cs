// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using DashboardResources = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ResourceActionsTests : DashboardTestContext
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public async Task OutputShortcut_PrefersLiveTerminalAndRetainsConsoleLogsInMenu(
        bool hasTerminal, bool enabled, bool readOnly, bool expectTerminal)
    {
        var resource = hasTerminal
            ? TerminalSetupHelpers.CreateTerminalResource("shell")
            : ModelTestHelpers.CreateResource("shell");
        var client = new TestDashboardClient(isEnabled: enabled, initialResources: [resource],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            isReadOnly: readOnly);
        var cut = RenderActions(resource, client, isDesktop: true);
        var outputButton = cut.FindComponent<FluentButton>();
        Assert.Equal(expectTerminal ? TerminalStrings.TerminalTitle : DashboardResources.ResourceActionConsoleLogsText,
            outputButton.Instance.Title);

        await cut.InvokeAsync(outputButton.Instance.OnClick.InvokeAsync);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var expectedPath = expectTerminal ? "/terminals/resource/shell" : "/consolelogs/resource/shell";
        Assert.Equal(navigation.ToAbsoluteUri(expectedPath).AbsoluteUri, navigation.Uri);

        var menuItems = cut.FindComponent<AspireMenuButton>().Instance.ItemsProvider();
        var expectedItems = new List<string> { ControlsStrings.ActionViewDetailsText };
        if (expectTerminal)
        {
            expectedItems.Add(TerminalStrings.TerminalTitle);
        }
        expectedItems.Add(DashboardResources.ResourceActionConsoleLogsText);
        expectedItems.Add(ControlsStrings.ViewJson);
        Assert.Equal(expectedItems, menuItems.Select(item => item.Text));

        var consoleLogs = Assert.Single(menuItems, item => item.Text == DashboardResources.ResourceActionConsoleLogsText);
        await cut.InvokeAsync(consoleLogs.OnClick!);
        Assert.Equal(navigation.ToAbsoluteUri("/consolelogs/resource/shell").AbsoluteUri, navigation.Uri);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutputShortcutAndMenu_PreserveReplicaSelection(bool multipleReplicas)
    {
        var resource = TerminalSetupHelpers.CreateTerminalResource("shell-abc123", displayName: "shell");
        var resources = new ConcurrentDictionary<string, ResourceViewModel>(StringComparers.ResourceName);
        resources[resource.Name] = resource;
        if (multipleReplicas)
        {
            var otherReplica = TerminalSetupHelpers.CreateTerminalResource("shell-def456", displayName: "shell");
            resources[otherReplica.Name] = otherReplica;
        }
        var client = new TestDashboardClient(isEnabled: true, initialResources: resources.Values.ToList(),
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        var cut = RenderActions(resource, client, isDesktop: true, resources);
        var expectedName = multipleReplicas ? "shell-abc123" : "shell";
        var navigation = Services.GetRequiredService<NavigationManager>();

        await cut.InvokeAsync(() => cut.FindComponent<FluentButton>().Instance.OnClick.InvokeAsync());
        Assert.Equal(navigation.ToAbsoluteUri($"/terminals/resource/{expectedName}").AbsoluteUri, navigation.Uri);

        var menuItems = cut.FindComponent<AspireMenuButton>().Instance.ItemsProvider();
        var consoleLogs = Assert.Single(menuItems, item => item.Text == DashboardResources.ResourceActionConsoleLogsText);
        await cut.InvokeAsync(consoleLogs.OnClick!);
        Assert.Equal(navigation.ToAbsoluteUri($"/consolelogs/resource/{expectedName}").AbsoluteUri, navigation.Uri);

        var terminal = Assert.Single(menuItems, item => item.Text == TerminalStrings.TerminalTitle);
        await cut.InvokeAsync(terminal.OnClick!);
        Assert.Equal(navigation.ToAbsoluteUri($"/terminals/resource/{expectedName}").AbsoluteUri, navigation.Uri);
    }

    [Fact]
    public void OutputShortcut_UpdatesWithTerminalCapability()
    {
        var resource = ModelTestHelpers.CreateResource("shell");
        var client = new TestDashboardClient(isEnabled: true, initialResources: [resource],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        var cut = RenderActions(resource, client, isDesktop: true);
        Assert.Equal(DashboardResources.ResourceActionConsoleLogsText, cut.FindComponent<FluentButton>().Instance.Title);

        cut.Render(builder => builder.Add(component => component.Resource, TerminalSetupHelpers.CreateTerminalResource("shell")));
        Assert.Equal(TerminalStrings.TerminalTitle, cut.FindComponent<FluentButton>().Instance.Title);

        cut.Render(builder => builder.Add(component => component.Resource, resource));
        Assert.Equal(DashboardResources.ResourceActionConsoleLogsText, cut.FindComponent<FluentButton>().Instance.Title);
    }

    [Fact]
    public async Task MobileMenu_OffersTerminalAndConsoleLogs()
    {
        var resource = TerminalSetupHelpers.CreateTerminalResource("shell");
        var client = new TestDashboardClient(isEnabled: true, initialResources: [resource],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        var cut = RenderActions(resource, client, isDesktop: false);
        Assert.Single(cut.FindComponents<FluentButton>());
        var menuItems = cut.FindComponent<AspireMenuButton>().Instance.ItemsProvider();
        Assert.Equal(
            new[] { ControlsStrings.ActionViewDetailsText, TerminalStrings.TerminalTitle, DashboardResources.ResourceActionConsoleLogsText, ControlsStrings.ViewJson },
            menuItems.Select(item => item.Text));

        var terminal = Assert.Single(menuItems, item => item.Text == TerminalStrings.TerminalTitle);
        await cut.InvokeAsync(terminal.OnClick!);
        var navigation = Services.GetRequiredService<NavigationManager>();
        Assert.Equal(navigation.ToAbsoluteUri("/terminals/resource/shell").AbsoluteUri, navigation.Uri);
    }

    private IRenderedComponent<ResourceActions> RenderActions(
        ResourceViewModel resource, TestDashboardClient client, bool isDesktop,
        ConcurrentDictionary<string, ResourceViewModel>? resources = null)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: !isDesktop);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, client);

        return Render<ResourceActions>(builder => builder
            .AddCascadingValue(viewport)
            .Add(component => component.Resource, resource)
            .Add(component => component.ResourceByName, resources ?? new ConcurrentDictionary<string, ResourceViewModel>(StringComparers.ResourceName))
            .Add(component => component.MaxHighlightedCount, 0)
            .Add(component => component.CommandSelected, EventCallback<CommandViewModel>.Empty)
            .Add(component => component.OnViewDetails, EventCallback<string?>.Empty)
            .Add(component => component.IsCommandExecuting, (_, _) => false));
    }
}
