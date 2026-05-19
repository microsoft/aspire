// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.ResourceMapping;

/// <summary>
/// Maps Aspire resource types to Radius user-defined resource type (UDT) strings.
/// </summary>
/// <remarks>
/// <para>
/// Radius is migrating from built-in portable resource types (<c>Applications.*</c>) to
/// user-defined resource types (UDTs) under the <c>Radius.*</c> namespace. During the transition,
/// types that have a <see cref="RadiusTypeMapping.LegacyFallbackType"/> are emitted using the
/// legacy <c>Applications.*</c> type and API version. Once a Radius release promotes the UDT
/// to stable, the legacy fallback should be removed from the mapping entry.
/// </para>
/// <para>
/// Custom type overrides (via <see cref="RadiusResourceCustomization.TypeOverride"/>) are
/// resolved by <see cref="Publishing.RadiusInfrastructureBuilder.ResolveResourceType"/> before
/// this mapper is consulted.
/// </para>
/// </remarks>
internal sealed class ResourceTypeMapper
{
    private readonly ILogger _logger;

    /// <summary>
    /// Mapping entry containing the new Radius.* type, optional legacy fallback, and API version.
    /// </summary>
    internal readonly record struct RadiusTypeMapping(
        string RadiusType,
        string ApiVersion,
        string? LegacyFallbackType = null,
        string? LegacyApiVersion = null);

    /// <summary>
    /// Maps Aspire resource CLR type names to Radius type information.
    /// Uses full type name to avoid direct assembly references to optional hosting packages (e.g., Dapr).
    /// </summary>
    private static readonly Dictionary<string, RadiusTypeMapping> s_typeMappings = new(StringComparer.Ordinal)
    {
        // Resource types from optional hosting packages - referenced by string name
        // since those packages are not referenced by this project.
        ["RedisResource"] = new(
            RadiusResourceTypes.RedisCaches,
            RadiusResourceTypes.RadiusApiVersion,
            RadiusResourceTypes.LegacyRedisCaches,
            RadiusResourceTypes.LegacyApiVersion),

        ["SqlServerServerResource"] = new(
            RadiusResourceTypes.SqlDatabases,
            RadiusResourceTypes.RadiusApiVersion),

        ["PostgresServerResource"] = new(
            RadiusResourceTypes.PostgreSqlDatabases,
            RadiusResourceTypes.RadiusApiVersion),

        ["MongoDBServerResource"] = new(
            RadiusResourceTypes.MongoDatabases,
            RadiusResourceTypes.RadiusApiVersion,
            RadiusResourceTypes.LegacyMongoDatabases,
            RadiusResourceTypes.LegacyApiVersion),

        ["RabbitMQServerResource"] = new(
            RadiusResourceTypes.RabbitMQQueues,
            RadiusResourceTypes.RadiusApiVersion,
            RadiusResourceTypes.LegacyRabbitMQQueues,
            RadiusResourceTypes.LegacyApiVersion),

        // Core hosting types - these are in the Aspire.Hosting package
        ["ContainerResource"] = new(
            RadiusResourceTypes.Containers,
            RadiusResourceTypes.RadiusApiVersion),

        ["ProjectResource"] = new(
            RadiusResourceTypes.Containers,
            RadiusResourceTypes.RadiusApiVersion),

        // Dapr types
        ["DaprStateStoreResource"] = new(
            RadiusResourceTypes.DaprStateStores,
            RadiusResourceTypes.RadiusApiVersion,
            RadiusResourceTypes.LegacyDaprStateStores,
            RadiusResourceTypes.LegacyApiVersion),

        ["DaprPubSubResource"] = new(
            RadiusResourceTypes.DaprPubSubBrokers,
            RadiusResourceTypes.RadiusApiVersion,
            RadiusResourceTypes.LegacyDaprPubSubBrokers,
            RadiusResourceTypes.LegacyApiVersion),
    };

    private static readonly RadiusTypeMapping s_containerFallback = new(
        RadiusResourceTypes.Containers,
        RadiusResourceTypes.RadiusApiVersion);

    public ResourceTypeMapper(ILogger<ResourceTypeMapper> logger)
        : this((ILogger)logger)
    {
    }

    // Overload for callers that already have a non-generic ILogger (e.g., a pipeline-step
    // logger or a test FakeLogger). Allows mapper diagnostics to flow into the same logger
    // the caller uses, instead of forcing each call site to allocate a LoggerFactory.
    internal ResourceTypeMapper(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Maps an Aspire resource to its Radius resource type string and API version.
    /// </summary>
    /// <remarks>
    /// Uses the new <c>Radius.*</c> namespace when available, falling back to legacy
    /// <c>Applications.*</c> types during the UDT migration. Unmapped resource types
    /// fall back to <c>Radius.Compute/containers</c>.
    /// </remarks>
    /// <param name="resource">The Aspire resource to map.</param>
    /// <returns>A tuple of (resourceType, apiVersion) for the Radius resource.</returns>
    public (string ResourceType, string ApiVersion) MapResource(IResource resource)
    {
        var resourceTypeName = GetMappingKey(resource);

        if (s_typeMappings.TryGetValue(resourceTypeName, out var mapping))
        {
            // If a legacy fallback exists, it means the Radius.* type is not yet fully migrated.
            // Use the legacy type. This is the prototype's expected v1 mapping for several types
            // (e.g. Redis, MongoDB, RabbitMQ, Dapr), so we log at Information rather than Warning
            // to avoid flooding the dashboard / CI logs with per-resource yellow noise. The README
            // documents which Aspire resources are mapped to legacy vs UDT.
            if (mapping.LegacyFallbackType is not null)
            {
                _logger.LogInformation(
                    "Resource '{ResourceName}' mapped to legacy type '{LegacyType}' (API {LegacyApiVersion}). " +
                    "New type '{RadiusType}' is pending migration.",
                    resource.Name,
                    mapping.LegacyFallbackType,
                    mapping.LegacyApiVersion,
                    mapping.RadiusType);

                return (mapping.LegacyFallbackType, mapping.LegacyApiVersion!);
            }

            return (mapping.RadiusType, mapping.ApiVersion);
        }

        // Unmapped type — fallback to Radius.Compute/containers
        _logger.LogWarning(
            "Resource '{ResourceName}' of type '{ResourceType}' has no Radius mapping; " +
            "falling back to '{FallbackType}'.",
            resource.Name,
            resource.GetType().Name,
            s_containerFallback.RadiusType);

        return (s_containerFallback.RadiusType, s_containerFallback.ApiVersion);
    }

    /// <summary>
    /// Gets the key used to look up the type mapping. Walks the type hierarchy to find the
    /// most specific match (e.g., <c>RedisResource</c> inherits from <c>ContainerResource</c>
    /// but should match as Redis).
    /// </summary>
    private static string GetMappingKey(IResource resource)
    {
        var type = resource.GetType();

        // Walk the inheritance chain to find the most specific mapped type
        while (type is not null)
        {
            if (s_typeMappings.ContainsKey(type.Name))
            {
                return type.Name;
            }

            type = type.BaseType;
        }

        return resource.GetType().Name;
    }
}
