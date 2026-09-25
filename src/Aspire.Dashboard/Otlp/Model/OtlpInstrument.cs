// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Otlp.Model.MetricValues;

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
    public required OtlpResourceView ResourceView { get; init; }

    public OtlpInstrumentKey GetKey() => new(Parent.Name, Name);
}

public class OtlpInstrumentData
{
    public required OtlpInstrumentSummary Summary { get; init; }
    public required List<DimensionScope> Dimensions { get; init; }
    public required Dictionary<string, List<string?>> KnownAttributeValues { get; init; }
    public required bool HasOverflow { get; init; }
}
