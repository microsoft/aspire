// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class GridTooltipTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CellTooltip_PreservesContentAndUpdatesDescription(bool propertyColumn)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);

        string? tooltip = "Full description";
        var cut = Render<AspireFluentDataGrid<string>>(parameters => parameters
            .Add(p => p.ItemsProvider, EnumerableGridItemsProvider.Create(() => new[] { "Displayed value" }))
            .Add(p => p.ChildContent, RenderColumn));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Displayed value", cut.Find("[role=gridcell]").TextContent);
            Assert.Equal(["Full description"], cut.FindAll("[data-tooltip-cell]").Select(e => e.GetAttribute("data-tooltip-cell")));
        });

        tooltip = "Updated description";
        cut.Render();
        cut.WaitForAssertion(() => Assert.Equal(
            ["Updated description"], cut.FindAll("[data-tooltip-cell]").Select(e => e.GetAttribute("data-tooltip-cell"))));

        tooltip = null;
        cut.Render();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Displayed value", cut.Find("[role=gridcell]").TextContent);
            Assert.Equal(Array.Empty<string>(), cut.FindAll("[data-tooltip-cell]").Select(e => e.GetAttribute("data-tooltip-cell")));
        });

        void RenderColumn(RenderTreeBuilder builder)
        {
            if (propertyColumn)
            {
                builder.OpenComponent<AspirePropertyColumn<string, string>>(0);
                builder.AddComponentParameter(1, nameof(AspirePropertyColumn<string, string>.Title), "Value");
                builder.AddComponentParameter(2, nameof(AspirePropertyColumn<string, string>.Property), (Expression<Func<string, string>>)(value => value));
                builder.AddComponentParameter(3, nameof(AspirePropertyColumn<string, string>.TooltipText), (Func<string, string?>)(_ => tooltip));
                builder.CloseComponent();
            }
            else
            {
                builder.OpenComponent<AspireTemplateColumn<string>>(4);
                builder.AddComponentParameter(5, nameof(AspireTemplateColumn<string>.Title), "Value");
                builder.AddComponentParameter(6, nameof(AspireTemplateColumn<string>.TooltipText), (Func<string, string?>)(_ => tooltip));
                builder.AddComponentParameter(7, nameof(AspireTemplateColumn<string>.ChildContent), (RenderFragment<string>)(value => contentBuilder => contentBuilder.AddContent(0, value)));
                builder.CloseComponent();
            }
        }
    }
}
