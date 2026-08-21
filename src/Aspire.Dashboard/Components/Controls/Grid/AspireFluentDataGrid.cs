// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.Grid;

[CascadingTypeParameter(nameof(TGridItem))]
public class AspireFluentDataGrid<TGridItem>(LibraryConfiguration configuration) : FluentDataGrid<TGridItem>(configuration)
{
    /// <summary>
    /// Refreshes virtualized data and renders this grid when the refresh originates outside a Blazor event.
    /// </summary>
    public async Task RefreshDataAndRenderAsync()
    {
        await RefreshDataAsync(force: true);
        StateHasChanged();
    }
}