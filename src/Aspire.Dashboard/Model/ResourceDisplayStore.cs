// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores how the user arranges resources per application in protected browser local storage: explicit resource and
/// group moves, and which resources and groups are collapsed.
/// </summary>
/// <remarks>
/// Every view of resources in a circuit shares this store, so a change made in one view, such as collapsing a resource
/// in the resources pane, is published to the others.
/// </remarks>
public sealed class ResourceDisplayStore
{
    // Bound the saved history so typical protected values fit within the dashboard's Blazor receive limit.
    private const int MaxSavedMoves = 50;

    private readonly ILocalStorage _localStorage;
    private readonly LocalStorageLocks _locks;
    private readonly IDashboardClient _dashboardClient;
    private readonly ILogger<ResourceDisplayStore> _logger;
    private readonly LocalStorageSetting<List<RelativeOrderMove>> _resourceOrder;
    private readonly LocalStorageSetting<Dictionary<string, List<RelativeOrderMove>>> _groupOrder;
    private readonly Lock _collapsedStateLock = new();
    private CollapsedState? _collapsedState;

    public ResourceDisplayStore(ILocalStorage localStorage, LocalStorageLocks locks, IDashboardClient dashboardClient, IOptions<DashboardOptions> options, ILogger<ResourceDisplayStore> logger)
    {
        _localStorage = localStorage;
        _locks = locks;
        _dashboardClient = dashboardClient;
        _logger = logger;

        var applicationName = options.Value.GetApplicationNameOrDefault();
        _resourceOrder = new(localStorage, locks, BrowserStorageKeys.ResourceOrderKey(applicationName), logger, protect: true,
            NormalizeResourceOrder, (x, y) => x.SequenceEqual(y), OnChanged);
        _groupOrder = new(localStorage, locks, BrowserStorageKeys.GroupOrderKey(applicationName), logger, protect: true,
            NormalizeGroupOrder, (x, y) => x.Count == y.Count && x.All(pair => y.TryGetValue(pair.Key, out var moves) && pair.Value.SequenceEqual(moves)),
            OnChanged);
    }

    public event Action? Changed;

    /// <summary>
    /// Loads the stored order and collapsed state with one browser call. Collapsed state is scoped to the resource
    /// service's application name, so when the client is enabled this waits until it's connected.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_dashboardClient.IsEnabled)
        {
            await _dashboardClient.WhenConnected.ConfigureAwait(false);
        }

        var collapsedState = GetCollapsedState();
        await ILocalStorageSetting.EnsureAllLoadedAsync(_localStorage,
            [_resourceOrder, _groupOrder, collapsedState.ResourceNames, collapsedState.Groups]).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets whether the children of the resource with the specified <see cref="ResourceViewModel.PersistentKey"/> are collapsed.
    /// </summary>
    public bool IsResourceCollapsed(string persistentKey) =>
        GetCollapsedState().ResourceNames.Read(names => names.Contains(persistentKey, StringComparers.ResourceName));

    /// <summary>
    /// Gets whether the group with the specified key is collapsed in the specified grouping. Resource pane sections use
    /// their own grouping.
    /// </summary>
    public bool IsGroupCollapsed(string grouping, string key) =>
        GetCollapsedState().Groups.Read(groups => IsGroupCollapsed(groups, grouping, key));

    /// <summary>
    /// Collapses or expands the children of the resources with the specified <see cref="ResourceViewModel.PersistentKey"/> values.
    /// </summary>
    /// <returns><see langword="false"/> if the change couldn't be saved to browser storage, otherwise <see langword="true"/>.</returns>
    public async Task<bool> SetResourcesCollapsedAsync(IEnumerable<string> persistentKeys, bool collapsed)
    {
        var keys = persistentKeys.ToList();
        if (keys.Count == 0)
        {
            return true;
        }

        // UpdateAsync reports no change without writing when the stored value already matches. The cached value isn't
        // checked first because a previous change to the same keys can still be saving, and the cache doesn't include it
        // until the save completes.
        var result = await GetCollapsedState().ResourceNames.UpdateAsync(stored =>
            SetCollapsed(stored, keys, collapsed, StringComparers.ResourceName) ? SettingUpdateResult.Changed : SettingUpdateResult.Unchanged).ConfigureAwait(false);
        return result != SettingUpdateResult.NotSaved;
    }

    /// <summary>
    /// Collapses the children of the resource with the specified <see cref="ResourceViewModel.PersistentKey"/> if they're
    /// expanded, otherwise expands them.
    /// </summary>
    /// <returns><see langword="false"/> if the change couldn't be saved to browser storage, otherwise <see langword="true"/>.</returns>
    public async Task<bool> ToggleResourceCollapsedAsync(string persistentKey)
    {
        // The new state is worked out from the stored value under the save lock instead of the cached value. The cache
        // doesn't include a toggle that's still saving, so a second click would repeat that toggle instead of undoing it.
        var result = await GetCollapsedState().ResourceNames.UpdateAsync(stored =>
        {
            SetCollapsed(stored, [persistentKey], !stored.Contains(persistentKey, StringComparers.ResourceName), StringComparers.ResourceName);
            return SettingUpdateResult.Changed;
        }).ConfigureAwait(false);
        return result != SettingUpdateResult.NotSaved;
    }

    /// <summary>
    /// Collapses the group with the specified key in the specified grouping if it's expanded, otherwise expands it.
    /// </summary>
    /// <returns><see langword="false"/> if the change couldn't be saved to browser storage, otherwise <see langword="true"/>.</returns>
    public async Task<bool> ToggleGroupCollapsedAsync(string grouping, string key)
    {
        // Like ToggleResourceCollapsedAsync, the new state is worked out from the stored value under the save lock.
        var result = await GetCollapsedState().Groups.UpdateAsync(groups =>
        {
            if (!groups.TryGetValue(grouping, out var keys))
            {
                groups.Add(grouping, keys = []);
            }

            SetCollapsed(keys, [key], !keys.Contains(key, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (keys.Count == 0)
            {
                groups.Remove(grouping);
            }

            return SettingUpdateResult.Changed;
        }).ConfigureAwait(false);
        return result != SettingUpdateResult.NotSaved;
    }

    private static bool IsGroupCollapsed(Dictionary<string, List<string>> groups, string grouping, string key) =>
        groups.TryGetValue(grouping, out var keys) && keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static bool SetCollapsed(List<string> stored, List<string> keys, bool collapsed, StringComparer comparer)
    {
        var changed = false;
        foreach (var key in keys)
        {
            if (collapsed && !stored.Contains(key, comparer))
            {
                stored.Add(key);
                changed = true;
            }
            else if (!collapsed)
            {
                changed |= stored.RemoveAll(item => comparer.Equals(item, key)) > 0;
            }
        }

        return changed;
    }

    private CollapsedState GetCollapsedState()
    {
        // The application name changes when the client connects and when another run is selected, and collapsed state
        // follows it. The state for the previous name is dropped and loaded again if that name is selected again.
        var applicationName = _dashboardClient.ApplicationName;
        lock (_collapsedStateLock)
        {
            if (_collapsedState is null || !StringComparer.Ordinal.Equals(_collapsedState.ApplicationName, applicationName))
            {
                _collapsedState = new CollapsedState(
                    applicationName,
                    new(_localStorage, _locks, BrowserStorageKeys.CollapsedResourceNamesKey(applicationName), _logger, protect: true,
                        stored => NormalizeCollapsedKeys(stored, StringComparers.ResourceName),
                        (x, y) => CollapsedKeysEqual(x, y, StringComparers.ResourceName),
                        OnChanged),
                    new(_localStorage, _locks, BrowserStorageKeys.CollapsedResourceGroupsKey(applicationName), _logger, protect: true,
                        NormalizeCollapsedGroups,
                        (x, y) => x.Count == y.Count && x.All(pair => y.TryGetValue(pair.Key, out var keys) && CollapsedKeysEqual(pair.Value, keys, StringComparer.OrdinalIgnoreCase)),
                        OnChanged));
            }

            return _collapsedState;
        }
    }

    private static List<string> NormalizeCollapsedKeys(List<string>? stored, StringComparer comparer) =>
        stored?.OfType<string>().Distinct(comparer).ToList() ?? [];

    private static bool CollapsedKeysEqual(List<string> x, List<string> y, StringComparer comparer) =>
        x.Count == y.Count && x.ToHashSet(comparer).SetEquals(y);

    private static Dictionary<string, List<string>> NormalizeCollapsedGroups(Dictionary<string, List<string>>? stored) =>
        stored?.Select(pair => KeyValuePair.Create(pair.Key, NormalizeCollapsedKeys(pair.Value, StringComparer.OrdinalIgnoreCase)))
            .Where(pair => pair.Value.Count > 0).ToDictionary(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);

    private void OnChanged() => Changed?.Invoke();

    public List<ResourceViewModel> OrderResources(IEnumerable<ResourceViewModel> resources)
    {
        var ordered = resources.OrderByDescending(r => r.DisplayOrder.GetValueOrDefault())
            // Match the legacy type/name order at priority 0. Ties at explicit nonzero priorities sort by name only.
            .ThenBy(r => r.DisplayOrder.GetValueOrDefault() == 0 ? r.ResourceType : null)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();
        var moves = _resourceOrder.Read(order => order.ToArray());
        if (moves.Length > 0)
        {
            var names = GetResourceNames(ordered);
            ReplayMoves(ordered, moves, r => r.PersistentKey, (source, anchor) =>
                StringComparer.Ordinal.Equals(GetVisibleParent(source, names), GetVisibleParent(anchor, names)));
        }

        return ordered;
    }

    public List<T> OrderGroups<T>(string grouping, List<T> groups, Func<T, string> getKey)
    {
        var moves = _groupOrder.Read(order => order.TryGetValue(grouping, out var saved) ? saved.ToArray() : []);
        ReplayMoves(groups, moves, getKey, canMove: null);
        return groups;
    }

    internal async Task<ResourceOrderMoveResult> MoveResourceAsync(ResourceViewModel resource, ResourceViewModel anchor, bool after, IEnumerable<ResourceViewModel> resources)
    {
        var ordered = OrderResources(resources);
        var resourceKey = resource.PersistentKey;
        var anchorKey = anchor.PersistentKey;
        // Saved moves don't include a parent key, so a key that's unique among siblings may still be ambiguous during replay.
        if (ordered.Count(r => StringComparers.ResourceName.Equals(r.PersistentKey, resourceKey)) != 1 ||
            ordered.Count(r => StringComparers.ResourceName.Equals(r.PersistentKey, anchorKey)) != 1)
        {
            return ResourceOrderMoveResult.Invalid;
        }

        var names = GetResourceNames(ordered);
        var parent = GetVisibleParent(resource, names);
        var siblingKeys = ordered.Where(r => StringComparer.Ordinal.Equals(GetVisibleParent(r, names), parent)).Select(r => r.PersistentKey);
        var result = CreateMove(resourceKey, anchorKey, after, siblingKeys, out var move);
        if (move is null)
        {
            return result;
        }

        var saved = await _resourceOrder.UpdateAsync(moves =>
        {
            moves.Add(move);
            CompactMoves(moves);
            TrimResourceMoves(moves);
            return SettingUpdateResult.Changed;
        }).ConfigureAwait(false);
        return GetMoveResult(saved);
    }

    internal async Task<ResourceOrderMoveResult> MoveGroupAsync(string grouping, string key, string anchor, bool after, IEnumerable<string> orderedGroupKeys)
    {
        var result = CreateMove(key, anchor, after, orderedGroupKeys, out var move);
        if (move is null)
        {
            return result;
        }

        var saved = await _groupOrder.UpdateAsync(order =>
        {
            if (!order.TryGetValue(grouping, out var moves))
            {
                order.Add(grouping, moves = []);
            }

            moves.Add(move);
            CompactMoves(moves);
            TrimGroupMoves(order);
            return SettingUpdateResult.Changed;
        }).ConfigureAwait(false);
        return GetMoveResult(saved);
    }

    private static ResourceOrderMoveResult GetMoveResult(SettingUpdateResult result) => result switch
    {
        SettingUpdateResult.Changed => ResourceOrderMoveResult.Moved,
        SettingUpdateResult.NotSaved => ResourceOrderMoveResult.NotSaved,
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static HashSet<string> GetResourceNames(IEnumerable<ResourceViewModel> resources) =>
        resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

    private static string? GetVisibleParent(ResourceViewModel resource, HashSet<string> names) =>
        resource.GetResourcePropertyValue(KnownProperties.Resource.ParentName) is { } parent && names.Contains(parent) ? parent : null;

    private static ResourceOrderMoveResult CreateMove(string key, string anchor, bool after, IEnumerable<string> orderedKeys, out RelativeOrderMove? move)
    {
        move = null;
        var keys = orderedKeys.ToList();
        var sourceIndex = keys.FindIndex(item => StringComparers.ResourceName.Equals(item, key));
        // Replica churn can leave two resources with the same key. Don't save a move for an ambiguous row.
        if (sourceIndex < 0 || keys.FindIndex(sourceIndex + 1, item => StringComparers.ResourceName.Equals(item, key)) >= 0)
        {
            return ResourceOrderMoveResult.Invalid;
        }

        keys.RemoveAt(sourceIndex);
        var anchorIndex = keys.FindIndex(item => StringComparers.ResourceName.Equals(item, anchor));
        if (anchorIndex < 0 || keys.FindIndex(anchorIndex + 1, item => StringComparers.ResourceName.Equals(item, anchor)) >= 0)
        {
            return ResourceOrderMoveResult.Invalid;
        }

        // Take the neighbors at the destination, not the neighbors the item is leaving.
        var insertionIndex = anchorIndex + (after ? 1 : 0);
        if (insertionIndex == sourceIndex)
        {
            return ResourceOrderMoveResult.Unchanged;
        }

        move = new RelativeOrderMove(key,
            PreviousKey: insertionIndex > 0 ? keys[insertionIndex - 1] : null,
            NextKey: insertionIndex < keys.Count ? keys[insertionIndex] : null,
            PreferNext: !after);
        return ResourceOrderMoveResult.Moved;
    }

    private static void ReplayMoves<T>(List<T> items, RelativeOrderMove[] moves, Func<T, string> getKey, Func<T, T, bool>? canMove)
    {
        foreach (var move in moves)
        {
            var source = items.FindIndex(item => StringComparers.ResourceName.Equals(getKey(item), move.Key));
            if (source < 0)
            {
                continue;
            }

            var after = !move.PreferNext;
            var anchor = FindAnchor(after ? move.PreviousKey : move.NextKey);
            if (anchor < 0)
            {
                after = !after;
                anchor = FindAnchor(after ? move.PreviousKey : move.NextKey);
                if (anchor < 0)
                {
                    continue;
                }
            }

            var item = items[source];
            items.RemoveAt(source);
            items.Insert(anchor - (source < anchor ? 1 : 0) + (after ? 1 : 0), item);

            int FindAnchor(string? key) => key is null || StringComparers.ResourceName.Equals(key, move.Key) ? -1 : items.FindIndex(candidate =>
                StringComparers.ResourceName.Equals(getKey(candidate), key) && (canMove is null || canMove(items[source], candidate)));
        }
    }

    private static void CompactMoves(List<RelativeOrderMove> moves)
    {
        var movedKeys = new HashSet<string>(StringComparers.ResourceName);
        var neededAnchors = new HashSet<string>(StringComparers.ResourceName);
        for (var i = moves.Count - 1; i >= 0; i--)
        {
            var move = moves[i];
            // A later move replaces this one only if no intervening move needs this position as an anchor.
            var needed = neededAnchors.Remove(move.Key);
            if (!movedKeys.Add(move.Key) && !needed)
            {
                moves.RemoveAt(i);
                continue;
            }

            if (move.PreviousKey is not null)
            {
                neededAnchors.Add(move.PreviousKey);
            }
            if (move.NextKey is not null)
            {
                neededAnchors.Add(move.NextKey);
            }
        }
    }

    private static List<RelativeOrderMove> NormalizeMoves(List<RelativeOrderMove>? moves)
    {
        var normalized = moves?.Where(move => move is { Key: not null } &&
            (move.PreviousKey is not null || move.NextKey is not null) &&
            !StringComparers.ResourceName.Equals(move.Key, move.PreviousKey) &&
            !StringComparers.ResourceName.Equals(move.Key, move.NextKey)).ToList() ?? [];
        CompactMoves(normalized);
        return normalized;
    }

    private static List<RelativeOrderMove> NormalizeResourceOrder(List<RelativeOrderMove>? stored)
    {
        var moves = NormalizeMoves(stored);
        TrimResourceMoves(moves);
        return moves;
    }

    private static Dictionary<string, List<RelativeOrderMove>> NormalizeGroupOrder(Dictionary<string, List<RelativeOrderMove>>? stored)
    {
        var order = stored?.Select(pair => KeyValuePair.Create(pair.Key, NormalizeMoves(pair.Value)))
            .Where(pair => pair.Value.Count > 0).ToDictionary(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        TrimGroupMoves(order);
        return order;
    }

    private static void TrimResourceMoves(List<RelativeOrderMove> moves)
    {
        if (moves.Count > MaxSavedMoves)
        {
            moves.RemoveRange(0, moves.Count - MaxSavedMoves);
        }
    }

    private static void TrimGroupMoves(Dictionary<string, List<RelativeOrderMove>> order)
    {
        var count = order.Values.Sum(moves => moves.Count);
        while (count > MaxSavedMoves)
        {
            // Modes don't share a replay order. Evict the oldest move from the mode with the most saved moves.
            var (grouping, moves) = order.MaxBy(pair => pair.Value.Count);
            moves.RemoveAt(0);
            count--;
            if (moves.Count == 0)
            {
                order.Remove(grouping);
            }
        }
    }

    // Groups maps a grouping to its collapsed keys, like the group order. For example:
    //   { "Tags": ["frontend", ""], "State": ["Running"], "section": ["telemetry"] }
    // An empty key is the untagged group.
    private sealed record CollapsedState(string ApplicationName, LocalStorageSetting<List<string>> ResourceNames,
        LocalStorageSetting<Dictionary<string, List<string>>> Groups);
}

/// <summary>
/// A user move replayed over the default order of the current resources or groups.
/// </summary>
/// <param name="Key">
/// The item to move: a resource's <see cref="ResourceViewModel.PersistentKey"/> or a key within the grouping mode.
/// An empty group key represents the untagged group.
/// </param>
/// <param name="PreviousKey">The neighbor to place the item after. <see langword="null"/> for a drop at the start.</param>
/// <param name="NextKey">The neighbor to place the item before. <see langword="null"/> for a drop at the end.</param>
/// <param name="PreferNext">Whether the next neighbor was the drop target and should be tried first.</param>
internal sealed record RelativeOrderMove(string Key, string? PreviousKey, string? NextKey, bool PreferNext);

internal enum ResourceOrderMoveResult
{
    Invalid,
    Unchanged,
    Moved,
    NotSaved
}
