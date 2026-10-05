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

    /// <summary>
    /// The SQL Server UDT registered by <c>resource-types-contrib</c>. Note the name: there is no
    /// <c>Radius.Data/sqlDatabases</c> UDT — that spelling belongs to the legacy
    /// <see cref="LegacySqlDatabases"/> portable type, and emitting it as a <c>Radius.*</c> type
    /// fails type resolution at deploy time.
    /// See <see href="https://github.com/radius-project/resource-types-contrib/blob/main/Data/sqlServerDatabases/sqlServerDatabases.yaml"/>.
    /// </summary>
    /// <remarks>
    /// Not emitted as of Radius 0.60: the type ships in the Bicep extension, but no Kubernetes
    /// recipe is published for it (<c>kube-recipes/sqlserverdatabases</c> does not exist), so the
    /// mapper still emits <see cref="LegacySqlDatabases"/>. The blocker is the missing recipe, not
    /// a missing type.
    /// </remarks>
    public const string SqlServerDatabases = "Radius.Data/sqlServerDatabases";

    public const string PostgreSqlDatabases = "Radius.Data/postgreSqlDatabases";

    /// <summary>
    /// The MongoDB UDT registered by <c>resource-types-contrib</c>.
    /// </summary>
    /// <remarks>
    /// Not emitted as of Radius 0.60: the type ships in the Bicep extension, but no Kubernetes
    /// recipe is published for it (<c>kube-recipes/mongodatabases</c> does not exist), so the
    /// mapper still emits <see cref="LegacyMongoDatabases"/>. The blocker is the missing recipe,
    /// not a missing type.
    /// </remarks>
    public const string MongoDatabases = "Radius.Data/mongoDatabases";

    // --- Radius.Messaging ---

    /// <summary>
    /// The RabbitMQ UDT. Note the name: Radius 0.60 spells this <c>rabbitMQ</c>, not
    /// <c>rabbitMQQueues</c> — the latter is the legacy portable type
    /// (<see cref="LegacyRabbitMQQueues"/>) and emitting it under the <c>Radius.*</c> namespace
    /// fails type resolution at deploy time.
    /// </summary>
    public const string RabbitMQ = "Radius.Messaging/rabbitMQ";

    // --- Radius.Security ---

    /// <summary>
    /// The <c>Radius.*</c> UDT replacement for <c>Applications.Core/secretStores</c>. Used to hold
    /// credentials that a UDT backing resource consumes by resource ID.
    /// </summary>
    public const string SecuritySecrets = "Radius.Security/secrets";

    // Deliberately absent: a Radius.Dapr/* namespace. No such namespace exists in Radius 0.60;
    // Dapr building blocks are still modelled by the legacy Applications.Dapr/* portable types
    // below, which is what ResourceTypeMapper emits for them.

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

    // LegacyRedisCaches and LegacyRabbitMQQueues are no longer emitted: Radius 0.60 ships UDT
    // equivalents with published Kubernetes recipes, so ResourceTypeMapper maps Redis and RabbitMQ
    // straight to Radius.Data/redisCaches and Radius.Messaging/rabbitMQ with no legacy fallback.
    // They are kept deliberately because the pre-0.60 spellings still appear in artifacts and docs
    // in the wild, and because LegacyRabbitMQQueues is what distinguishes the legacy portable type
    // from the UDT in the RabbitMQ remarks above — a distinction that fails type resolution at
    // deploy time when it is confused. Neither has a row in RadiusBackingConnections.s_schemas,
    // which ConnectionSchemaTable_DescribesOnlyEmittedBackingTypes enforces.
    public const string LegacyRedisCaches = "Applications.Datastores/redisCaches";

    public const string LegacyMongoDatabases = "Applications.Datastores/mongoDatabases";

    public const string LegacySqlDatabases = "Applications.Datastores/sqlDatabases";

    public const string LegacyRabbitMQQueues = "Applications.Messaging/rabbitMQQueues";

    public const string LegacyDaprStateStores = "Applications.Dapr/stateStores";

    public const string LegacyDaprPubSubBrokers = "Applications.Dapr/pubSubBrokers";
}
