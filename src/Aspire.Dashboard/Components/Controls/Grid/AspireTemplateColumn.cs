// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.Grid;

public class AspireTemplateColumn<TGridItem> : TemplateColumn<TGridItem>, IAspireColumn
{
    [Parameter]
    public GridColumnManager? ColumnManager { get; set; }

    protected override void OnInitialized()
    {
        Tooltip = true;
    }

    // Suppress FluentDataGrid's native title; the shared provider anchors to the grid cell
    // using the metadata below, without adding a wrapper around the cell's content.
    protected override string? RawCellContent(TGridItem item) => null;

    protected override void CellContent(RenderTreeBuilder builder, TGridItem item)
    {
        if (Tooltip && base.RawCellContent(item) is { Length: > 0 } text)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "hidden", true);
            builder.AddAttribute(2, "data-tooltip-cell", text);
            builder.CloseElement();
        }

        builder.AddContent(3, (RenderFragment)(contentBuilder => base.CellContent(contentBuilder, item)));
    }

    protected override bool ShouldRender()
    {
        if (ColumnManager is not null && ColumnId is not null && !ColumnManager.IsColumnVisible(ColumnId))
        {
            return false;
        }

        return base.ShouldRender();
    }
}
