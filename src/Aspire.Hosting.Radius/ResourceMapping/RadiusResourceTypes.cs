// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.ResourceMapping;

/// <summary>
/// Constants for Radius and legacy Applications resource type strings and API versions.
/// </summary>
internal static class RadiusResourceTypes
{
    // --- API Versions ---

    /// <summary>
    /// API version for new Radius.* namespace resource types.
    /// </summary>
    public const string RadiusApiVersion = "2025-08-01-preview";

    /// <summary>
    /// API version for legacy Applications.* namespace resource types.
    /// Will be removed once portable resource types are removed from Radius
    /// and the mapper switches the remaining legacy entries to Radius.* UDTs.
    /// </summary>
    public const string LegacyApiVersion = "2023-10-01-preview";

    // --- Radius.Core ---

    public const string Environments = "Radius.Core/environments";
    public const string Applications = "Radius.Core/applications";
    public const string RecipePacks = "Radius.Core/recipePacks";

    // --- Radius.Compute ---

    public const string Containers = "Radius.Compute/containers";

    // --- Radius.Data ---

    public const string RedisCaches = "Radius.Data/redisCaches";
    public const string SqlDatabases = "Radius.Data/sqlDatabases";
    public const string PostgreSqlDatabases = "Radius.Data/postgreSqlDatabases";
    public const string MongoDatabases = "Radius.Data/mongoDatabases";

    // --- Radius.Messaging ---

    public const string RabbitMQQueues = "Radius.Messaging/rabbitMQQueues";

    // --- Radius.Dapr ---

    public const string DaprStateStores = "Radius.Dapr/stateStores";
    public const string DaprPubSubBrokers = "Radius.Dapr/pubSubBrokers";

    // --- Legacy Applications.* fallback types ---
    // These portable resource types are being replaced by user-defined types (UDTs)
    // in the Radius.* namespace. The corresponding Radius.* constants above should be
    // used once the UDT equivalents are available in the target Radius release.
    // See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-02-user-defined-resource-type-feature-spec.md
    //
    // These constants intentionally do not carry [Obsolete] attributes — the package
    // is in preview, the constants are internal, and the mapper still emits these
    // values as fallbacks. The constants (and their callsites in ResourceTypeMapper)
    // will be removed in the same change that migrates the mapper to the Radius.*
    // UDT equivalents.

    public const string LegacyApplications = "Applications.Core/applications";

    public const string LegacyEnvironments = "Applications.Core/environments";

    public const string LegacyRedisCaches = "Applications.Datastores/redisCaches";

    public const string LegacyMongoDatabases = "Applications.Datastores/mongoDatabases";

    public const string LegacyRabbitMQQueues = "Applications.Messaging/rabbitMQQueues";

    public const string LegacyDaprStateStores = "Applications.Dapr/stateStores";

    public const string LegacyDaprPubSubBrokers = "Applications.Dapr/pubSubBrokers";
}
