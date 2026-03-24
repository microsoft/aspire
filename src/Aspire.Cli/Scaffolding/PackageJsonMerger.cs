// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Merges scaffold-generated package.json with an existing one on disk.
/// Handles script name conflicts by adding Aspire-specific scripts under the <c>aspire:</c>
/// namespace prefix, and creates convenience aliases for non-conflicting names.
/// </summary>
internal static class PackageJsonMerger
{
    private const string ScriptsKey = "scripts";
    private const string DependenciesKey = "dependencies";
    private const string DevDependenciesKey = "devDependencies";
    private const string AspirePrefix = "aspire:";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Merges scaffold-generated package.json content with existing content.
    /// Preserves all existing properties and scripts. Scaffold scripts that conflict
    /// with existing names are added under the <c>aspire:</c> prefix. Non-conflicting
    /// <c>aspire:X</c> scripts get a convenience alias <c>X</c> pointing to <c>npm run aspire:X</c>.
    /// </summary>
    /// <returns>The merged package.json content as a JSON string.</returns>
    internal static string Merge(string existingContent, string scaffoldContent, ILogger? logger = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(existingContent))
            {
                return scaffoldContent;
            }

            var existingJson = JsonNode.Parse(existingContent)?.AsObject();
            var scaffoldJson = JsonNode.Parse(scaffoldContent)?.AsObject();

            if (existingJson is null || scaffoldJson is null)
            {
                return scaffoldContent;
            }

            MergeObjects(existingJson, scaffoldJson);
            return existingJson.ToJsonString(s_jsonOptions);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to merge existing package.json, using scaffold output as-is");
            return scaffoldContent;
        }
    }

    /// <summary>
    /// Merges all top-level properties from scaffold into existing.
    /// Scripts get special conflict-aware handling, dependency sections use semver-aware merging,
    /// and everything else uses deep merge.
    /// </summary>
    private static void MergeObjects(JsonObject existing, JsonObject scaffold)
    {
        // Handle scripts separately with conflict-aware logic
        var scaffoldScripts = scaffold[ScriptsKey]?.AsObject();
        if (scaffoldScripts is not null)
        {
            var existingScripts = EnsureObject(existing, ScriptsKey);
            MergeScripts(existingScripts, scaffoldScripts);
        }

        // Handle dependency sections with semver-aware merging
        MergeDependencySection(existing, scaffold, DependenciesKey);
        MergeDependencySection(existing, scaffold, DevDependenciesKey);

        // Deep merge everything else
        foreach (var (key, sourceValue) in scaffold)
        {
            if (key is ScriptsKey or DependenciesKey or DevDependenciesKey || sourceValue is null)
            {
                continue;
            }

            var targetValue = existing[key];

            if (targetValue is null)
            {
                existing[key] = sourceValue.DeepClone();
            }
            else if (targetValue is JsonObject targetObj && sourceValue is JsonObject sourceObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            // Scalar values in existing are preserved
        }
    }

    /// <summary>
    /// Merges scaffold scripts into existing scripts with conflict-aware handling.
    /// </summary>
    /// <remarks>
    /// For each scaffold script:
    /// <list type="bullet">
    /// <item>Already <c>aspire:</c> prefixed → always added/updated</item>
    /// <item>Not prefixed, conflicts with existing → added as <c>aspire:{name}</c></item>
    /// <item>Not prefixed, no conflict → added with the original name</item>
    /// </list>
    /// After processing, for each <c>aspire:X</c> script where no non-prefixed <c>X</c> exists,
    /// a convenience alias is added: <c>"X": "npm run aspire:X"</c>.
    /// </remarks>
    internal static void MergeScripts(JsonObject existingScripts, JsonObject scaffoldScripts)
    {
        foreach (var (name, value) in scaffoldScripts)
        {
            if (value is not JsonValue scriptValue || !scriptValue.TryGetValue<string>(out var command))
            {
                continue;
            }

            if (name.StartsWith(AspirePrefix, StringComparison.Ordinal))
            {
                // Already prefixed — always set it
                existingScripts[name] = command;
            }
            else if (existingScripts[name] is not null)
            {
                // Conflict — add under aspire: prefix
                existingScripts[$"{AspirePrefix}{name}"] = command;
            }
            else
            {
                // No conflict — add with original name
                existingScripts[name] = command;
            }
        }

        // Add convenience aliases for aspire: scripts that have no non-prefixed equivalent
        AddConvenienceAliases(existingScripts);
    }

    /// <summary>
    /// For each <c>aspire:X</c> script, if no script named <c>X</c> exists,
    /// adds <c>"X": "npm run aspire:X"</c> as a convenience alias.
    /// </summary>
    private static void AddConvenienceAliases(JsonObject scripts)
    {
        // Collect aspire: keys first to avoid modifying during enumeration
        var aspireScripts = new List<(string unprefixed, string prefixed)>();
        foreach (var (name, _) in scripts)
        {
            if (name.StartsWith(AspirePrefix, StringComparison.Ordinal))
            {
                var unprefixed = name[AspirePrefix.Length..];
                if (unprefixed.Length > 0)
                {
                    aspireScripts.Add((unprefixed, name));
                }
            }
        }

        foreach (var (unprefixed, prefixed) in aspireScripts)
        {
            if (scripts[unprefixed] is null)
            {
                scripts[unprefixed] = $"npm run {prefixed}";
            }
        }
    }

    /// <summary>
    /// Merges a dependency section (e.g., "dependencies", "devDependencies") from scaffold into existing
    /// using semver-aware comparison. New packages are added; existing packages are upgraded only when
    /// the scaffold specifies a newer version. Unparseable version ranges (union ranges, workspace
    /// references, etc.) are preserved as-is.
    /// </summary>
    private static void MergeDependencySection(JsonObject existing, JsonObject scaffold, string sectionName)
    {
        var scaffoldDeps = scaffold[sectionName]?.AsObject();
        if (scaffoldDeps is null)
        {
            return;
        }

        var existingDeps = EnsureObject(existing, sectionName);

        foreach (var (packageName, versionNode) in scaffoldDeps)
        {
            if (versionNode is not JsonValue desiredValue || !desiredValue.TryGetValue<string>(out var desiredVersion))
            {
                continue;
            }

            var existingVersionNode = existingDeps[packageName];
            if (existingVersionNode is null)
            {
                existingDeps[packageName] = desiredVersion;
            }
            else
            {
                if (existingVersionNode is JsonValue existingValue
                    && existingValue.TryGetValue<string>(out var existingVersion)
                    && NpmVersionHelper.ShouldUpgrade(existingVersion, desiredVersion))
                {
                    existingDeps[packageName] = desiredVersion;
                }
            }
        }
    }

    /// <summary>
    /// Deep merges properties from source into target. Existing target values are preserved.
    /// For nested objects, recursively merges. Scalar values in target are never overwritten.
    /// </summary>
    internal static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, sourceValue) in source)
        {
            if (sourceValue is null)
            {
                continue;
            }

            var targetValue = target[key];

            if (targetValue is null)
            {
                target[key] = sourceValue.DeepClone();
            }
            else if (targetValue is JsonObject targetObj && sourceValue is JsonObject sourceObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            // Scalar values in target are preserved
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        parent[propertyName] = obj;
        return obj;
    }
}
