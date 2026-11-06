// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores resource and group positions, independently of individual runs.
/// </summary>
/// <remarks>
/// <para>
/// Positions describe the application rather than a single run, so they're kept in the browser's local storage under
/// a key scoped to the application name, and every run of the application uses them. They aren't shared between
/// browsers.
/// </para>
/// <para>
/// The store is scoped to a circuit. Reads are synchronous and only include stored positions once
/// <see cref="EnsureLoadedAsync"/> has read them, which raises <see cref="Changed"/>. Await it before assigning
/// positions to new resources, so they're placed after the stored ones.
/// </para>
/// </remarks>
public sealed class ResourceOrderStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _positions = new(StringComparers.ResourceName);
    private readonly Dictionary<string, long> _defaultPositions = new(StringComparers.ResourceName);
    private readonly Dictionary<string, Dictionary<string, long>> _groupPositions = new(StringComparer.Ordinal);
    private readonly LocalStorageSetting<ResourceOrderSettings> _setting;
    private Task? _loadTask;
    private bool _loaded;
    private bool _hasCustomResourceOrder;

    public ResourceOrderStore(ILocalStorage localStorage, IOptions<DashboardOptions> options, ILogger<ResourceOrderStore> logger)
    {
        _setting = new(localStorage, BrowserStorageKeys.ResourceOrderKey(options.Value.GetApplicationNameOrDefault()), logger);
    }

    public event Action? Changed;

    /// <summary>
    /// Reads the stored positions, once per circuit.
    /// </summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    public IReadOnlyDictionary<string, long> GetPositions() => GetPositions(grouping: null);

    public IReadOnlyDictionary<string, long> GetGroupPositions(string grouping) => GetPositions(grouping);

    private IReadOnlyDictionary<string, long> GetPositions(string? grouping)
    {
        lock (_lock)
        {
            return grouping is null ? GetEffectiveResourcePositions() : new Dictionary<string, long>(GetScopePositions(grouping), StringComparers.ResourceName);
        }
    }

    // Keys must be ResourceViewModel.PersistentKey, not run-specific resource names. Keep positions for absent
    // resources so restarting a run, temporarily removing a resource, or changing its state doesn't reset its order.
    public void EnsureResources(IEnumerable<string> resourceKeys) => EnsureItems(grouping: null, resourceKeys);

    public void EnsureResources(IEnumerable<ResourceViewModel> resources)
    {
        var snapshot = resources.ToList();
        var defaultsChanged = false;
        lock (_lock)
        {
            foreach (var resource in snapshot)
            {
                if (resource.DefaultDashboardPosition is { } position)
                {
                    defaultsChanged |= !_defaultPositions.TryGetValue(resource.PersistentKey, out var previous) || previous != position;
                    _defaultPositions[resource.PersistentKey] = position;
                }
                else
                {
                    defaultsChanged |= _defaultPositions.Remove(resource.PersistentKey);
                }
            }
        }
        EnsureItems(grouping: null, snapshot.Select(r => r.PersistentKey));
        if (defaultsChanged)
        {
            Changed?.Invoke();
        }
    }

    public void EnsureGroups(string grouping, IEnumerable<string> groupKeys) => EnsureItems(grouping, groupKeys);

    private void EnsureItems(string? grouping, IEnumerable<string> keys)
    {
        var changed = false;
        lock (_lock)
        {
            var positions = GetScopePositions(grouping);
            var newKeys = keys.Where(key => !positions.ContainsKey(key)).Distinct(StringComparers.ResourceName).ToList();
            if (newKeys.Count > 0)
            {
                var nextPosition = positions.Count == 0 ? 0 : positions.Values.Max() + 1;
                foreach (var key in newKeys)
                {
                    positions.Add(key, nextPosition++);
                }
                changed = true;
                SaveLocked();
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void Move(string resourceKey, string targetKey, bool after) => Move(grouping: null, resourceKey, targetKey, after);

    public void MoveGroup(string grouping, string groupKey, string targetKey, bool after) => Move(grouping, groupKey, targetKey, after);

    private void Move(string? grouping, string resourceKey, string targetKey, bool after)
    {
        lock (_lock)
        {
            var currentPositions = GetScopePositions(grouping);
            if (!currentPositions.ContainsKey(resourceKey) || !currentPositions.ContainsKey(targetKey))
            {
                throw new ArgumentException("Both items must have an assigned position.");
            }
            if (StringComparers.ResourceName.Equals(resourceKey, targetKey))
            {
                return;
            }

            var effectivePositions = grouping is null ? GetEffectiveResourcePositions() : currentPositions;
            var orderedKeys = effectivePositions.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList();
            orderedKeys.RemoveAll(key => StringComparers.ResourceName.Equals(key, resourceKey));
            var targetIndex = orderedKeys.FindIndex(key => StringComparers.ResourceName.Equals(key, targetKey));
            orderedKeys.Insert(targetIndex + (after ? 1 : 0), resourceKey);
            currentPositions.Clear();
            for (var i = 0; i < orderedKeys.Count; i++)
            {
                currentPositions.Add(orderedKeys[i], i);
            }
            if (grouping is null)
            {
                _hasCustomResourceOrder = true;
            }
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private Dictionary<string, long> GetScopePositions(string? grouping)
    {
        if (grouping is null)
        {
            return _positions;
        }
        if (!_groupPositions.TryGetValue(grouping, out var positions))
        {
            positions = new Dictionary<string, long>(StringComparers.ResourceName);
            _groupPositions.Add(grouping, positions);
        }

        return positions;
    }

    private Dictionary<string, long> GetEffectiveResourcePositions()
    {
        if (_hasCustomResourceOrder || _defaultPositions.Count == 0)
        {
            return new Dictionary<string, long>(_positions, StringComparers.ResourceName);
        }

        // Keep declared defaults separate from saved order: a new AppHost default must take
        // effect until the user explicitly reorders, including after restarting the dashboard.
        var positions = new Dictionary<string, long>(StringComparers.ResourceName);
        var nextPosition = _defaultPositions.Values.Max() + 1;
        foreach (var pair in _positions.OrderBy(p => p.Value))
        {
            positions[pair.Key] = _defaultPositions.TryGetValue(pair.Key, out var position) ? position : nextPosition++;
        }

        return positions;
    }

    private async Task LoadAsync()
    {
        var stored = await _setting.ReadAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _loaded = true;

            // Positions assigned before the stored ones were read weren't saved, because saving would have replaced
            // the stored positions. Stored positions come first, and those items keep their relative order after the
            // stored ones, like any other new item.
            var assignedBeforeLoad = _positions.Count > 0 || _groupPositions.Values.Any(positions => positions.Count > 0);
            if (stored is not null)
            {
                MergeStoredPositions(_positions, stored.Resources);
                foreach (var (grouping, groupKeys) in stored.Groups ?? [])
                {
                    MergeStoredPositions(GetScopePositions(grouping), groupKeys);
                }
                _hasCustomResourceOrder |= stored.CustomOrder;
            }
            if (assignedBeforeLoad)
            {
                SaveLocked();
            }
        }

        if (stored is not null)
        {
            Changed?.Invoke();
        }
    }

    private static void MergeStoredPositions(Dictionary<string, long> positions, List<string>? storedKeys)
    {
        var assignedBeforeLoad = positions.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList();
        positions.Clear();
        foreach (var key in (storedKeys ?? []).Concat(assignedBeforeLoad))
        {
            // Group keys can be empty, so only skip missing keys from an edited or corrupted value.
            if (key is not null)
            {
                positions.TryAdd(key, positions.Count);
            }
        }
    }

    private void SaveLocked()
    {
        // Until the stored positions are read, saving would replace them. LoadAsync saves changes made before then.
        if (!_loaded)
        {
            return;
        }

        _setting.Write(new ResourceOrderSettings
        {
            Resources = GetOrderedKeys(_positions),
            Groups = _groupPositions.ToDictionary(pair => pair.Key, pair => GetOrderedKeys(pair.Value), StringComparer.Ordinal),
            CustomOrder = _hasCustomResourceOrder
        });

        static List<string> GetOrderedKeys(Dictionary<string, long> positions) =>
            [.. positions.OrderBy(pair => pair.Value).Select(pair => pair.Key)];
    }
}

/// <summary>
/// The resource and group order saved in the browser's local storage.
/// </summary>
internal sealed class ResourceOrderSettings
{
    /// <summary>
    /// Gets or sets the persistent keys of resources, in order.
    /// </summary>
    public List<string>? Resources { get; set; }

    /// <summary>
    /// Gets or sets the group keys of each grouping, such as "Tags" or "State", in order.
    /// </summary>
    public Dictionary<string, List<string>>? Groups { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user reordered resources. Until then, positions declared by the
    /// AppHost take precedence over the stored order.
    /// </summary>
    public bool CustomOrder { get; set; }
}
