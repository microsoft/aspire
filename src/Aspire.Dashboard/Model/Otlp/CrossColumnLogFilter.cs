// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model.Otlp;

/// <summary>
/// A telemetry filter that matches log entries when the search text appears
/// in any user-visible column (message, resource name, trace ID, severity, or category).
/// </summary>
internal sealed class CrossColumnLogFilter : TelemetryFilter
{
    private readonly string _searchText;

    public CrossColumnLogFilter(string searchText)
    {
        _searchText = searchText;
    }

    public override IEnumerable<OtlpLogEntry> Apply(IEnumerable<OtlpLogEntry> input)
    {
        return input.Where(entry =>
            (entry.Message is not null && entry.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase)) ||
            entry.ResourceView.Resource.ResourceName.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
            entry.TraceId.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
            entry.Severity.ToString().Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
            entry.Scope.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
    }

    public override bool Apply(OtlpSpan span)
    {
        // Cross-column log filter is not applicable to spans.
        return true;
    }

    public override bool Equals(TelemetryFilter? other)
    {
        return other is CrossColumnLogFilter otherFilter &&
               string.Equals(_searchText, otherFilter._searchText, StringComparison.OrdinalIgnoreCase);
    }
}
