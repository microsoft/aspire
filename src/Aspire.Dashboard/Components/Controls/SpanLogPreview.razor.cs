// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components;

public sealed partial class SpanLogPreview : ComponentBase
{
    /// <summary>
    /// The id of the preview's element, which each log marker names in its <c>data-tooltip-content</c> attribute.
    /// </summary>
    public const string ElementId = "span-log-preview";

    private LogSummary? _logEntry;

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.StructuredLogs> Loc { get; init; }

    /// <summary>
    /// Fills the preview with a log marker's entry, when the pointer or keyboard reaches the marker.
    /// </summary>
    /// <remarks>
    /// The marker's handler targets this component, not the page, so only the preview renders again, not the trace's
    /// whole waterfall.
    /// </remarks>
    public void Show(LogSummary logEntry)
    {
        if (_logEntry?.InternalId == logEntry.InternalId)
        {
            return;
        }

        _logEntry = logEntry;
        StateHasChanged();
    }

    internal LogSummary? LogEntry => _logEntry;
}
