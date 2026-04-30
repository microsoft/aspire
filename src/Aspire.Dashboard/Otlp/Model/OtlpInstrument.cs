// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;

namespace Aspire.Dashboard.Otlp.Model;

[DebuggerDisplay("Name = {Name}, Unit = {Unit}, Type = {Type}")]
public class OtlpInstrumentSummary
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Unit { get; init; }
    public required OtlpInstrumentType Type { get; init; }
    public required OtlpAggregationTemporality AggregationTemporality { get; init; }
    public required OtlpScope Parent { get; init; }

    public OtlpInstrumentKey GetKey() => new(Parent.Name, Name);
}

public class OtlpInstrumentData
{
    public required OtlpInstrumentSummary Summary { get; init; }
    public required List<DimensionScope> Dimensions { get; init; }
    public required Dictionary<string, List<string?>> KnownAttributeValues { get; init; }
    public required bool HasOverflow { get; init; }
}

[DebuggerDisplay("Name = {Summary.Name}, Unit = {Summary.Unit}, Type = {Summary.Type}")]
public class OtlpInstrument
{
    public required OtlpInstrumentSummary Summary { get; init; }
    public required OtlpContext Context { get; init; }

    public Dictionary<ReadOnlyMemory<KeyValuePair<string, string>>, DimensionScope> Dimensions { get; } = new(ScopeAttributesComparer.Instance);
    public Dictionary<string, List<string?>> KnownAttributeValues { get; } = new();
    public bool HasOverflow { get; set; }

    public DimensionScope FindScope(RepeatedField<KeyValue> attributes, ref KeyValuePair<string, string>[]? tempAttributes)
    {
        // See https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/metrics/sdk.md#overflow-attribute
        // Inspect attributes before they're merged with parent attributes. "otel.metric.overflow" should be the only attribute.
        if (!HasOverflow && attributes.Count == 1 && attributes[0].Key == "otel.metric.overflow" && attributes[0].Value.GetString() == "true")
        {
            HasOverflow = true;
        }

        // We want to find the dimension scope that matches the attributes, but we don't want to allocate.
        // Copy values to a temporary reusable array.
        //
        // A meter can have attributes. Merge these with the data point attributes when creating a dimension.
        OtlpHelpers.CopyKeyValuePairs(attributes, Summary.Parent.Attributes, Context, out var copyCount, ref tempAttributes);
        Array.Sort(tempAttributes, 0, copyCount, KeyValuePairComparer.Instance);

        var comparableAttributes = tempAttributes.AsMemory(0, copyCount);

        // Can't use CollectionsMarshal.GetValueRefOrAddDefault here because comparableAttributes is a view over mutable data.
        // Need to add dimensions using durable attributes instance after scope is created.
        if (!Dimensions.TryGetValue(comparableAttributes, out var dimension))
        {
            if (Dimensions.Count >= TelemetryRepository.MaxDimensionCount)
            {
                throw new InvalidOperationException($"Dimension limit of {TelemetryRepository.MaxDimensionCount} reached for instrument '{Summary.Name}'.");
            }

            dimension = CreateDimensionScope(comparableAttributes);
            Dimensions.Add(dimension.Attributes, dimension);
        }
        return dimension;
    }

    private DimensionScope CreateDimensionScope(Memory<KeyValuePair<string, string>> comparableAttributes)
    {
        var isFirst = Dimensions.Count == 0;
        var durableAttributes = comparableAttributes.ToArray();
        var dimension = new DimensionScope(Context.Options.MaxMetricsCount, durableAttributes);

        var keys = KnownAttributeValues.Keys.Union(durableAttributes.Select(a => a.Key)).Distinct();
        foreach (var key in keys)
        {
            ref var values = ref CollectionsMarshal.GetValueRefOrAddDefault(KnownAttributeValues, key, out var existed);
            // Adds to dictionary if not present.
            if (values == null)
            {
                if (!existed && KnownAttributeValues.Count > TelemetryRepository.MaxKnownAttributeValueCount)
                {
                    // Over limit. Remove the default entry that GetValueRefOrAddDefault added.
                    KnownAttributeValues.Remove(key);
                    continue;
                }

                values = new List<string?>();

                // If the key is new and there are already dimensions, add an empty value because there are dimensions without this key.
                if (!isFirst)
                {
                    TryAddValue(values, null, TelemetryRepository.MaxKnownAttributeValuesPerKey);
                }
            }

            var currentDimensionValue = OtlpHelpers.GetValue(durableAttributes, key);
            TryAddValue(values, currentDimensionValue, TelemetryRepository.MaxKnownAttributeValuesPerKey);
        }

        return dimension;

        static void TryAddValue(List<string?> values, string? value, int maxValues)
        {
            if (values.Count < maxValues && !values.Contains(value))
            {
                values.Add(value);
            }
        }
    }

    public static OtlpInstrument Clone(OtlpInstrument instrument, bool cloneData, DateTime? valuesStart, DateTime? valuesEnd)
    {
        var newInstrument = new OtlpInstrument
        {
            Summary = instrument.Summary,
            Context = instrument.Context,
            HasOverflow = instrument.HasOverflow
        };

        if (cloneData)
        {
            foreach (var item in instrument.KnownAttributeValues)
            {
                newInstrument.KnownAttributeValues.Add(item.Key, item.Value.ToList());
            }
            foreach (var item in instrument.Dimensions)
            {
                newInstrument.Dimensions.Add(item.Key, DimensionScope.Clone(item.Value, valuesStart, valuesEnd));
            }
        }

        return newInstrument;
    }

    private sealed class ScopeAttributesComparer : IEqualityComparer<ReadOnlyMemory<KeyValuePair<string, string>>>
    {
        public static readonly ScopeAttributesComparer Instance = new();

        public bool Equals(ReadOnlyMemory<KeyValuePair<string, string>> x, ReadOnlyMemory<KeyValuePair<string, string>> y)
        {
            return x.Span.SequenceEqual(y.Span);
        }

        public int GetHashCode([DisallowNull] ReadOnlyMemory<KeyValuePair<string, string>> obj)
        {
            var hashcode = new HashCode();
            foreach (KeyValuePair<string, string> pair in obj.Span)
            {
                hashcode.Add(pair.Key);
                hashcode.Add(pair.Value);
            }
            return hashcode.ToHashCode();
        }
    }

    private sealed class KeyValuePairComparer : IComparer<KeyValuePair<string, string>>
    {
        public static readonly KeyValuePairComparer Instance = new();

        public int Compare(KeyValuePair<string, string> x, KeyValuePair<string, string> y)
        {
            return string.Compare(x.Key, y.Key, StringComparison.Ordinal);
        }
    }
}
