// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class SpanIdButtonValue
{
    [Parameter, EditorRequired]
    public required string Value { get; set; }

    [Parameter, EditorRequired]
    public required string HighlightText { get; set; }

    [Parameter, EditorRequired]
    public required string TraceId { get; set; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    private async Task OnClickAsync()
    {
        var available = await TraceLinkHelpers.WaitForSpanToBeAvailableAsync(
            traceId: TraceId,
            spanId: Value,
            getSpan: TelemetryRepository.GetSpan,
            DialogService,
            InvokeAsync,
            Loc,
            CancellationToken.None).ConfigureAwait(false);

        if (available)
        {
            NavigationManager.NavigateTo(DashboardUrls.TraceDetailUrl(TraceId, spanId: Value));
        }
    }
}
