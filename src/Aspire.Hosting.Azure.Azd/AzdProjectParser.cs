// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Parses an azd <c>azure.yaml</c> document into the typed <see cref="AzdProject"/> model.
/// </summary>
/// <remarks>
/// Parsing is intentionally tolerant: the document is first read into an untyped object graph and
/// then projected onto the typed model. Unknown keys never cause failures; they are retained in
/// <see cref="AzdProject.Raw"/> and on each <see cref="AzdResource.Properties"/> so that callers and
/// diagnostics can still observe them.
/// </remarks>
internal static class AzdProjectParser
{
    /// <summary>
    /// Parses the contents of an <c>azure.yaml</c> document.
    /// </summary>
    /// <param name="yaml">The raw YAML text.</param>
    /// <returns>The parsed <see cref="AzdProject"/>.</returns>
    public static AzdProject Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var deserializer = new DeserializerBuilder().Build();

        // YamlDotNet yields Dictionary<object, object?> for mappings, List<object?> for sequences,
        // and string for scalars when deserializing to object. Normalize the whole tree to
        // string-keyed dictionaries so the rest of the importer can treat it uniformly.
        var rootObject = deserializer.Deserialize<object?>(yaml);
        var root = Normalize(rootObject) as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();

        var project = new AzdProject
        {
            Raw = root,
            Name = GetString(root, "name"),
            ResourceGroup = GetString(root, "resourceGroup"),
            Metadata = ParseMetadata(GetMap(root, "metadata")),
            Infra = ParseInfra(GetMap(root, "infra")),
            Services = ParseServices(GetMap(root, "services")),
            Resources = ParseResources(GetMap(root, "resources")),
        };

        return project;
    }

    private static AzdMetadata? ParseMetadata(IReadOnlyDictionary<string, object?>? map)
        => map is null ? null : new AzdMetadata { Template = GetString(map, "template") };

    private static AzdInfra? ParseInfra(IReadOnlyDictionary<string, object?>? map)
        => map is null ? null : new AzdInfra
        {
            Provider = GetString(map, "provider"),
            Path = GetString(map, "path"),
            Module = GetString(map, "module"),
        };

    private static IReadOnlyDictionary<string, AzdService> ParseServices(IReadOnlyDictionary<string, object?>? map)
    {
        var services = new Dictionary<string, AzdService>();
        if (map is null)
        {
            return services;
        }

        foreach (var (key, value) in map)
        {
            if (value is not IReadOnlyDictionary<string, object?> serviceMap)
            {
                continue;
            }

            services[key] = new AzdService
            {
                Project = GetString(serviceMap, "project"),
                Host = GetString(serviceMap, "host"),
                Language = GetString(serviceMap, "language"),
                Image = GetString(serviceMap, "image"),
                Docker = ParseDocker(GetMap(serviceMap, "docker")),
                Uses = GetStringList(serviceMap, "uses"),
                Env = GetStringMap(serviceMap, "env"),
            };
        }

        return services;
    }

    private static AzdDocker? ParseDocker(IReadOnlyDictionary<string, object?>? map)
        => map is null ? null : new AzdDocker
        {
            Context = GetString(map, "context"),
            Path = GetString(map, "path"),
            Target = GetString(map, "target"),
        };

    private static IReadOnlyDictionary<string, AzdResource> ParseResources(IReadOnlyDictionary<string, object?>? map)
    {
        var resources = new Dictionary<string, AzdResource>();
        if (map is null)
        {
            return resources;
        }

        foreach (var (key, value) in map)
        {
            if (value is not IReadOnlyDictionary<string, object?> resourceMap)
            {
                continue;
            }

            // Everything other than the well-known fields is type-specific configuration; keep it
            // in Properties so individual resource mappers can read it (databases, queues, models...).
            var properties = new Dictionary<string, object?>();
            foreach (var (propKey, propValue) in resourceMap)
            {
                if (propKey is "type" or "name" or "uses" or "existing")
                {
                    continue;
                }

                properties[propKey] = propValue;
            }

            resources[key] = new AzdResource
            {
                Type = GetString(resourceMap, "type"),
                Name = GetString(resourceMap, "name"),
                Uses = GetStringList(resourceMap, "uses"),
                Existing = GetBool(resourceMap, "existing"),
                Properties = properties,
            };
        }

        return resources;
    }

    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case IDictionary<object, object> dictionary:
                var map = new Dictionary<string, object?>();
                foreach (var entry in dictionary)
                {
                    map[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = Normalize(entry.Value);
                }

                return map;

            case IList<object> list:
                var items = new List<object?>(list.Count);
                foreach (var item in list)
                {
                    items.Add(Normalize(item));
                }

                return items;

            default:
                return value;
        }
    }

    private static IReadOnlyDictionary<string, object?>? GetMap(IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) && value is IReadOnlyDictionary<string, object?> nested ? nested : null;

    private static string? GetString(IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static bool GetBool(IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) && value is not null
           && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var result) && result;

    private static IReadOnlyList<string> GetStringList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        // `uses` may be authored either as a sequence or, occasionally, as a single scalar.
        if (value is IReadOnlyList<object?> list)
        {
            return list
                .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))
                .Where(item => !string.IsNullOrEmpty(item))
                .Select(item => item!)
                .ToList();
        }

        var single = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(single) ? [] : [single];
    }

    private static IReadOnlyDictionary<string, string?> GetStringMap(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (GetMap(map, key) is not { } nested)
        {
            return new Dictionary<string, string?>();
        }

        var result = new Dictionary<string, string?>();
        foreach (var (k, v) in nested)
        {
            result[k] = v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        return result;
    }
}
