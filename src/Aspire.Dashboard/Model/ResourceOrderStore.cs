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
    // At 50 moves, 64-character ASCII resource names with both anchors fit under SignalR's 32 KiB receive limit.
    // https://github.com/dotnet/aspnetcore/blob/ae89581dfd24c6277b4267a9779d05f7b9f047f1/src/SignalR/server/Core/src/HubOptionsSetup.cs#L19-L23
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

    internal ResourceOrderMoveResult MoveResource(ResourceViewModel resource, ResourceViewModel anchor, bool after, IEnumerable<ResourceViewModel> resources)
    {
        var ordered = OrderResources(resources);
        var names = GetResourceNames(ordered);
        var parent = GetVisibleParent(resource, names);
        var siblingKeys = ordered.Where(r => StringComparer.Ordinal.Equals(GetVisibleParent(r, names), parent)).Select(r => r.PersistentKey);
        var result = CreateMove(resource.PersistentKey, anchor.PersistentKey, after, siblingKeys, out var move);
        if (move is null)
        {
            return result;
        }

        return _resourceOrder.Update(moves =>
        {
            if (!ReplaceMove(moves, move))
            {
                return false;
            }

            TrimResourceMoves(moves);
            return true;
        }) ? ResourceOrderMoveResult.Moved : ResourceOrderMoveResult.Unchanged;
    }

    internal ResourceOrderMoveResult MoveGroup(string grouping, string key, string anchor, bool after, IEnumerable<string> orderedGroupKeys)
    {
        var result = CreateMove(key, anchor, after, orderedGroupKeys, out var move);
        if (move is null)
        {
            return result;
        }

        return _groupOrder.Update(order =>
        {
            if (!order.TryGetValue(grouping, out var moves))
            {
                order.Add(grouping, moves = []);
            }

            if (!ReplaceMove(moves, move))
            {
                return false;
            }

            TrimGroupMoves(order);
            return true;
        }) ? ResourceOrderMoveResult.Moved : ResourceOrderMoveResult.Unchanged;
    }

    private static HashSet<string> GetResourceNames(IEnumerable<ResourceViewModel> resources) =>
        resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

    private static string? GetVisibleParent(ResourceViewModel resource, HashSet<string> names) =>
        resource.GetResourcePropertyValue(KnownProperties.Resource.ParentName) is { } parent && names.Contains(parent) ? parent : null;

    private static ResourceOrderMoveResult CreateMove(string key, string anchor, bool after, IEnumerable<string> orderedKeys, out RelativeOrderMove? move)
    {
        move = null;
        var keys = orderedKeys.ToList();
        var sourceIndex = keys.FindIndex(item => StringComparers.ResourceName.Equals(item, key));
        if (sourceIndex < 0)
        {
            return ResourceOrderMoveResult.Invalid;
        }

        keys.RemoveAt(sourceIndex);
        var anchorIndex = keys.FindIndex(item => StringComparers.ResourceName.Equals(item, anchor));
        if (anchorIndex < 0)
        {
            return ResourceOrderMoveResult.Invalid;
        }

        // Take the neighbors at the destination, not the neighbors the item is leaving.
        var insertionIndex = anchorIndex + (after ? 1 : 0);
        if (insertionIndex == sourceIndex)
        {
            return ResourceOrderMoveResult.Unchanged;
        }

        move = new RelativeOrderMove(Guid.NewGuid(), key,
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

            int FindAnchor(string? key) => key is null ? -1 : items.FindIndex(candidate =>
                StringComparers.ResourceName.Equals(getKey(candidate), key) && (canMove is null || canMove(items[source], candidate)));
        }
    }

    private static bool ReplaceMove(List<RelativeOrderMove> moves, RelativeOrderMove move)
    {
        // A load can read back a move before the writer has removed it from the in-memory queue.
        if (moves.Exists(existing => existing.Id == move.Id))
        {
            return false;
        }

        moves.RemoveAll(existing => StringComparers.ResourceName.Equals(existing.Key, move.Key));
        moves.Add(move);
        return true;
    }

    private static List<RelativeOrderMove> NormalizeMoves(List<RelativeOrderMove>? moves)
    {
        var ids = new HashSet<Guid>();
        return moves?.Where(move => move is { Key: not null } && move.Id != Guid.Empty &&
            (move.PreviousKey is not null || move.NextKey is not null) &&
            !StringComparers.ResourceName.Equals(move.Key, move.PreviousKey) &&
            !StringComparers.ResourceName.Equals(move.Key, move.NextKey) && ids.Add(move.Id))
            .Reverse().DistinctBy(move => move.Key, StringComparers.ResourceName).Reverse().ToList() ?? [];
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
/// <param name="Id">Identifies the move, not the item, so the same move is applied only once.</param>
/// <param name="Key">
/// The item to move: a resource's <see cref="ResourceViewModel.PersistentKey"/> or a key within the grouping mode.
/// An empty group key represents the untagged group.
/// </param>
/// <param name="PreviousKey">The neighbor to place the item after. <see langword="null"/> for a drop at the start.</param>
/// <param name="NextKey">The neighbor to place the item before. <see langword="null"/> for a drop at the end.</param>
/// <param name="PreferNext">Whether the next neighbor was the drop target and should be tried first.</param>
internal sealed record RelativeOrderMove(Guid Id, string Key, string? PreviousKey, string? NextKey, bool PreferNext);

internal enum ResourceOrderMoveResult
{
    Invalid,
    Unchanged,
    Moved
}
