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
/// <see cref="EnsureLoadedAsync"/> has read the stored pins, which raises <see cref="Changed"/>.
/// </para>
/// </remarks>
public sealed class MetricPinStore
{
    private readonly Lock _lock = new();
    private readonly List<OtlpInstrumentKey> _pins = [];
    private readonly LocalStorageSetting<List<OtlpInstrumentKey>> _setting;
    private Task? _loadTask;
    private bool _loaded;

    public MetricPinStore(ILocalStorage localStorage, IOptions<DashboardOptions> options, ILogger<MetricPinStore> logger)
    {
        _setting = new(localStorage, BrowserStorageKeys.MetricPinsKey(options.Value.GetApplicationNameOrDefault()), logger);
    }

    /// <summary>
    /// Raised after pins are loaded or change. Handlers may run on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the stored pins, once per circuit.
    /// </summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    /// <summary>
    /// Gets the pinned instruments in the order they were pinned.
    /// </summary>
    public IReadOnlyList<OtlpInstrumentKey> GetPins()
    {
        lock (_lock)
        {
            return [.. _pins];
        }
    }

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

    public bool IsPinned(OtlpInstrumentKey key)
    {
        lock (_lock)
        {
            return _pins.Contains(key);
        }
    }

    /// <summary>
    /// Pins or unpins an instrument.
    /// </summary>
    public void SetPinned(OtlpInstrumentKey key, bool pinned)
    {
        lock (_lock)
        {
            if (_pins.Contains(key) == pinned)
            {
                return;
            }

            if (pinned)
            {
                _pins.Add(key);
            }
            else
            {
                _pins.Remove(key);
            }
            SaveLocked();
        }

        Changed?.Invoke();
    }

    private async Task LoadAsync()
    {
        var stored = await _setting.ReadAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _loaded = true;

            // Instruments pinned before the stored pins were read stay pinned after the stored ones, then are saved
            // so neither replaces the other.
            var pinnedBeforeLoad = _pins.ToList();
            _pins.Clear();
            foreach (var key in (stored ?? []).Concat(pinnedBeforeLoad))
            {
                if (!string.IsNullOrEmpty(key.MeterName) && !string.IsNullOrEmpty(key.InstrumentName) && !_pins.Contains(key))
                {
                    _pins.Add(key);
                }
            }
            if (pinnedBeforeLoad.Count > 0)
            {
                SaveLocked();
            }
        }

        if (stored is { Count: > 0 })
        {
            Changed?.Invoke();
        }
    }

    private void SaveLocked()
    {
        // Until the stored pins are read, saving would replace them. LoadAsync saves changes made before then.
        if (_loaded)
        {
            _setting.Write([.. _pins]);
        }
    }
}
