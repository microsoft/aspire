// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores pinned metric instruments, independently of individual runs.
/// </summary>
/// <remarks>
/// <para>
/// Pins are keyed by meter and instrument name, not by resource. Pinning <c>http.server.request.duration</c>
/// once pins it for every service that reports it, which matches how the same instrument is compared across
/// services, and keeps pins valid when resources are renamed or replicas get new instance names.
/// </para>
/// <para>
/// Pins describe the application rather than a single run, so they're kept in the browser's local storage under a key
/// scoped to the application name. The store is scoped to a circuit. Reads are synchronous and return nothing until
/// <see cref="EnsureLoadedAsync"/> has read the stored pins, which raises <see cref="Changed"/>. Changes are merged
/// with pins saved by other tabs of the same browser rather than replacing them.
/// </para>
/// </remarks>
public sealed class MetricPinStore
{
    private readonly LocalStorageSetting<List<OtlpInstrumentKey>> _setting;

    public MetricPinStore(ILocalStorage localStorage, LocalStorageLocks locks, TimeProvider timeProvider, IOptions<DashboardOptions> options, ILogger<MetricPinStore> logger)
    {
        _setting = new(localStorage, locks, timeProvider, BrowserStorageKeys.MetricPinsKey(options.Value.GetApplicationNameOrDefault()), logger,
            NormalizePins, (x, y) => x.SequenceEqual(y), () => Changed?.Invoke());
    }

    /// <summary>
    /// Raised after pins are loaded or change. Handlers may run on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the stored pins, once per circuit.
    /// </summary>
    public Task EnsureLoadedAsync() => _setting.EnsureLoadedAsync();

    /// <summary>
    /// Gets the pinned instruments in the order they were pinned.
    /// </summary>
    public IReadOnlyList<OtlpInstrumentKey> GetPins() => _setting.Read(pins => pins.ToArray());

    /// <summary>
    /// Gets the pinned instruments among <paramref name="instruments"/>, in the order they were pinned.
    /// Pins for instruments that aren't in the list, such as ones only another resource reports, are skipped.
    /// </summary>
    public List<OtlpInstrumentSummary> GetPinnedInstruments(IEnumerable<OtlpInstrumentSummary> instruments)
    {
        // The same instrument can come from several resources, and each of them is pinned along with it.
        var instrumentsByKey = instruments.ToLookup(i => i.GetKey());
        return [.. GetPins().SelectMany(key => instrumentsByKey[key])];
    }

    public bool IsPinned(OtlpInstrumentKey key) => _setting.Read(pins => pins.Contains(key));

    /// <summary>
    /// Pins or unpins an instrument. Pinning an instrument again lists it last.
    /// </summary>
    public void SetPinned(OtlpInstrumentKey key, bool pinned)
    {
        _setting.Update(pins => pinned ? AddPin(pins, key) : pins.Remove(key));
    }

    private static bool AddPin(List<OtlpInstrumentKey> pins, OtlpInstrumentKey key)
    {
        if (pins.Contains(key))
        {
            return false;
        }

        pins.Add(key);
        return true;
    }

    // Stored pins can be edited in the browser, so drop incomplete and repeated ones.
    private static List<OtlpInstrumentKey> NormalizePins(List<OtlpInstrumentKey>? stored)
    {
        var pins = new List<OtlpInstrumentKey>();
        foreach (var key in stored ?? [])
        {
            if (!string.IsNullOrEmpty(key.MeterName) && !string.IsNullOrEmpty(key.InstrumentName))
            {
                AddPin(pins, key);
            }
        }

        return pins;
    }
}
