// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class TotalItemsFooter
{
    // Total item count can be set via the parameter or via method.
    // Required because the count is updated when the data grid data is refreshed.
    /// <summary>
    /// This parameter is required because this control could be added and removed from the page.
    /// When the control is re-added to the page it gets the count back via the parameter.
    /// </summary>
    [Parameter]
    public int TotalItemCount { get; set; }

    /// <summary>
    /// Gets or sets the number of items that can be displayed when it is less than <see cref="TotalItemCount"/>.
    /// </summary>
    [Parameter]
    public int? DisplayedItemCount { get; set; }

    [Parameter, EditorRequired]
    public required string SingularText { get; set; }

    [Parameter, EditorRequired]
    public required string PluralText { get; set; }

    /// <summary>
    /// Gets or sets the format used when <see cref="DisplayedItemCount"/> has a value. The displayed count is {0}, and the total count is {1}.
    /// </summary>
    [Parameter]
    public string? PartialText { get; set; }

    [Parameter]
    public string? PauseText { get; set; }

    /// <summary>
    /// Called when data grid data is refreshed. This sets the count explicitly and forces the control to re-render.
    /// </summary>
    public void UpdateDisplayedCount(int totalItemCount)
    {
        TotalItemCount = totalItemCount;
        StateHasChanged();
    }

    /// <summary>
    /// Updates the total and displayed item counts, then refreshes the control.
    /// </summary>
    /// <param name="totalItemCount">The item count to display.</param>
    /// <param name="displayedItemCount">The number of items that can be displayed, or <see langword="null"/> when all items can be displayed.</param>
    public void UpdateDisplayedCount(int totalItemCount, int? displayedItemCount)
    {
        TotalItemCount = totalItemCount;
        DisplayedItemCount = displayedItemCount;
        StateHasChanged();
    }
}
