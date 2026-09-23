// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ChartFilters
{
    private readonly Guid _idSuffix = Guid.NewGuid();
    private readonly HashSet<string> _openFilterNames = [];

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

    private string GetFilterButtonId(DimensionFilterViewModel filter) => $"typeFilterButton-{filter.SanitizedHtmlId}-{filter.NameHash}-{_idSuffix}";

    private bool IsPopupOpen(DimensionFilterViewModel filter) => _openFilterNames.Contains(filter.Name);

    private void TogglePopup(DimensionFilterViewModel filter) => SetPopupOpen(filter, !IsPopupOpen(filter));

    private void ShowPopup(DimensionFilterViewModel filter) => SetPopupOpen(filter, opened: true);

    private void SetPopupOpen(DimensionFilterViewModel filter, bool opened)
    {
        if (opened)
        {
            _openFilterNames.Add(filter.Name);
        }
        else
        {
            _openFilterNames.Remove(filter.Name);
        }
    }

    private Task OnFilterSelectionChangedAsync(DimensionFilterViewModel filter) => OnDimensionValuesChanged.InvokeAsync(filter);

    private Task OnShowCountChangedAsync(bool value) => ShowCountChanged.InvokeAsync(value);
}
