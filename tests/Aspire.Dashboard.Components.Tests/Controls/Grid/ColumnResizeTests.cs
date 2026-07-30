// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls.Grid;

public class ColumnResizeTests : DashboardTestContext
{
    [Fact]
    public void PropertyGrid_RendersResizeHandleAndImportsResizerModule()
    {
        // Arrange
        JSInterop.Mode = JSRuntimeMode.Loose;
        DashboardSetupHelpers.AddCommonDashboardServices(this);

        var items = new List<TestPropertyItem>
        {
            new("Name1", "Value1"),
            new("Name2", "Value2"),
        }.AsQueryable();

        // Act
        var cut = RenderComponent<PropertyGrid<TestPropertyItem>>(builder =>
        {
            builder.Add(p => p.Items, items);
        });

        // Assert
        var table = cut.Find("table");
        Assert.Contains("resizable-grid", table.ClassList);

        // Only the first (name) column is resizable; the trailing value column has nothing to resize
        // against, so there is exactly one handle.
        var handle = Assert.Single(cut.FindAll("[data-resize-handle]"));
        Assert.Equal("separator", handle.GetAttribute("role"));
        Assert.Equal("vertical", handle.GetAttribute("aria-orientation"));
        Assert.Equal("0", handle.GetAttribute("data-column-index"));
        Assert.False(string.IsNullOrEmpty(handle.GetAttribute("aria-label")));

        // The colocated resize module is imported to wire up the handle.
        Assert.Contains(JSInterop.Invocations, i =>
            i.Identifier == "import" &&
            i.Arguments.Count > 0 &&
            (i.Arguments[0] as string) == "./Components/Controls/Grid/ColumnResizer.razor.js");
    }

    [Fact]
    public void GridSortableHeader_NonSortable_OmitsSortSemantics()
    {
        // Arrange
        JSInterop.Mode = JSRuntimeMode.Loose;
        DashboardSetupHelpers.AddCommonDashboardServices(this);

        var items = new List<TestPropertyItem>
        {
            new("Name1", "Value1"),
        }.AsQueryable();

        // Act
        var cut = RenderComponent<PropertyGrid<TestPropertyItem>>(builder =>
        {
            builder.Add(p => p.Items, items);
            builder.Add(p => p.IsNameSortable, false);
            builder.Add(p => p.IsValueSortable, false);
        });

        // Assert
        // Non-sortable headers should not advertise a sort affordance, but the resizable name column
        // still renders its handle.
        var headers = cut.FindAll("thead th");
        Assert.All(headers, th => Assert.Null(th.GetAttribute("aria-sort")));
        Assert.Single(cut.FindAll("[data-resize-handle]"));
    }

    private sealed record TestPropertyItem(string Name, string? Value) : IPropertyGridItem;
}
