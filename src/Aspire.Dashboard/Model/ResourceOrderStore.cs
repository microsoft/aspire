// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores explicit resource and group moves per application in protected browser local storage.
/// </summary>
public sealed class ResourceOrderStore
{
    // Bound the saved history so typical protected values fit within the dashboard's Blazor receive limit.
    private const int MaxSavedMoves = 50;

    private readonly LocalStorageSetting<List<RelativeOrderMove>> _resourceOrder;
    private readonly LocalStorageSetting<Dictionary<string, List<RelativeOrderMove>>> _groupOrder;

    public ResourceOrderStore(ILocalStorage localStorage, LocalStorageLocks locks, IOptions<DashboardOptions> options, ILogger<ResourceOrderStore> logger)
    {
        var applicationName = options.Value.GetApplicationNameOrDefault();
        _resourceOrder = new(localStorage, locks, BrowserStorageKeys.ResourceOrderKey(applicationName), logger, protect: true,
            NormalizeResourceOrder, (x, y) => x.SequenceEqual(y), () => Changed?.Invoke());
        _groupOrder = new(localStorage, locks, BrowserStorageKeys.GroupOrderKey(applicationName), logger, protect: true,
            NormalizeGroupOrder, (x, y) => x.Count == y.Count && x.All(pair => y.TryGetValue(pair.Key, out var moves) && pair.Value.SequenceEqual(moves)),
            () => Changed?.Invoke());
    }

    public event Action? Changed;

    public Task EnsureLoadedAsync() => Task.WhenAll(_resourceOrder.EnsureLoadedAsync(), _groupOrder.EnsureLoadedAsync());

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
