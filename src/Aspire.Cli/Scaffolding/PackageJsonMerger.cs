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
    private const string EnginesKey = "engines";
    private const string EnginesNodeKey = "node";
    private const string AspirePrefix = "aspire:";

    // package.json standard uses 2-space indentation. These options produce output
    // consistent with npm init / npm install formatting conventions.
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IndentSize = 2
    };

    private static readonly JsonDocumentOptions s_jsonDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Merges scaffold-generated package.json content with existing content.
    /// Preserves all existing properties and scripts. Scaffold scripts that conflict
    /// with existing names are added under the <c>aspire:</c> prefix. Non-conflicting
    /// <c>aspire:X</c> scripts get a convenience alias <c>X</c> pointing to <c>npm run aspire:X</c>.
    /// </summary>
    /// <returns>The merged package.json content as a JSON string.</returns>
    internal static string Merge(string existingContent, string scaffoldContent, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return scaffoldContent;
        }

        // Phase 1: Parse inputs. If either fails, return scaffold as-is.
        JsonObject? existingJson;
        JsonObject? scaffoldJson;
        try
        {
            existingJson = JsonNode.Parse(existingContent, documentOptions: s_jsonDocumentOptions) as JsonObject;
            scaffoldJson = JsonNode.Parse(scaffoldContent, documentOptions: s_jsonDocumentOptions) as JsonObject;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse package.json content, using scaffold output as-is.");
            return scaffoldContent;
        }

        if (existingJson is null || scaffoldJson is null)
        {
            return scaffoldContent;
        }

        // Phase 2: Merge. If merge fails, return scaffold as-is.
        try
        {
            MergeObjects(existingJson, scaffoldJson, logger);
            return existingJson.ToJsonString(s_jsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to merge package.json content, using scaffold output as-is.");
            return scaffoldContent;
        }
    }

    /// <summary>
    /// Merges all top-level properties from scaffold into existing.
    /// Scripts get special conflict-aware handling, dependency sections use semver-aware merging,
    /// and everything else uses deep merge.
    /// </summary>
    private static void MergeObjects(JsonObject existing, JsonObject scaffold, ILogger logger)
    {
        // Handle scripts separately with conflict-aware logic
        var scaffoldScripts = scaffold[ScriptsKey]?.AsObject();
        if (scaffoldScripts is not null)
        {
            var existingScripts = EnsureObject(existing, ScriptsKey, logger);
            MergeScripts(existingScripts, scaffoldScripts);
        }

        // Handle dependency sections with semver-aware merging
        MergeDependencySection(existing, scaffold, DependenciesKey, logger);
        MergeDependencySection(existing, scaffold, DevDependenciesKey, logger);

        // Handle engines with overwrite semantics for "node" — since the user is running
        // "aspire init", we enforce our Node version constraint (required for ESLint 10
        // and TypeScript tooling compatibility). Other engines sub-keys are preserved.
        MergeEngines(existing, scaffold, logger);

        // Deep merge everything else (scalars, nested objects).
        // Array properties (e.g., "keywords") are preserved from existing — the scaffold
        // echoes the original arrays unchanged, so the existing value is always correct.
        foreach (var (key, sourceValue) in scaffold)
        {
            if (key is ScriptsKey or DependenciesKey or DevDependenciesKey or EnginesKey || sourceValue is null)
            {
                continue;
            }

            var targetValue = existing[key];

            if (targetValue is null)
            {
                // Property only in scaffold — add it (including arrays from scaffold-only)
                existing[key] = sourceValue.DeepClone();
            }
            else if (targetValue is JsonObject targetObj && sourceValue is JsonObject sourceObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            // Arrays and scalar values in existing are preserved
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
    private static void MergeDependencySection(JsonObject existing, JsonObject scaffold, string sectionName, ILogger logger)
    {
        var scaffoldDeps = scaffold[sectionName]?.AsObject();
        if (scaffoldDeps is null)
        {
            return;
        }

        var existingDeps = EnsureObject(existing, sectionName, logger);

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
    /// Merges the <c>engines</c> section from scaffold into existing. The <c>engines.node</c>
    /// constraint is always overwritten by the scaffold's value because <c>aspire init</c> requires
    /// specific Node.js versions for ESLint 10 and TypeScript tooling compatibility. Other
    /// <c>engines</c> sub-keys (e.g., <c>npm</c>) are preserved from the existing package.json.
    /// </summary>
    private static void MergeEngines(JsonObject existing, JsonObject scaffold, ILogger logger)
    {
        var scaffoldEngines = scaffold[EnginesKey]?.AsObject();
        if (scaffoldEngines is null)
        {
            return;
        }

        var existingEngines = EnsureObject(existing, EnginesKey, logger);

        foreach (var (key, value) in scaffoldEngines)
        {
            if (value is null)
            {
                continue;
            }

            if (key == EnginesNodeKey)
            {
                // Always overwrite engines.node — Aspire requires specific Node versions
                existingEngines[key] = value.DeepClone();
            }
            else if (existingEngines[key] is null)
            {
                existingEngines[key] = value.DeepClone();
            }
            // Other existing engine constraints are preserved
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

    private static JsonObject EnsureObject(JsonObject parent, string propertyName, ILogger logger)
    {
        if (parent[propertyName] is JsonObject obj)
        {
            return obj;
        }

        if (parent[propertyName] is not null)
        {
            logger.LogWarning(
                "Replacing non-object '{PropertyName}' value with an empty object. The original value will be lost.",
                propertyName);
        }

        obj = new JsonObject();
        parent[propertyName] = obj;
        return obj;
    }
}
