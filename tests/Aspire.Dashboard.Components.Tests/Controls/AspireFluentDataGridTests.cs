// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public sealed class AspireFluentDataGridTests : DashboardTestContext
{
    [Fact]
    public void Render_LoadingContent_UsesCompactCenteredLayout()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);

        var cut = RenderComponent<AspireFluentDataGrid<string>>(builder => builder
            .Add(component => component.Loading, true));

        var stack = cut.FindComponent<FluentStack>().Instance;
        Assert.Equal("8", stack.HorizontalGap);
        Assert.Equal(HorizontalAlignment.Center, stack.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Center, stack.VerticalAlignment);

        var spinner = cut.FindComponent<FluentSpinner>().Instance;
        Assert.Equal(SpinnerSize.Small, spinner.Size);
        Assert.Equal("Loading...", cut.Find("tr[row-state='loading-content']").TextContent.Trim());
    }
}