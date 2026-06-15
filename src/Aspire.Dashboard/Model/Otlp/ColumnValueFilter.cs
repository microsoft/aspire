// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model.Otlp;

/// <summary>
/// A telemetry filter that restricts results to entries whose column value is in the set
/// of checked (visible) values. Used by column-header filter menus.
/// </summary>
internal sealed class ColumnValueFilter : TelemetryFilter
{
    private readonly ConcurrentDictionary<string, bool> _allowedValues;
    private readonly Func<OtlpLogEntry, string> _logValueExtractor;
    private readonly Func<OtlpSpan, string>? _spanValueExtractor;

    /// <summary>
    /// Cached flag indicating whether all values are currently checked (no filtering needed).
    /// Recalculated via <see cref="RecalculateIsUnfiltered"/> when filter state changes,
    /// avoiding repeated snapshot allocations from <c>ConcurrentDictionary.Values.All()</c>.
    /// </summary>
    private volatile bool _isUnfiltered = true;

    public ColumnValueFilter(
        ConcurrentDictionary<string, bool> allowedValues,
        Func<OtlpLogEntry, string> logValueExtractor,
        Func<OtlpSpan, string>? spanValueExtractor = null)
    {
        _allowedValues = allowedValues;
        _logValueExtractor = logValueExtractor;
        _spanValueExtractor = spanValueExtractor;
        RecalculateIsUnfiltered();
    }

    /// <summary>
    /// Recalculates the cached unfiltered state. Call this after the filter values change
    /// (e.g., after a checkbox is toggled in the <c>ColumnFilterMenu</c>).
    /// </summary>
    public void RecalculateIsUnfiltered()
    {
        _isUnfiltered = _allowedValues.IsEmpty || _allowedValues.Values.All(v => v);
    }

    public override IEnumerable<OtlpLogEntry> Apply(IEnumerable<OtlpLogEntry> input)
    {
        if (_isUnfiltered)
        {
            return input;
        }

        return input.Where(entry =>
        {
            var value = _logValueExtractor(entry);
            // Default to visible when the value isn't in the dictionary. A value can be absent if its
            // telemetry arrives before the column's checkbox state is seeded (e.g. a new resource whose
            // logs arrive before OnNewResources updates the resource list, or LogLevel.None which isn't
            // an explicit checkbox). Hiding such entries would silently drop telemetry the user never
            // had a chance to filter out.
            return !_allowedValues.TryGetValue(value, out var isVisible) || isVisible;
        });
    }

    public override bool Apply(OtlpSpan span)
    {
        if (_spanValueExtractor is null)
        {
            return true;
        }

        if (_isUnfiltered)
        {
            return true;
        }

        var value = _spanValueExtractor(span);
        // Default to visible when the value isn't in the dictionary. A span's resource can be absent if
        // its telemetry arrives before OnNewResources seeds the resource list. Hiding it would silently
        // drop traces the user never had a chance to filter out.
        return !_allowedValues.TryGetValue(value, out var isVisible) || isVisible;
    }

    public override bool Equals(TelemetryFilter? other)
    {
        // Each ColumnValueFilter instance is unique to a column; reference equality is sufficient.
        return ReferenceEquals(this, other);
    }
}
