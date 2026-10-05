// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
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

    protected override bool ShouldRender()
    {
        if (ColumnManager is not null && ColumnId is not null && !ColumnManager.IsColumnVisible(ColumnId))
        {
            return false;
        }

        return base.ShouldRender();
    }
}
