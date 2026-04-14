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
    /// </summary>
    [Obsolete("Legacy API version for portable resource types being replaced by UDTs. Use RadiusApiVersion instead.")]
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
    // in the Radius.* namespace. See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-02-user-defined-resource-type-feature-spec.md

    [Obsolete("Portable resource types are being replaced by Radius.* user-defined types. Use RadiusResourceTypes.RedisCaches instead.")]
    public const string LegacyRedisCaches = "Applications.Datastores/redisCaches";

    [Obsolete("Portable resource types are being replaced by Radius.* user-defined types. Use RadiusResourceTypes.MongoDatabases instead.")]
    public const string LegacyMongoDatabases = "Applications.Datastores/mongoDatabases";

    [Obsolete("Portable resource types are being replaced by Radius.* user-defined types. Use RadiusResourceTypes.RabbitMQQueues instead.")]
    public const string LegacyRabbitMQQueues = "Applications.Messaging/rabbitMQQueues";

    [Obsolete("Portable resource types are being replaced by Radius.* user-defined types. Use RadiusResourceTypes.DaprStateStores instead.")]
    public const string LegacyDaprStateStores = "Applications.Dapr/stateStores";

    [Obsolete("Portable resource types are being replaced by Radius.* user-defined types. Use RadiusResourceTypes.DaprPubSubBrokers instead.")]
    public const string LegacyDaprPubSubBrokers = "Applications.Dapr/pubSubBrokers";
}
