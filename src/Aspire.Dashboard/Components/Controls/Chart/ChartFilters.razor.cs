// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ChartFilters
{
    [Parameter, EditorRequired]
    public required OtlpInstrumentType InstrumentType { get; set; }

    [Parameter, EditorRequired]
    public required bool ShowCount { get; set; }

    [Parameter]
    public EventCallback<bool> ShowCountChanged { get; set; }

    [Parameter, EditorRequired]
    public required ImmutableList<DimensionFilterViewModel> DimensionFilters { get; set; }

    [Parameter]
    public EventCallback<DimensionFilterViewModel> OnDimensionValuesChanged { get; set; }

    private Task OnShowCountChangedAsync(bool value) => ShowCountChanged.InvokeAsync(value);
}
