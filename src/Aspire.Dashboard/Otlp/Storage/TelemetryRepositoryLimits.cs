// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Storage;

internal static class TelemetryRepositoryLimits
{
    public const int MaxResourceViewCount = 10_000;
    public const int MaxInstrumentCount = 10_000;
    public const int MaxScopeCount = 10_000;
    public const int MaxDimensionCount = 10_000;
    public const int MaxKnownAttributeValueCount = 10_000;
    public const int MaxKnownAttributeValuesPerKey = 10_000;
}

internal sealed class KnownAttributeValuesState
{
    private readonly Dictionary<string, HashSet<string>> _values = new(StringComparer.Ordinal);

    public void LoadDimension(IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        AddDimension(attributes);
    }

    public void ValidateDimension(IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        var dimensionValues = GetDimensionValues(attributes);
        var newKeyCount = dimensionValues.Keys.Count(key => !_values.ContainsKey(key));
        if (_values.Count + newKeyCount > TelemetryRepositoryLimits.MaxKnownAttributeValueCount)
        {
            throw new InvalidOperationException($"Known attribute key limit of {TelemetryRepositoryLimits.MaxKnownAttributeValueCount} reached.");
        }

        foreach (var (key, value) in dimensionValues)
        {
            if (_values.TryGetValue(key, out var values) &&
                !values.Contains(value) &&
                values.Count >= TelemetryRepositoryLimits.MaxKnownAttributeValuesPerKey)
            {
                throw new InvalidOperationException($"Known attribute value limit of {TelemetryRepositoryLimits.MaxKnownAttributeValuesPerKey} reached for key '{key}'.");
            }
        }
    }

    public void AddDimension(IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        foreach (var (key, value) in GetDimensionValues(attributes))
        {
            if (!_values.TryGetValue(key, out var values))
            {
                values = [];
                _values.Add(key, values);
            }
            values.Add(value);
        }
    }

    private static Dictionary<string, string> GetDimensionValues(IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        var dimensionValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            dimensionValues.TryAdd(attribute.Key, attribute.Value);
        }
        return dimensionValues;
    }
}
