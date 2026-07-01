// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class ManageSignalsButtonTests : DashboardTestContext
{
    [Fact]
    public void Render_WithDownloadLogs_UsesDownloadLeadingIcon()
    {
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = RenderComponent<ManageSignalsButton>(builder =>
        {
            builder.Add(p => p.SelectedResource, CreateSelectedResource());
            builder.Add(p => p.HandleClearSignal, _ => Task.CompletedTask);
            builder.Add(p => p.HandleDownloadLogs, () => Task.CompletedTask);
        });

        var menuButton = cut.FindComponent<AspireMenuButton>();

        Assert.IsType<Icons.Regular.Size16.ArrowDownload>(menuButton.Instance.IconStart);
    }

    [Fact]
    public void Render_WithoutDownloadLogs_UsesClearLeadingIcon()
    {
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = RenderComponent<ManageSignalsButton>(builder =>
        {
            builder.Add(p => p.SelectedResource, CreateSelectedResource());
            builder.Add(p => p.HandleClearSignal, _ => Task.CompletedTask);
        });

        var menuButton = cut.FindComponent<AspireMenuButton>();

        Assert.IsType<Icons.Regular.Size16.Broom>(menuButton.Instance.IconStart);
    }

    [Fact]
    public void Render_ClearAllMenuItemUsesSize16Icon()
    {
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = RenderComponent<ManageSignalsButton>(builder =>
        {
            builder.Add(p => p.SelectedResource, CreateSelectedResource());
            builder.Add(p => p.HandleClearSignal, _ => Task.CompletedTask);
        });

        var menu = cut.FindComponent<AspireMenu>();
        var clearAllMenuItem = Assert.Single(menu.Instance.Items, item => item.Id == "clear-menu-all");

        Assert.IsType<Icons.Regular.Size16.Broom>(clearAllMenuItem.Icon);
    }

    private static SelectViewModel<ResourceTypeDetails> CreateSelectedResource()
    {
        return new()
        {
            Id = ResourceTypeDetails.CreateSingleton("test-resource", "test-resource"),
            Name = "test-resource"
        };
    }
}
