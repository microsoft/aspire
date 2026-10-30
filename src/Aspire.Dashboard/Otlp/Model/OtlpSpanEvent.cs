// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Model;
using Aspire.Shared;

namespace Aspire.Dashboard.Otlp.Model;

public class OtlpSpanAttributeItem(string name, string value) : IPropertyGridItem
{
    public string Name { get; } = name;
    public string Value { get; } = value;
    public string? TextVisualizerFormat { get; init; }

    internal static IEnumerable<OtlpSpanAttributeItem> CreateItems(KeyValuePair<string, string>[] attributes, KeyValuePair<string, string>[] fallbackAttributes)
    {
        var databaseSystem = TextVisualizerFormatHelpers.GetDatabaseSystem(attributes) ?? TextVisualizerFormatHelpers.GetDatabaseSystem(fallbackAttributes);
        return attributes.Select(pair => new OtlpSpanAttributeItem(pair.Key, pair.Value)
        {
            TextVisualizerFormat = TextVisualizerFormatHelpers.GetFormat(pair.Key, databaseSystem)
        });
    }
}

public class OtlpSpanEvent(OtlpSpan span) : IPropertyGridItem
{
    public required Guid InternalId { get; init; }
    public required string Name { get; init; }
    public required DateTime Time { get; init; }
    public required KeyValuePair<string, string>[] Attributes { get; init; }
    string IPropertyGridItem.Name => DurationFormatter.FormatDuration(Time - span.StartTime, CultureInfo.CurrentCulture);
    object IPropertyGridItem.Key => InternalId;
    string? IPropertyGridItem.Value => Name;
}
