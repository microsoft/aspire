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
/// browsers. Changes are merged with positions saved by other tabs of the same browser rather than replacing them;
/// see <see cref="LocalStorageSetting{TValue}"/>.
/// </para>
/// <para>
/// The store is scoped to a circuit. Reads are synchronous and only include stored positions once
/// <see cref="EnsureLoadedAsync"/> has read them, which raises <see cref="Changed"/>. Items given a position before
/// then are placed after the stored ones.
/// </para>
/// </remarks>
public sealed class ResourceOrderStore
{
    // Positions declared by the AppHost aren't stored: they come with the resources on every run.
    private readonly Lock _defaultsLock = new();
    private readonly Dictionary<string, long> _defaultPositions = new(StringComparers.ResourceName);
    private readonly LocalStorageSetting<ResourceOrderSettings> _setting;

    public ResourceOrderStore(ILocalStorage localStorage, IOptions<DashboardOptions> options, ILogger<ResourceOrderStore> logger)
    {
        _setting = new(localStorage, BrowserStorageKeys.ResourceOrderKey(options.Value.GetApplicationNameOrDefault()), logger,
            Normalize, () => Changed?.Invoke());
    }

    public event Action? Changed;

    /// <summary>
    /// Reads the stored positions, once per circuit.
    /// </summary>
    public Task EnsureLoadedAsync() => _setting.EnsureLoadedAsync();

    public IReadOnlyDictionary<string, long> GetPositions() => _setting.Read(GetEffectiveResourcePositions);

    public IReadOnlyDictionary<string, long> GetGroupPositions(string grouping) =>
        _setting.Read(order => ToPositions(order.Groups.GetValueOrDefault(grouping) ?? []));

    // Keys must be ResourceViewModel.PersistentKey, not run-specific resource names. Keep positions for absent
    // resources so restarting a run, temporarily removing a resource, or changing its state doesn't reset its order.
    public void EnsureResources(IEnumerable<string> resourceKeys) => EnsureItems(grouping: null, resourceKeys);

    public void EnsureResources(IEnumerable<ResourceViewModel> resources)
    {
        var snapshot = resources.ToList();
        var defaultsChanged = false;
        lock (_defaultsLock)
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
        var snapshot = keys.ToList();
        _setting.Update(order => AppendMissing(GetScope(order, grouping), snapshot));
    }

    public void Move(string resourceKey, string targetKey, bool after) => Move(grouping: null, resourceKey, targetKey, after);

    public void MoveGroup(string grouping, string groupKey, string targetKey, bool after) => Move(grouping, groupKey, targetKey, after);

    private void Move(string? grouping, string key, string targetKey, bool after)
    {
        var hasPositions = _setting.Read(order =>
            (grouping is null ? order.Resources : order.Groups.GetValueOrDefault(grouping)) is { } keys &&
            keys.Contains(key, StringComparers.ResourceName) && keys.Contains(targetKey, StringComparers.ResourceName));
        if (!hasPositions)
        {
            throw new ArgumentException("Both items must have an assigned position.");
        }
        if (StringComparers.ResourceName.Equals(key, targetKey))
        {
            return;
        }

        _setting.Update(order =>
        {
            // Another tab can have saved an order without these items, for example after local storage was cleared.
            var keys = GetScope(order, grouping);
            if (!keys.Contains(key, StringComparers.ResourceName) || !keys.Contains(targetKey, StringComparers.ResourceName))
            {
                return false;
            }

            var orderedKeys = grouping is null
                ? GetEffectiveResourcePositions(order).OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList()
                : [.. keys];
            orderedKeys.RemoveAll(k => StringComparers.ResourceName.Equals(k, key));
            var targetIndex = orderedKeys.FindIndex(k => StringComparers.ResourceName.Equals(k, targetKey));
            orderedKeys.Insert(targetIndex + (after ? 1 : 0), key);
            keys.Clear();
            keys.AddRange(orderedKeys);
            if (grouping is null)
            {
                order.CustomOrder = true;
            }
            return true;
        });
    }

    private static List<string> GetScope(ResourceOrderSettings order, string? grouping)
    {
        if (grouping is null)
        {
            return order.Resources;
        }
        if (!order.Groups.TryGetValue(grouping, out var keys))
        {
            keys = [];
            order.Groups.Add(grouping, keys);
        }

        return keys;
    }

    private static bool AppendMissing(List<string> keys, IEnumerable<string?> newKeys)
    {
        var existingKeys = keys.ToHashSet(StringComparers.ResourceName);
        var count = keys.Count;
        foreach (var key in newKeys)
        {
            // Group keys can be empty, so only skip missing keys from an edited or corrupted value.
            if (key is not null && existingKeys.Add(key))
            {
                keys.Add(key);
            }
        }

        return keys.Count > count;
    }

    private static Dictionary<string, long> ToPositions(List<string> keys)
    {
        var positions = new Dictionary<string, long>(StringComparers.ResourceName);
        for (var i = 0; i < keys.Count; i++)
        {
            positions[keys[i]] = i;
        }

        return positions;
    }

    private Dictionary<string, long> GetEffectiveResourcePositions(ResourceOrderSettings order)
    {
        lock (_defaultsLock)
        {
            if (order.CustomOrder || _defaultPositions.Count == 0)
            {
                return ToPositions(order.Resources);
            }

            // Keep declared defaults separate from saved order: a new AppHost default must take
            // effect until the user explicitly reorders, including after restarting the dashboard.
            var positions = new Dictionary<string, long>(StringComparers.ResourceName);
            var nextPosition = _defaultPositions.Values.Max() + 1;
            foreach (var key in order.Resources)
            {
                positions[key] = _defaultPositions.TryGetValue(key, out var position) ? position : nextPosition++;
            }

            return positions;
        }
    }

    // Stored order can be edited in the browser, so rebuild it without repeated or missing keys.
    private static ResourceOrderSettings Normalize(ResourceOrderSettings? stored)
    {
        var order = new ResourceOrderSettings { CustomOrder = stored?.CustomOrder ?? false };
        AppendMissing(order.Resources, stored?.Resources ?? []);
        foreach (var (grouping, keys) in stored?.Groups ?? [])
        {
            AppendMissing(GetScope(order, grouping), keys ?? []);
        }

        return order;
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
    public List<string> Resources { get; set; } = [];

    /// <summary>
    /// Gets or sets the group keys of each grouping, such as "Tags" or "State", in order.
    /// </summary>
    public Dictionary<string, List<string>> Groups { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets a value indicating whether the user reordered resources. Until then, positions declared by the
    /// AppHost take precedence over the stored order.
    /// </summary>
    public bool CustomOrder { get; set; }
}
