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

    public ColumnValueFilter(
        ConcurrentDictionary<string, bool> allowedValues,
        Func<OtlpLogEntry, string> logValueExtractor,
        Func<OtlpSpan, string>? spanValueExtractor = null)
    {
        _allowedValues = allowedValues;
        _logValueExtractor = logValueExtractor;
        _spanValueExtractor = spanValueExtractor;
    }

    public override IEnumerable<OtlpLogEntry> Apply(IEnumerable<OtlpLogEntry> input)
    {
        // If all values are checked, no filtering is needed.
        if (_allowedValues.Values.All(v => v))
        {
            return input;
        }

        return input.Where(entry =>
        {
            var value = _logValueExtractor(entry);
            return _allowedValues.TryGetValue(value, out var isVisible) && isVisible;
        });
    }

    public override bool Apply(OtlpSpan span)
    {
        if (_spanValueExtractor is null)
        {
            return true;
        }

        // If all values are checked, no filtering is needed.
        if (_allowedValues.Values.All(v => v))
        {
            return true;
        }

        var value = _spanValueExtractor(span);
        return _allowedValues.TryGetValue(value, out var isVisible) && isVisible;
    }

    public override bool Equals(TelemetryFilter? other)
    {
        // Each ColumnValueFilter instance is unique to a column; reference equality is sufficient.
        return ReferenceEquals(this, other);
    }
}
