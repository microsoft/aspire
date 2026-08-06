// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;

namespace Aspire.Dashboard.Model;

public class InstrumentViewModel
{
    private ImmutableArray<Func<Task>> _dataUpdateSubscriptions = [];

    public OtlpInstrumentSummary? Instrument { get; private set; }
    public List<DimensionScope>? MatchedDimensions { get; private set; }

    public string? Theme { get; set; }
    public bool ShowCount { get; set; }

    internal void AddDataUpdateSubscription(Func<Task> subscription)
    {
        ImmutableInterlocked.Update(ref _dataUpdateSubscriptions, static (subscriptions, subscription) => subscriptions.Add(subscription), subscription);
    }

    internal void RemoveDataUpdateSubscription(Func<Task> subscription)
    {
        ImmutableInterlocked.Update(ref _dataUpdateSubscriptions, static (subscriptions, subscription) => subscriptions.Remove(subscription), subscription);
    }

    public async Task UpdateDataAsync(OtlpInstrumentSummary instrument, List<DimensionScope> matchedDimensions)
    {
        Instrument = instrument;
        MatchedDimensions = matchedDimensions;

        // A chart can be disposed while another subscription is awaiting a render. Invoke the immutable
        // snapshot captured at the start so disposal can't invalidate the active enumeration.
        var subscriptions = _dataUpdateSubscriptions;

        foreach (var subscription in subscriptions)
        {
            await subscription().ConfigureAwait(false);
        }
    }
}
