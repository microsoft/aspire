// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Grid;

[CascadingTypeParameter(nameof(TGridItem))]
public class AspireFluentDataGrid<TGridItem> : FluentDataGrid<TGridItem>
{
    public AspireFluentDataGrid(LibraryConfiguration configuration) : base(configuration)
    {
        LoadingContent = RenderLoadingContent;
    }

    [Inject]
    public required IStringLocalizer<ControlsStrings> Loc { get; init; }

    /// <summary>
    /// Refreshes virtualized data and renders this grid when the refresh originates outside a Blazor event.
    /// </summary>
    public async Task RefreshDataAndRenderAsync()
    {
        await RefreshDataAsync(force: true);
        StateHasChanged();
    }

    private void RenderLoadingContent(RenderTreeBuilder builder)
    {
        builder.OpenComponent<FluentStack>(0);
        builder.AddComponentParameter(1, nameof(FluentStack.HorizontalGap), "8");
        builder.AddComponentParameter(2, nameof(FluentStack.HorizontalAlignment), HorizontalAlignment.Center);
        builder.AddComponentParameter(3, nameof(FluentStack.VerticalAlignment), VerticalAlignment.Center);
        builder.AddAttribute(4, nameof(FluentStack.ChildContent), (RenderFragment)(contentBuilder =>
        {
            contentBuilder.OpenComponent<FluentSpinner>(5);
            contentBuilder.AddComponentParameter(6, nameof(FluentSpinner.Size), SpinnerSize.Small);
            contentBuilder.CloseComponent();
            contentBuilder.OpenElement(7, "div");
            contentBuilder.AddContent(8, Loc[nameof(ControlsStrings.Loading)]);
            contentBuilder.CloseElement();
        }));
        builder.CloseComponent();
    }
}
