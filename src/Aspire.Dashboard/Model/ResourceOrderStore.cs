// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores resource and group positions in the application settings database, independently of individual runs.
/// </summary>
public sealed class ResourceOrderStore
{
    private const string SettingsName = "resource and group order";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _positions = new(StringComparers.ResourceName);
    private readonly Dictionary<string, Dictionary<string, long>> _groupPositions = new(StringComparer.Ordinal);
    private readonly DashboardSettingsDatabase _database;
    private bool _loaded;
    private bool _hasCustomResourceOrder;

    public ResourceOrderStore(IOptions<DashboardOptions> options, ILogger<ResourceOrderStore> logger)
        : this(DashboardSettingsDatabase.GetDatabasePath(options.Value), logger)
    {
    }

    internal ResourceOrderStore(string? databasePath, ILogger logger)
    {
        _database = new DashboardSettingsDatabase(databasePath, logger);
    }

    public event Action? Changed;

    /// <summary>
    /// Gets the stored resource positions, shared by every run.
    /// </summary>
    public IReadOnlyDictionary<string, long> GetPositions() => GetPositions(grouping: null);

    /// <summary>
    /// Gets the positions to display the given run's resources with. AppHost display orders apply until the user reorders.
    /// </summary>
    /// <remarks>
    /// Display orders declared by the AppHost aren't stored: they come with the resources of a run, and a circuit can
    /// switch between the live run and recordings. They're provided by the caller so opening a recording can't change
    /// the order of the live run.
    /// </remarks>
    public IReadOnlyDictionary<string, long> GetPositions(IEnumerable<ResourceViewModel> resources)
    {
        var displayOrders = GetDisplayOrders(resources);
        lock (_lock)
        {
            EnsureLoaded();
            return GetEffectiveResourcePositions(displayOrders);
        }
    }

    public IReadOnlyDictionary<string, long> GetGroupPositions(string grouping) => GetPositions(grouping);

    private IReadOnlyDictionary<string, long> GetPositions(string? grouping)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return new Dictionary<string, long>(GetScopePositions(grouping), StringComparers.ResourceName);
        }
    }

    // Keys must be ResourceViewModel.PersistentKey, not run-specific resource names. Keep positions for absent
    // resources so restarting a run, temporarily removing a resource, or changing its state doesn't reset its order.
    public void EnsureResources(IEnumerable<string> resourceKeys) => EnsureItems(grouping: null, resourceKeys);

    public void EnsureGroups(string grouping, IEnumerable<string> groupKeys) => EnsureItems(grouping, groupKeys);

    private void EnsureItems(string? grouping, IEnumerable<string> keys)
    {
        var changed = false;
        lock (_lock)
        {
            EnsureLoaded();
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
                Save(grouping, positions, isUserMove: false);
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public bool TryMove(string resourceKey, string targetKey, bool after) => TryMove(grouping: null, resourceKey, targetKey, after, [], []);

    /// <summary>
    /// Moves a resource within the order the given run's resources are displayed in, which becomes the stored order.
    /// </summary>
    public bool TryMove(string resourceKey, string targetKey, bool after, IEnumerable<ResourceViewModel> resources)
    {
        var snapshot = resources.ToList();

        // Resources can share a position, for example the same AppHost display order. Those are displayed by name, so
        // the move needs the same tie-breaker or the moved resource lands somewhere other than where it was dropped.
        var nameRanks = new Dictionary<string, int>(StringComparers.ResourceName);
        foreach (var resource in snapshot.OrderBy(r => r, ResourceViewModelNameComparer.Instance))
        {
            nameRanks.TryAdd(resource.PersistentKey, nameRanks.Count);
        }

        return TryMove(grouping: null, resourceKey, targetKey, after, GetDisplayOrders(snapshot), nameRanks);
    }

    public bool TryMoveGroup(string grouping, string groupKey, string targetKey, bool after) => TryMove(grouping, groupKey, targetKey, after, [], []);

    private bool TryMove(string? grouping, string resourceKey, string targetKey, bool after, Dictionary<string, int> displayOrders, Dictionary<string, int> nameRanks)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var currentPositions = GetScopePositions(grouping);
            if (!currentPositions.ContainsKey(resourceKey) || !currentPositions.ContainsKey(targetKey))
            {
                throw new ArgumentException("Both items must have an assigned position.");
            }
            if (StringComparers.ResourceName.Equals(resourceKey, targetKey))
            {
                return true;
            }

            var effectivePositions = grouping is null ? GetEffectiveResourcePositions(displayOrders) : currentPositions;
            var orderedKeys = effectivePositions
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => nameRanks.GetValueOrDefault(pair.Key, int.MaxValue))
                .Select(pair => pair.Key)
                .ToList();
            orderedKeys.RemoveAll(key => StringComparers.ResourceName.Equals(key, resourceKey));
            var targetIndex = orderedKeys.FindIndex(key => StringComparers.ResourceName.Equals(key, targetKey));
            orderedKeys.Insert(targetIndex + (after ? 1 : 0), resourceKey);
            var positions = orderedKeys.Select((key, index) => (key, position: (long)index))
                .ToDictionary(pair => pair.key, pair => pair.position, StringComparers.ResourceName);
            if (!Save(grouping, positions, isUserMove: true))
            {
                return false;
            }
            currentPositions.Clear();
            foreach (var pair in positions)
            {
                currentPositions.Add(pair.Key, pair.Value);
            }
            if (grouping is null)
            {
                _hasCustomResourceOrder = true;
            }
        }
        Changed?.Invoke();
        return true;
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

    private static Dictionary<string, int> GetDisplayOrders(IEnumerable<ResourceViewModel> resources)
    {
        var orders = new Dictionary<string, int>(StringComparers.ResourceName);
        foreach (var resource in resources)
        {
            // An explicit 0 is the same as no order. Recording it would give the resource a shared, name-sorted
            // position ahead of resources without an order, so merely adding WithDisplayOrder(0) would move it.
            if (resource.DisplayOrder is { } order and not 0)
            {
                orders[resource.PersistentKey] = order;
            }
        }

        return orders;
    }

    private Dictionary<string, long> GetEffectiveResourcePositions(Dictionary<string, int> displayOrders)
    {
        if (_hasCustomResourceOrder || displayOrders.Count == 0)
        {
            return new Dictionary<string, long>(_positions, StringComparers.ResourceName);
        }

        // Keep declared display orders separate from stored order: a new AppHost value must take
        // effect until the user explicitly reorders, including after restarting the dashboard.
        //
        // Display orders are relative sort keys like ResourceUrlAnnotation.DisplayOrder: higher values
        // come first and resources without one count as 0. Resources declaring the same nonzero order
        // share a position so they're displayed by name; resources at 0 keep their stored order, so
        // declaring an order on one resource doesn't reshuffle all the others.
        var positions = new Dictionary<string, long>(StringComparers.ResourceName);
        var nextPosition = 0L;
        var storedOrder = _positions.OrderBy(pair => pair.Value).Select(pair => pair.Key);
        var groups = storedOrder.GroupBy(key => displayOrders.GetValueOrDefault(key)).OrderByDescending(group => group.Key);
        foreach (var group in groups)
        {
            var hasDeclaredOrder = false;
            foreach (var key in group.Where(displayOrders.ContainsKey))
            {
                positions[key] = nextPosition;
                hasDeclaredOrder = true;
            }
            if (hasDeclaredOrder)
            {
                nextPosition++;
            }
            foreach (var key in group.Where(key => !displayOrders.ContainsKey(key)))
            {
                positions[key] = nextPosition++;
            }
        }

        return positions;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;

        _database.Load(SettingsName, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS resource_order (
                    resource_key TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    position INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS resource_group_order (
                    grouping TEXT NOT NULL,
                    group_key TEXT NOT NULL COLLATE NOCASE,
                    position INTEGER NOT NULL,
                    PRIMARY KEY (grouping, group_key)
                );
                CREATE TABLE IF NOT EXISTS resource_order_settings (
                    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                    custom_order INTEGER NOT NULL
                );
                SELECT resource_key, position FROM resource_order ORDER BY position;
                """;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    _positions.Add(reader.GetString(0), reader.GetInt64(1));
                }
            }

            command.CommandText = "SELECT custom_order FROM resource_order_settings WHERE id = 1;";
            var customOrder = command.ExecuteScalar();
            // Older settings do not record whether order was inferred or customized; preserve
            // their existing order rather than risk undoing a user's drag-and-drop customization.
            _hasCustomResourceOrder = customOrder is null ? _positions.Count > 0 : Convert.ToBoolean(customOrder, CultureInfo.InvariantCulture);
            command.CommandText = "INSERT OR IGNORE INTO resource_order_settings (id, custom_order) VALUES (1, $customOrder);";
            command.Parameters.AddWithValue("$customOrder", _hasCustomResourceOrder);
            command.ExecuteNonQuery();
            command.Parameters.Clear();

            command.CommandText = "SELECT grouping, group_key, position FROM resource_group_order ORDER BY grouping, position;";
            using var groupReader = command.ExecuteReader();
            while (groupReader.Read())
            {
                GetScopePositions(groupReader.GetString(0)).Add(groupReader.GetString(1), groupReader.GetInt64(2));
            }
        });
    }

    private bool Save(string? grouping, IReadOnlyDictionary<string, long> positions, bool isUserMove)
    {
        return _database.TrySave(SettingsName, connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = grouping is null
                ? "INSERT INTO resource_order (resource_key, position) VALUES ($key, $position) ON CONFLICT(resource_key) DO UPDATE SET position = excluded.position;"
                : "INSERT INTO resource_group_order (grouping, group_key, position) VALUES ($grouping, $key, $position) ON CONFLICT(grouping, group_key) DO UPDATE SET position = excluded.position;";
            if (grouping is not null)
            {
                command.Parameters.AddWithValue("$grouping", grouping);
            }
            var keyParameter = command.Parameters.Add("$key", SqliteType.Text);
            var positionParameter = command.Parameters.Add("$position", SqliteType.Integer);
            foreach (var pair in positions)
            {
                keyParameter.Value = pair.Key;
                positionParameter.Value = pair.Value;
                command.ExecuteNonQuery();
            }
            if (grouping is null && isUserMove)
            {
                command.Parameters.Clear();
                command.CommandText = "UPDATE resource_order_settings SET custom_order = 1 WHERE id = 1;";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        });
    }
}
