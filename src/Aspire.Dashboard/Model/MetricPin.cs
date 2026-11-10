// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model;

/// <summary>
/// A metric instrument pinned for a resource.
/// </summary>
/// <param name="ResourceName">
/// The name of the resource that reports the instrument, without a replica's instance id, so the resource's replicas
/// share the pin and it stays valid when they get new instance ids.
/// </param>
/// <param name="MeterName">The name of the instrument's meter.</param>
/// <param name="InstrumentName">The name of the instrument.</param>
public readonly record struct MetricPin(string ResourceName, string MeterName, string InstrumentName)
{
    public OtlpInstrumentKey GetInstrumentKey() => new(MeterName, InstrumentName);

    /// <summary>
    /// Gets whether this pins <paramref name="key"/> for <paramref name="resourceName"/>. Resource names are compared
    /// like everywhere else in the dashboard, ignoring case.
    /// </summary>
    public bool Matches(string resourceName, OtlpInstrumentKey key) =>
        string.Equals(ResourceName, resourceName, StringComparisons.ResourceName) && GetInstrumentKey() == key;

    /// <summary>
    /// Gets whether this pins <paramref name="instrument"/>, which is pinned for the resource that reports it.
    /// </summary>
    public bool Matches(OtlpInstrumentSummary instrument) => Matches(instrument.ResourceView.Resource.ResourceName, instrument.GetKey());
}
