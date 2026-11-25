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
/// An instrument is pinned for a resource (see <see cref="MetricPin"/>). Pinning applies to the resources of the current
/// selection: pinning <c>http.server.request.duration</c> while two services are selected pins it for both, and
/// unpinning it unpins it for both. Pins are keyed by resource name rather than by replica, so a resource's replicas
/// share them and they stay valid when replicas get new instance names.
/// </para>
/// <para>
/// Pins describe the application rather than a single run, so they're kept in protected browser local storage under a key
/// scoped to the application name. The store is scoped to a circuit. Reads are synchronous and return nothing until
/// <see cref="EnsureLoadedAsync"/> has read the stored pins, which raises <see cref="Changed"/>. Changes are merged
/// with pins saved by other tabs of the same browser rather than replacing them.
/// </para>
/// </remarks>
public sealed class MetricPinStore
{
    internal const int MaxSavedPins = 50;

    private readonly LocalStorageSetting<List<MetricPin>> _setting;
    private readonly ILogger<MetricPinStore> _logger;

    public MetricPinStore(ILocalStorage localStorage, LocalStorageLocks locks, IOptions<DashboardOptions> options, ILogger<MetricPinStore> logger)
    {
        _logger = logger;
        _setting = new(localStorage, locks, BrowserStorageKeys.MetricPinsKey(options.Value.GetApplicationNameOrDefault()), logger, protect: true,
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
    /// Gets the pins in the order they were made.
    /// </summary>
    public IReadOnlyList<MetricPin> GetPins() => _setting.Read(pins => pins.ToArray());

    /// <summary>
    /// Gets the instruments among <paramref name="instruments"/> that are pinned for the resource reporting them, in the
    /// order they were pinned. A resource's replicas report the same instrument, and each of them is pinned with it.
    /// </summary>
    public List<OtlpInstrumentSummary> GetPinnedInstruments(IEnumerable<OtlpInstrumentSummary> instruments)
    {
        var candidates = instruments.ToList();
        return [.. GetPins().SelectMany(pin => candidates.Where(pin.Matches))];
    }

    /// <summary>
    /// Gets whether <paramref name="instrument"/> is pinned for the resource reporting it.
    /// </summary>
    public bool IsPinned(OtlpInstrumentSummary instrument) => _setting.Read(pins => pins.Any(pin => pin.Matches(instrument)));

    /// <summary>
    /// Gets whether <paramref name="key"/> is pinned for any of <paramref name="resourceNames"/>, such as the resources
    /// of the current selection.
    /// </summary>
    public bool IsPinned(OtlpInstrumentKey key, IReadOnlyCollection<string> resourceNames) =>
        _setting.Read(pins => pins.Any(pin => resourceNames.Any(name => pin.Matches(name, key))));

    /// <summary>
    /// Pins or unpins an instrument for each of <paramref name="resourceNames"/>. A pin operation is rejected if
    /// the new pins would exceed the saved pin limit.
    /// </summary>
    internal Task<SettingUpdateResult> SetPinnedAsync(OtlpInstrumentKey key, IReadOnlyCollection<string> resourceNames, bool pinned)
    {
        return _setting.UpdateAsync(pins =>
        {
            var changed = false;
            foreach (var name in resourceNames)
            {
                changed |= pinned
                    ? AddPin(pins, new MetricPin(name, key.MeterName, key.InstrumentName))
                    : pins.RemoveAll(pin => pin.Matches(name, key)) > 0;
                if (pinned && pins.Count > MaxSavedPins)
                {
                    // TODO: If users hit this limit, let them remove saved pins without matching telemetry.
                    // Those pins count toward the limit but can't be removed in the dashboard.
                    // The candidate value is discarded, so none of the pins in this selection are saved.
                    _logger.LogWarning("Cannot pin the instrument for the selection because it would exceed the limit of {Limit} saved metric pins.", MaxSavedPins);
                    return SettingUpdateResult.LimitReached;
                }
            }

            return changed ? SettingUpdateResult.Changed : SettingUpdateResult.Unchanged;
        });
    }

    private static bool AddPin(List<MetricPin> pins, MetricPin pin)
    {
        if (pins.Any(existing => existing.Matches(pin.ResourceName, pin.GetInstrumentKey())))
        {
            return false;
        }

        pins.Add(pin);
        return true;
    }

    // Drop incomplete and repeated pins. A pin without a resource name can't be attributed to a resource.
    private static List<MetricPin> NormalizePins(List<MetricPin>? stored)
    {
        var pins = new List<MetricPin>();
        foreach (var pin in stored ?? [])
        {
            if (pins.Count == MaxSavedPins)
            {
                break;
            }

            if (!string.IsNullOrEmpty(pin.ResourceName) && !string.IsNullOrEmpty(pin.MeterName) && !string.IsNullOrEmpty(pin.InstrumentName))
            {
                AddPin(pins, pin);
            }
        }

        return pins;
    }
}
