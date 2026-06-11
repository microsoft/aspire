// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class ResourceSelect
{
    private const int ResourceOptionPixelHeight = 32;
    private const int MaxVisibleResourceOptions = 15;
    private const int SelectPadding = 8; // 4px top + 4px bottom

    private readonly string _selectId = $"resource-select-{Guid.NewGuid():N}";

    [Parameter]
    public IEnumerable<SelectViewModel<ResourceTypeDetails>>? Resources { get; set; }

    [Parameter]
    public SelectViewModel<ResourceTypeDetails>? SelectedResource { get; set; }

    [Parameter]
    public EventCallback<SelectViewModel<ResourceTypeDetails>> SelectedResourceChanged { get; set; }

    [Parameter]
    public string? AriaLabel { get; set; }

    [Parameter]
    public bool CanSelectGrouping { get; set; }

    [Parameter]
    public string? LabelClass { get; set; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> Loc { get; init; }

    private Task SelectedResourceChangedCore()
    {
        return InvokeAsync(async () =>
        {
            if (SelectedResource is { } resource)
            {
                await SelectedResourceChanged.InvokeAsync(resource);
            }
        });
    }

    private static void ValuedChanged(string? value)
    {
        // Do nothing. Required for bunit change to trigger SelectedOptionChanged.
    }

    private string? GetPopupHeight()
    {
        if (Resources?.TryGetNonEnumeratedCount(out var count) is false or null)
        {
            return null;
        }

        if (count <= MaxVisibleResourceOptions)
        {
            return null;
        }

        return $"{(ResourceOptionPixelHeight * MaxVisibleResourceOptions) + SelectPadding}px";
    }
}
