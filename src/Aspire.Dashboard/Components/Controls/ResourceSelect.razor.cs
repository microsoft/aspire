// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class ResourceSelect
{
    private const int ResourceOptionPixelHeight = 32;
    private const int MaxVisibleResourceOptions = 15;
    private const int SelectPadding = 8; // 4px top + 4px bottom

    private readonly string _selectId = $"resource-select-{Guid.NewGuid():N}";

    private bool _open;

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

    private void Toggle()
    {
        if (Resources is null)
        {
            return;
        }

        _open = !_open;
    }

    private void Close()
    {
        _open = false;
    }

    private async Task OnOptionClickedAsync(SelectViewModel<ResourceTypeDetails> option, bool disabled)
    {
        if (disabled)
        {
            return;
        }

        _open = false;
        await SetSelectedResourceAsync(option);
    }

    private async Task OnTriggerKeyDownAsync(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Enter":
            case " ":
            case "Spacebar":
                Toggle();
                break;
            case "Escape":
                _open = false;
                break;
            case "ArrowDown":
                await MoveSelectionAsync(1);
                break;
            case "ArrowUp":
                await MoveSelectionAsync(-1);
                break;
        }
    }

    // Move the selection to the next/previous enabled option, mirroring native <select> keyboard behavior.
    private async Task MoveSelectionAsync(int delta)
    {
        if (Resources is null)
        {
            return;
        }

        var options = Resources.ToList();
        if (options.Count == 0)
        {
            return;
        }

        var currentIndex = SelectedResource is null ? -1 : options.FindIndex(o => Equals(o, SelectedResource));
        var index = currentIndex;

        for (var i = 0; i < options.Count; i++)
        {
            index += delta;
            if (index < 0 || index >= options.Count)
            {
                return;
            }

            var candidate = options[index];
            var disabled = !CanSelectGrouping && candidate.Id?.Type is Otlp.Model.OtlpResourceType.ResourceGrouping;
            if (!disabled)
            {
                await SetSelectedResourceAsync(candidate);
                return;
            }
        }
    }

    private async Task SetSelectedResourceAsync(SelectViewModel<ResourceTypeDetails> resource)
    {
        SelectedResource = resource;
        await SelectedResourceChanged.InvokeAsync(resource);
    }

    private string? ListStyle()
    {
        if (Resources?.TryGetNonEnumeratedCount(out var count) is false or null)
        {
            return null;
        }

        if (count <= MaxVisibleResourceOptions)
        {
            return null;
        }

        var maxHeight = (ResourceOptionPixelHeight * MaxVisibleResourceOptions) + SelectPadding;
        return string.Create(CultureInfo.InvariantCulture, $"max-height: {maxHeight}px; overflow-y: auto;");
    }
}

