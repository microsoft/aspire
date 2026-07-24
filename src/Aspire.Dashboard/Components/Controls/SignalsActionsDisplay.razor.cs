// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class SignalsActionsDisplay
{
    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Parameter, EditorRequired]
    public required bool IsPaused { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<bool> OnPausedChanged { get; set; }

    [Parameter, EditorRequired]
    public required Func<ResourceKey?, Task> HandleClearSignal { get; set; }

    [Parameter, EditorRequired]
    public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }
}
