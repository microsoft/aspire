// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class SpanTypeSelect : ComponentBase
{
    [Parameter, EditorRequired]
    public required List<SelectViewModel<SpanType>> SpanTypes { get; set; }

    [Parameter, EditorRequired]
    public required SelectViewModel<SpanType> SpanType { get; set; }

    [Parameter]
    public EventCallback<SelectViewModel<SpanType>> SpanTypeChanged { get; set; }

    [Parameter, EditorRequired]
    public required Func<Task> HandleSelectedSpanTypeChangedAsync { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Inject]
    public required IStringLocalizer<Resources.Traces> Loc { get; init; }

    // Created once. A new id on each render makes the page's frequent re-renders replace the select, which closes it while
    // the user is choosing a span type.
    private readonly string _spanTypeSelectId = $"span-type-select-{Guid.NewGuid():N}";

    private async Task HandleSelectedSpanTypeChangedInternalAsync()
    {
        await SpanTypeChanged.InvokeAsync(SpanType);
        await HandleSelectedSpanTypeChangedAsync();
    }
}

