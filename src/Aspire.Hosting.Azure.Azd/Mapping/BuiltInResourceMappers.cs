// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Provides the built-in <see cref="IAzdResourceMapper"/> set used to translate azd <c>resources</c>
/// entries into Aspire Azure resources.
/// </summary>
/// <remarks>
/// The mappers cover the resource types most commonly found in azd templates. Each mapper creates the
/// top-level Azure resource and, where the azd entry declares them, the nested resources (databases,
/// queues, topics, blob containers, event hubs, or model deployments). Unsupported types fall through
/// so the importer can report a diagnostic rather than mapping them incorrectly.
/// </remarks>
internal static class BuiltInResourceMappers
{
    public static IReadOnlyList<IAzdResourceMapper> Create() =>
    [
        // Secrets / configuration.
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "keyvault"),
            ctx => ctx.Builder.AddAzureKeyVault(ctx.ResourceName).AsGeneric()),

        // Storage account (+ blob containers).
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "storage"),
            ctx =>
            {
                var storage = ctx.Builder.AddAzureStorage(ctx.ResourceName);
                foreach (var container in ExtractNames(ctx.Resource.Properties, "containers", "blobContainers"))
                {
                    storage.AddBlobContainer(Sanitize($"{ctx.ResourceName}-{container}"), container);
                }

                return storage.AsGeneric();
            }),

        // Redis cache. azd's `db.redis` provisions Azure Cache for Redis (Microsoft.Cache/redis), which
        // Azure is retiring (https://learn.microsoft.com/azure/azure-cache-for-redis/retirement-faq). For
        // the same reason Aspire's AddAzureRedis (Azure Cache for Redis) is obsolete, so the importer maps
        // db.redis to Azure Managed Redis (Microsoft.Cache/redisEnterprise) via AddAzureManagedRedis, the
        // recommended successor. That is a deliberate SKU substitution, not a faithful 1:1 mapping, so it
        // is surfaced as a warning: a customer with an existing Azure Cache for Redis instance should
        // confirm the successor SKU fits before deploying (or register a custom mapper to override it).
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "db.redis"),
            ctx =>
            {
                ctx.Diagnostics.Warning(
                    $"azd resource '{ctx.ResourceName}' is type 'db.redis' (Azure Cache for Redis, which Azure is retiring). " +
                    "Aspire maps it to Azure Managed Redis, its supported successor, via AddAzureManagedRedis. " +
                    "Confirm this SKU change is acceptable for your migration, or register a custom IAzdResourceMapper to override it.",
                    ctx.ResourceName);
                return ctx.Builder.AddAzureManagedRedis(ctx.ResourceName).AsGeneric();
            }),

        // PostgreSQL flexible server. azd's generic database resources do not list databases inline;
        // the resource provisions a single database named after the resource itself. (azd's
        // db.postgres / db.mysql / db.redis / db.mongo all share a props-less ResourceConfig.)
        // https://github.com/Azure/azure-dev/blob/main/cli/azd/pkg/project/resources.go
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "db.postgres"),
            ctx =>
            {
                var postgres = ctx.Builder.AddAzurePostgresFlexibleServer(ctx.ResourceName);
                postgres.AddDatabase(Sanitize($"{ctx.ResourceName}-db"), ctx.ResourceName);
                return postgres.AsGeneric();
            }),

        // Cosmos DB (NoSQL). azd declares containers directly (CosmosDBProps.Containers); they live
        // under an implicit database named after the resource.
        // https://github.com/Azure/azure-dev/blob/main/cli/azd/pkg/project/resources.go
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "db.cosmos"),
            ctx =>
            {
                var cosmos = ctx.Builder.AddAzureCosmosDB(ctx.ResourceName);
                var database = cosmos.AddCosmosDatabase(Sanitize($"{ctx.ResourceName}-db"), ctx.ResourceName);
                foreach (var (name, partitionKeys) in ExtractContainers(ctx.Resource.Properties))
                {
                    database.AddContainer(Sanitize($"{ctx.ResourceName}-{name}"), partitionKeys, name);
                }

                return cosmos.AsGeneric();
            }),

        // Service Bus namespace (+ queues and topics).
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "messaging.servicebus"),
            ctx =>
            {
                var serviceBus = ctx.Builder.AddAzureServiceBus(ctx.ResourceName);
                foreach (var queue in ExtractNames(ctx.Resource.Properties, "queues"))
                {
                    serviceBus.AddServiceBusQueue(Sanitize($"{ctx.ResourceName}-{queue}"), queue);
                }

                foreach (var topic in ExtractNames(ctx.Resource.Properties, "topics"))
                {
                    serviceBus.AddServiceBusTopic(Sanitize($"{ctx.ResourceName}-{topic}"), topic);
                }

                return serviceBus.AsGeneric();
            }),

        // Event Hubs namespace (+ hubs).
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "messaging.eventhubs"),
            ctx =>
            {
                var eventHubs = ctx.Builder.AddAzureEventHubs(ctx.ResourceName);
                foreach (var hub in ExtractNames(ctx.Resource.Properties, "hubs", "eventHubs"))
                {
                    eventHubs.AddHub(Sanitize($"{ctx.ResourceName}-{hub}"), hub);
                }

                return eventHubs.AsGeneric();
            }),

        // AI Search.
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "ai.search"),
            ctx => ctx.Builder.AddAzureSearch(ctx.ResourceName).AsGeneric()),

        // Azure OpenAI account (+ a model deployment).
        new DelegateResourceMapper(
            ctx => MatchesType(ctx, "ai.openai.model", "ai.openai"),
            ctx =>
            {
                var openai = ctx.Builder.AddAzureOpenAI(ctx.ResourceName);

                // azd describes the model either via a nested `model: { name, version }` map or via
                // flat `model`/`version` scalars. Be tolerant of both shapes.
                var modelName = GetNestedString(ctx.Resource.Properties, "model", "name")
                    ?? GetString(ctx.Resource.Properties, "model");
                var modelVersion = GetNestedString(ctx.Resource.Properties, "model", "version")
                    ?? GetString(ctx.Resource.Properties, "version");

                if (!string.IsNullOrEmpty(modelName) && !string.IsNullOrEmpty(modelVersion))
                {
                    openai.AddDeployment(Sanitize($"{ctx.ResourceName}-{modelName}"), modelName, modelVersion);
                }
                else
                {
                    ctx.Diagnostics.Information(
                        "OpenAI resource imported without a model deployment because the model name/version could not be determined.",
                        ctx.ResourceName);
                }

                return openai.AsGeneric();
            }),
    ];

    private static bool MatchesType(AzdResourceMapContext context, params string[] types)
    {
        var actual = context.Resource.Type;
        return actual is not null && types.Any(t => string.Equals(t, actual, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the environment variables azd injects into a service that <c>uses</c> a resource of the
    /// given type, so the importer can tell the customer which variable names their application code
    /// previously relied on.
    /// </summary>
    /// <remarks>
    /// azd derives these names as <c>&lt;PREFIX&gt;_&lt;VAR&gt;</c> (snake/upper-cased) and injects them into the
    /// consuming service (passwordless by default; secrets come from Key Vault references). Aspire wires
    /// the same dependency with <c>WithReference</c> using its own connection conventions, so these names
    /// are reported as a diagnostic rather than silently changed.
    /// See <c>internal/scaffold/resource_meta.go</c> (EnvVarName) in https://github.com/Azure/azure-dev.
    /// </remarks>
    internal static IReadOnlyList<string> GetAzdInjectedEnvironmentVariables(string? type) => type?.ToLowerInvariant() switch
    {
        "db.redis" => ["REDIS_HOST", "REDIS_PORT", "REDIS_PASSWORD", "REDIS_URL", "REDIS_ENDPOINT"],
        "db.postgres" => ["POSTGRES_HOST", "POSTGRES_DATABASE", "POSTGRES_USERNAME", "POSTGRES_PORT", "POSTGRES_PASSWORD", "POSTGRES_URL"],
        "db.mysql" => ["MYSQL_HOST", "MYSQL_DATABASE", "MYSQL_USERNAME", "MYSQL_PORT", "MYSQL_PASSWORD", "MYSQL_URL"],
        "db.mongo" => ["MONGODB_URL"],
        "db.cosmos" => ["AZURE_COSMOS_ENDPOINT"],
        "ai.openai.model" or "ai.openai" => ["AZURE_OPENAI_ENDPOINT"],
        "ai.project" => ["AZURE_AI_PROJECT_ENDPOINT"],
        "ai.search" => ["AZURE_AI_SEARCH_ENDPOINT"],
        "messaging.eventhubs" => ["AZURE_EVENT_HUBS_NAME", "AZURE_EVENT_HUBS_HOST"],
        "messaging.servicebus" => ["AZURE_SERVICE_BUS_NAME", "AZURE_SERVICE_BUS_HOST"],
        "storage" => ["AZURE_STORAGE_ACCOUNT_NAME", "AZURE_STORAGE_BLOB_ENDPOINT"],
        "keyvault" => ["AZURE_KEY_VAULT_NAME", "AZURE_KEY_VAULT_ENDPOINT"],
        _ => [],
    };

    /// <summary>
    /// Extracts a list of names from one of several possible property keys.
    /// </summary>
    /// <remarks>
    /// azd authors collections in more than one shape, so this accepts any of:
    /// a sequence of scalars (<c>[orders, payments]</c>), a sequence of maps each with a <c>name</c>
    /// field, or a map keyed by name (<c>{ orders: { ... } }</c>).
    /// </remarks>
    internal static IEnumerable<string> ExtractNames(IReadOnlyDictionary<string, object?> properties, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!properties.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            switch (value)
            {
                case IReadOnlyList<object?> list:
                    foreach (var item in list)
                    {
                        if (item is IReadOnlyDictionary<string, object?> map && map.TryGetValue("name", out var n) && n is not null)
                        {
                            yield return Convert.ToString(n, CultureInfo.InvariantCulture)!;
                        }
                        else if (item is not null)
                        {
                            yield return Convert.ToString(item, CultureInfo.InvariantCulture)!;
                        }
                    }

                    break;

                case IReadOnlyDictionary<string, object?> namedMap:
                    foreach (var name in namedMap.Keys)
                    {
                        yield return name;
                    }

                    break;

                default:
                    yield return Convert.ToString(value, CultureInfo.InvariantCulture)!;
                    break;
            }
        }
    }

    /// <summary>
    /// Extracts cosmos container specifications (name + partition key paths) from the
    /// <c>containers</c> property.
    /// </summary>
    /// <remarks>
    /// azd authors cosmos containers as a sequence of maps, each with a <c>name</c> and a
    /// <c>partitionKeys</c> list (for example <c>- name: items\n  partitionKeys: [/id]</c>). When no
    /// partition key is supplied, Cosmos defaults to <c>/id</c>.
    /// See https://github.com/Azure/azure-dev/blob/main/cli/azd/pkg/project/resources.go (CosmosDBProps).
    /// </remarks>
    internal static IEnumerable<(string Name, string[] PartitionKeys)> ExtractContainers(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue("containers", out var value) || value is not IReadOnlyList<object?> list)
        {
            yield break;
        }

        foreach (var item in list)
        {
            if (item is not IReadOnlyDictionary<string, object?> map ||
                !map.TryGetValue("name", out var nameValue) || nameValue is null)
            {
                continue;
            }

            var name = Convert.ToString(nameValue, CultureInfo.InvariantCulture)!;

            var partitionKeys = map.TryGetValue("partitionKeys", out var keys) && keys is IReadOnlyList<object?> keyList && keyList.Count > 0
                ? keyList.Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)!).ToArray()
                : ["/id"];

            yield return (name, partitionKeys);
        }
    }

    internal static string? GetString(IReadOnlyDictionary<string, object?> properties, string key)
        => properties.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    internal static string? GetNestedString(IReadOnlyDictionary<string, object?> properties, string key, string nestedKey)
        => properties.TryGetValue(key, out var value) && value is IReadOnlyDictionary<string, object?> nested
            ? GetString(nested, nestedKey)
            : null;

    /// <summary>
    /// Sanitizes an arbitrary azd name into a valid Aspire resource name (lowercase alphanumerics and hyphens).
    /// </summary>
    internal static string Sanitize(string name)
    {
        var chars = name
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();

        var sanitized = new string(chars).Trim('-');

        // Resource names must start with a letter; prefix when the source started with a digit/hyphen.
        if (sanitized.Length == 0)
        {
            return "resource";
        }

        return char.IsLetter(sanitized[0]) ? sanitized : $"r-{sanitized}";
    }

    private sealed class DelegateResourceMapper(
        Func<AzdResourceMapContext, bool> canMap,
        Func<AzdResourceMapContext, IResourceBuilder<IResource>?> map) : IAzdResourceMapper
    {
        public bool CanMap(AzdResourceMapContext context) => canMap(context);

        public IResourceBuilder<IResource>? Map(AzdResourceMapContext context) => map(context);
    }
}

internal static class ResourceBuilderGenericExtensions
{
    /// <summary>
    /// Upcasts a strongly-typed resource builder to <see cref="IResourceBuilder{IResource}"/>.
    /// </summary>
    internal static IResourceBuilder<IResource> AsGeneric<T>(this IResourceBuilder<T> builder) where T : class, IResource
        => builder;
}
