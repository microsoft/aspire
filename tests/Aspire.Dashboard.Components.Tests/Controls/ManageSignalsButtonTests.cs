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
    public void Render_ClearAllMenuItemUsesSize16Icon()
    {
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var selectedResource = new SelectViewModel<ResourceTypeDetails>
        {
            Id = ResourceTypeDetails.CreateSingleton("test-resource", "test-resource"),
            Name = "test-resource"
        };

        var cut = RenderComponent<ManageSignalsButton>(builder =>
        {
            builder.Add(p => p.SelectedResource, selectedResource);
            builder.Add(p => p.HandleClearSignal, _ => Task.CompletedTask);
        });

        var menu = cut.FindComponent<AspireMenu>();
        var clearAllMenuItem = Assert.Single(menu.Instance.Items, item => item.Id == "clear-menu-all");

        Assert.IsType<Icons.Regular.Size16.Broom>(clearAllMenuItem.Icon);
    }
}
