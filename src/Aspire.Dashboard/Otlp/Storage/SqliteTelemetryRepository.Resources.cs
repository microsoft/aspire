// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Text;
using Aspire.Dashboard.Otlp.Model;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    public List<OtlpResource> GetResources(bool includeUninstrumentedPeers = false) => GetTelemetryResources(includeUninstrumentedPeers, name: null);

    public List<OtlpResource> GetResourcesByName(string name, bool includeUninstrumentedPeers = false) => GetTelemetryResources(includeUninstrumentedPeers, name);

    public OtlpResource? GetResourceByCompositeName(string compositeName) => GetResources(includeUninstrumentedPeers: true).SingleOrDefault(resource => resource.ResourceKey.EqualsCompositeName(compositeName));

    public OtlpResource? GetResource(ResourceKey key) => GetResources(includeUninstrumentedPeers: true).SingleOrDefault(resource => resource.ResourceKey == key);

    public List<OtlpResource> GetResources(ResourceKey key, bool includeUninstrumentedPeers = false)
    {
        return key.InstanceId is null
            ? GetResourcesByName(key.Name, includeUninstrumentedPeers)
            : GetResources(includeUninstrumentedPeers).Where(resource => resource.ResourceKey == key).ToList();
    }

    private void DeleteTelemetryResourceFromDatabase(ResourceKey resourceKey)
    {
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var sql = new StringBuilder("""
                DELETE FROM telemetry_resources
                WHERE resource_name = @ResourceName COLLATE NOCASE
                """);
            var parameters = new DynamicParameters();
            parameters.Add("ResourceName", resourceKey.Name);
            if (resourceKey.InstanceId is not null)
            {
                sql.Append(" AND instance_id = @InstanceId COLLATE NOCASE");
                parameters.Add("InstanceId", resourceKey.InstanceId);
            }
            sql.Append(';');
            // A trace can contain spans from multiple resources. Capture affected trace IDs before
            // resource deletion cascades its spans, then delete each complete trace to avoid retaining partial trace data.
            var affectedTraceIds = connection.Query<string>($"""
                SELECT DISTINCT spans.trace_id
                FROM telemetry_spans spans
                JOIN telemetry_resources resources ON resources.resource_id = spans.resource_id
                WHERE resources.resource_name = @ResourceName COLLATE NOCASE
                  {(resourceKey.InstanceId is null ? string.Empty : "AND resources.instance_id = @InstanceId COLLATE NOCASE")};
                """, parameters, transaction).AsList();
            connection.Execute(sql.ToString(), parameters, transaction);
            foreach (var traceBatch in affectedTraceIds.Chunk(MaxTraceBatchSize))
            {
                connection.Execute(
                    "DELETE FROM telemetry_traces WHERE trace_id IN @TraceIds;",
                    new { TraceIds = traceBatch },
                    transaction);
            }
            connection.Execute("""
                UPDATE telemetry_resources
                SET has_traces = EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.resource_id = telemetry_resources.resource_id);
                """, transaction: transaction);
            DeleteOrphanedScopes(connection, transaction);
            transaction.Commit();
            ClearIngestionCaches();
        }
    }

    private static void DeleteOrphanedScopes(SqliteConnection connection, IDbTransaction transaction)
    {
        connection.Execute("""
            DELETE FROM telemetry_scopes
            WHERE NOT EXISTS (SELECT 1 FROM telemetry_logs WHERE telemetry_logs.scope_id = telemetry_scopes.scope_id)
              AND NOT EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.scope_id = telemetry_scopes.scope_id)
              AND NOT EXISTS (SELECT 1 FROM telemetry_metric_instruments WHERE telemetry_metric_instruments.scope_id = telemetry_scopes.scope_id);
            """, transaction: transaction);
    }

    private List<OtlpResource> GetTelemetryResources(bool includeUninstrumentedPeers, string? name)
    {
        EnsureCachePopulated();
        lock (_cacheLock)
        {
            return _cachedResourcesByKey.Values
                .Select(resource => resource.Resource)
                .Where(resource => includeUninstrumentedPeers || !resource.UninstrumentedPeer)
                .Where(resource => name is null || string.Equals(resource.ResourceName, name, StringComparisons.ResourceName))
                .OrderBy(resource => resource.ResourceKey)
                .ToList();
        }
    }

    private sealed class ResourceViewPropertiesComparer : IEqualityComparer<KeyValuePair<string, string>[]>
    {
        public static readonly ResourceViewPropertiesComparer Instance = new();

        public bool Equals(KeyValuePair<string, string>[]? left, KeyValuePair<string, string>[]? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.SequenceEqual(right);

        public int GetHashCode(KeyValuePair<string, string>[] properties)
        {
            var hash = new HashCode();
            foreach (var property in properties)
            {
                hash.Add(property);
            }
            return hash.ToHashCode();
        }
    }

    private class TelemetryResourceRecord
    {
        public required string ResourceName { get; init; }
        public string? InstanceId { get; init; }
    }
}