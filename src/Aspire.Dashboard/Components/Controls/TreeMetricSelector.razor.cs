// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class TreeMetricSelector
{
    [Parameter, EditorRequired]
    public required Func<Task> HandleSelectedTreeItemChangedAsync { get; set; }

    [Parameter, EditorRequired]
    public required Metrics.MetricsViewModel PageViewModel { get; set; }

    [Parameter]
    public bool IncludeLabel { get; set; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    public void OnResourceChanged()
    {
        StateHasChanged();
    }
}
