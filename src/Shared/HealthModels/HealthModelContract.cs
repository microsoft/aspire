// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aspire.HealthModels;

/// <summary>Serializes and validates the portable v1 health-model contract.</summary>
internal static partial class HealthModelContract
{
    /// <summary>The maximum size of an imported model document in UTF-8 bytes.</summary>
    public const int MaxFileSize = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    // Model and entity identifiers follow the Microsoft.CloudHealth resource naming constraints.
    // Use an absolute end anchor so a trailing newline is not accepted as part of an identifier.
    // https://learn.microsoft.com/azure/templates/microsoft.cloudhealth/2026-05-01-preview/healthmodels/entities
    [GeneratedRegex("\\A[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EntityNamePattern();

    /// <summary>Serializes a model without changing its saved values or collection order.</summary>
    public static string Serialize(HealthModelDocument document) => JsonSerializer.Serialize(document, s_jsonOptions);

    /// <summary>Reads and validates a portable model without requiring a running application.</summary>
    public static HealthModelDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileSize)
        {
            throw new InvalidDataException("The model document exceeds the maximum file size.");
        }

        // A model file has the shape { schemaVersion: 1, name, applicationName, entities: [...],
        // relationships: [{ parentEntityName, childEntityName }] }. Unknown fields are rejected so
        // runtime measurements or unsupported Azure settings cannot be silently discarded on import.
        var document = JsonSerializer.Deserialize<HealthModelDocument>(json, s_jsonOptions)
            ?? throw new InvalidDataException("The model document is null.");
        Validate(document);
        return document;
    }

    /// <summary>Validates intrinsic configuration and the local v1 editor's graph restrictions.</summary>
    public static void Validate(HealthModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.ApplicationName) ||
            document.Entities.IsDefaultOrEmpty || document.Entities.Length > 2000 || document.Relationships.IsDefault ||
            document.Relationships.Length > 10000 || string.IsNullOrEmpty(document.Name) || !EntityNamePattern().IsMatch(document.Name))
        {
            throw new InvalidDataException("The model version, application, name or collection sizes are invalid.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in document.Entities)
        {
            if (entity is null || string.IsNullOrEmpty(entity.Name) || !EntityNamePattern().IsMatch(entity.Name) ||
                !names.Add(entity.Name) || string.IsNullOrWhiteSpace(entity.DisplayName) || entity.DisplayName.Length > 260 ||
                entity.CanvasPosition is null || !IsValidPosition(entity.CanvasPosition) ||
                !Enum.IsDefined(entity.Impact) || entity.Dependencies is null || entity.LocalSignals.IsDefault ||
                entity.LocalSignals.Length > 1000 ||
                entity.Name != document.Name && (string.IsNullOrEmpty(entity.AspireResourceName) || entity.ReplicaIndex is null) ||
                entity.ReplicaIndex is < 0 || entity.HealthObjective is { } objective && (!double.IsFinite(objective) || objective < 0 || objective > 100))
            {
                throw new InvalidDataException("An entity has an invalid name, binding, position, impact or health objective.");
            }

            var signalNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var signal in entity.LocalSignals)
            {
                if (signal is null || string.IsNullOrEmpty(signal.Name) || !Enum.IsDefined(signal.Kind) || !signalNames.Add(signal.Name))
                {
                    throw new InvalidDataException("An entity has an invalid or duplicate local signal binding.");
                }
            }
            ValidateAggregation(entity.Dependencies);
        }

        if (!names.Contains(document.Name) || document.Entities.Single(e => e.Name == document.Name).Impact != EntityImpact.Standard ||
            document.Relationships.Any(r => r is null || r.ChildEntityName == document.Name ||
                !names.Contains(r.ParentEntityName) || !names.Contains(r.ChildEntityName)))
        {
            throw new InvalidDataException("The model must have a standard-impact root with valid relationships and no parent.");
        }

        var incoming = names.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
        var seen = new HashSet<HealthModelRelationship>();
        foreach (var relationship in document.Relationships)
        {
            if (!seen.Add(relationship))
            {
                throw new InvalidDataException("The health model contains a dangling or duplicate relationship.");
            }
            incoming[relationship.ChildEntityName]++;
        }

        // DAG propagation and root reachability are local v1 editor constraints, not Azure restrictions.
        // Traverse the contract directly so importing or publishing does not require dashboard runtime types.
        var children = document.Relationships.ToLookup(r => r.ParentEntityName, r => r.ChildEntityName, StringComparer.Ordinal);
        var ready = new Queue<string>(document.Entities.Where(e => incoming[e.Name] == 0).Select(e => e.Name));
        var reachable = new HashSet<string>(StringComparer.Ordinal) { document.Name };
        var visited = 0;
        while (ready.TryDequeue(out var name))
        {
            visited++;
            foreach (var child in children[name])
            {
                if (reachable.Contains(name))
                {
                    reachable.Add(child);
                }
                if (--incoming[child] == 0)
                {
                    ready.Enqueue(child);
                }
            }
        }

        if (visited != names.Count)
        {
            throw new InvalidDataException("Health model relationships must not contain a cycle.");
        }
        if (reachable.Count != names.Count)
        {
            throw new InvalidDataException("All entities must be reachable from the model root.");
        }
    }

    /// <summary>Checks that canvas coordinates are finite and within the local editor's bounds.</summary>
    public static bool IsValidPosition(HealthModelCanvasPosition position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) &&
        Math.Abs(position.X) <= 1_000_000 && Math.Abs(position.Y) <= 1_000_000;

    /// <summary>Validates dependency aggregation settings independently of the model graph.</summary>
    public static void ValidateAggregation(DependenciesAggregation aggregation)
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        if (!Enum.IsDefined(aggregation.AggregationType) || !Enum.IsDefined(aggregation.Unit))
        {
            throw new InvalidDataException("The dependency aggregation type or unit is invalid.");
        }
        if (aggregation.AggregationType == DependenciesAggregationType.WorstOf)
        {
            if (aggregation.DegradedThreshold is not null || aggregation.UnhealthyThreshold is not null)
            {
                throw new InvalidDataException("Worst-of rollup does not accept thresholds.");
            }
            return;
        }

        if (aggregation.UnhealthyThreshold is not { } unhealthy || !ValidThreshold(unhealthy) ||
            aggregation.DegradedThreshold is { } degraded && (!ValidThreshold(degraded) ||
                (aggregation.AggregationType == DependenciesAggregationType.MinHealthy ? degraded <= unhealthy : degraded >= unhealthy)))
        {
            throw new InvalidDataException("Set an unhealthy threshold and order the thresholds from degraded to unhealthy.");
        }

        bool ValidThreshold(double value) => double.IsFinite(value) && value >= 0 &&
            (aggregation.Unit == AggregationUnit.Percentage ? value <= 100 : value == Math.Truncate(value));
    }
}
