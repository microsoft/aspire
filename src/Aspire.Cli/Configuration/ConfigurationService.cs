// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Configuration;

internal sealed class ConfigurationService(IConfiguration configuration, CliExecutionContext executionContext, FileInfo globalSettingsFile, ILogger<ConfigurationService> logger) : IConfigurationService
{
    public async Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        var settingsFilePath = GetSettingsFilePath(isGlobal);
        if (!isGlobal && AppHostPathConfigurationPolicy.IsHierarchicalAppHostPathKey(key))
        {
            settingsFilePath = EnsureAspireConfigFileForAppHostPathSettings(settingsFilePath);
        }

        using var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            executionContext, isGlobal ? ConfigurationHelper.GetCanonicalGlobalSettingsFile(globalSettingsFile) : new FileInfo(settingsFilePath), logger, cancellationToken)
            ?? throw new IOException("Could not acquire the configuration lock.");
        if (isGlobal)
        {
            settingsFilePath = GetSettingsFilePath(isGlobal: true);
        }
        await SetConfigurationInFileAsync(settingsFilePath, key, value, cancellationToken);
    }

    internal static async Task SetConfigurationInFileAsync(string settingsFilePath, string key, string value, CancellationToken cancellationToken = default)
    {
        JsonObject settings;

        // Read existing settings or create new
        if (File.Exists(settingsFilePath))
        {
            var existingContent = await File.ReadAllTextAsync(settingsFilePath, cancellationToken);
            // Handle empty files or whitespace-only content
            settings = string.IsNullOrWhiteSpace(existingContent)
                ? new JsonObject()
                : JsonNode.Parse(existingContent, nodeOptions: null, ConfigurationHelper.ParseOptions)?.AsObject() ?? new JsonObject();
        }
        else
        {
            settings = new JsonObject();
        }

        ConfigurationHelper.NormalizeSettings(settings);
        var keyParts = ParseKey(key);
        var resolvedKey = TemplateSourceConfiguration.ValidateEditKey(settings, string.Join('.', keyParts.Select(part => part.Name)));
        ResolveKeyParts(keyParts, resolvedKey);

        // Validate the candidate under the config lock, before writing. A rejected edit must
        // preserve the original file, including comments and unrelated settings.
        SetNestedValue(settings, keyParts, key, value);
        TemplateSourceConfiguration.ValidateEditedSource(settings, resolvedKey);

        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(settingsFilePath, settings, cancellationToken, preserveOwnershipAndAcl: true);
    }

    public async Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        var keyParts = ParseKey(key);
        var settingsFilePath = GetSettingsFilePath(isGlobal);
        using var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            executionContext, isGlobal ? ConfigurationHelper.GetCanonicalGlobalSettingsFile(globalSettingsFile) : new FileInfo(settingsFilePath), logger, cancellationToken)
            ?? throw new IOException("Could not acquire the configuration lock.");
        if (isGlobal)
        {
            settingsFilePath = GetSettingsFilePath(isGlobal: true);
        }

        if (!File.Exists(settingsFilePath))
        {
            return false;
        }

        var existingContent = await File.ReadAllTextAsync(settingsFilePath, cancellationToken);

        // Handle empty files or whitespace-only content
        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return false;
        }

        var settings = JsonNode.Parse(existingContent, nodeOptions: null, ConfigurationHelper.ParseOptions)?.AsObject();
        if (settings is null)
        {
            return false;
        }

        ConfigurationHelper.NormalizeSettings(settings);
        ResolveKeyParts(keyParts, TemplateSourceConfiguration.ResolveEditKey(settings, string.Join('.', keyParts.Select(part => part.Name))));
        var removedFlat = settings.Remove(string.Join(':', keyParts.Select(part => part.Name)));
        var deleted = DeleteNestedValue(settings, keyParts, key, 0) || removedFlat;

        if (deleted)
        {
            await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(settingsFilePath, settings, cancellationToken, preserveOwnershipAndAcl: true);
        }

        return deleted;
    }

    public string GetSettingsFilePath(bool isGlobal)
    {
        if (isGlobal)
        {
            var preferredFile = ConfigurationHelper.GetCanonicalGlobalSettingsFile(globalSettingsFile);
            return File.Exists(preferredFile.FullName) ? preferredFile.FullName : globalSettingsFile.FullName;
        }
        else
        {
            return FindNearestSettingsFile();
        }
    }

    private string FindNearestSettingsFile()
    {
        var searchDirectory = executionContext.WorkingDirectory;

        // Walk up the directory tree to find existing settings file
        while (searchDirectory is not null)
        {
            // Prefer aspire.config.json (new format)
            var newSettingsPath = Path.Combine(searchDirectory.FullName, AspireConfigFile.FileName);
            if (File.Exists(newSettingsPath))
            {
                logger.LogInformation("Found settings file at {Path}", newSettingsPath);
                return newSettingsPath;
            }

            // TODO: Remove legacy .aspire/settings.json fallback once confident most users have migrated.
            // Tracked by https://github.com/microsoft/aspire/issues/15239
            // Fall back to .aspire/settings.json (legacy)
            var legacySettingsPath = ConfigurationHelper.BuildPathToSettingsJsonFile(searchDirectory.FullName);
            if (File.Exists(legacySettingsPath))
            {
                logger.LogInformation("Found legacy settings file at {Path}", legacySettingsPath);
                return legacySettingsPath;
            }

            searchDirectory = searchDirectory.Parent;
        }

        // If no existing settings file found, default to aspire.config.json in current directory
        var defaultPath = Path.Combine(executionContext.WorkingDirectory.FullName, AspireConfigFile.FileName);
        logger.LogDebug("No existing settings file found, defaulting to {Path}", defaultPath);
        return defaultPath;
    }

    private string EnsureAspireConfigFileForAppHostPathSettings(string settingsFilePath)
    {
        var settingsFile = new FileInfo(settingsFilePath);
        var legacySettingsRootDirectory = ConfigurationHelper.GetLegacySettingsRootDirectory(settingsFile);
        if (legacySettingsRootDirectory is null)
        {
            return settingsFilePath;
        }

        var aspireConfigPath = Path.Combine(legacySettingsRootDirectory.FullName, AspireConfigFile.FileName);
        if (!File.Exists(aspireConfigPath))
        {
            logger.LogInformation("Migrating legacy settings from {LegacyDir} to {ConfigFile}", legacySettingsRootDirectory.FullName, aspireConfigPath);
            _ = AspireConfigFile.LoadOrCreate(legacySettingsRootDirectory.FullName);
        }

        return aspireConfigPath;
    }

    public async Task<Dictionary<string, string>> GetAllConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var allConfig = new Dictionary<string, string>();

        var nearestSettingFilePath = FindNearestSettingsFile();
        await LoadConfigurationFromFileAsync(nearestSettingFilePath, allConfig, cancellationToken);
        await LoadConfigurationFromFileAsync(GetSettingsFilePath(isGlobal: true), allConfig, cancellationToken);

        return allConfig;
    }

    public async Task<Dictionary<string, string>> GetLocalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var localConfig = new Dictionary<string, string>();
        var nearestSettingFilePath = FindNearestSettingsFile();
        await LoadConfigurationFromFileAsync(nearestSettingFilePath, localConfig, cancellationToken);
        return localConfig;
    }

    public async Task<Dictionary<string, string>> GetGlobalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var globalConfig = new Dictionary<string, string>();
        await LoadConfigurationFromFileAsync(GetSettingsFilePath(isGlobal: true), globalConfig, cancellationToken);
        return globalConfig;
    }

    private static async Task LoadConfigurationFromFileAsync(string filePath, Dictionary<string, string> config, CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);

            // Handle empty files or whitespace-only content
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var settings = JsonNode.Parse(content, nodeOptions: null, ConfigurationHelper.ParseOptions)?.AsObject();

            if (settings is not null)
            {
                FlattenJsonObject(settings, config, string.Empty);
            }
        }
        catch
        {
            // Ignore errors reading configuration files
        }
    }

    /// <summary>
    /// Sets a nested value using dot notation and bracketed array indices.
    /// Creates intermediate containers as needed and preserves existing arrays.
    /// Also removes any conflicting flattened keys (colon-separated format) to prevent duplicate key errors.
    /// </summary>
    private static void SetNestedValue(JsonObject settings, KeyPart[] keyParts, string key, string value)
    {
        // Remove any conflicting flattened keys (e.g., "features:showAllTemplates" when setting "features.showAllTemplates")
        // This prevents duplicate key errors when loading the configuration
        RemoveConflictingFlattenedKeys(settings, keyParts.Select(part => part.Name).ToArray());

        JsonNode current = settings;

        for (int i = 0; i < keyParts.Length - 1; i++)
        {
            var part = keyParts[i];
            var child = GetChildValue(current, part, key, allowAppend: true);

            if (keyParts[i + 1].IsArrayIndex)
            {
                if (child is null)
                {
                    child = new JsonArray();
                    SetChildValue(current, part, key, child);
                }
                else if (child is not JsonArray)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationArrayRequired, key));
                }
            }
            else if (child is not JsonObject and not JsonArray)
            {
                child = new JsonObject();
                SetChildValue(current, part, key, child);
            }

            current = child;
        }

        SetChildValue(current, keyParts[^1], key, JsonValue.Create(value));
    }

    /// <summary>
    /// Removes any flattened keys (colon-separated) that would conflict with a nested structure.
    /// For example, when setting "features.showAllTemplates", remove "features:showAllTemplates".
    /// </summary>
    private static void RemoveConflictingFlattenedKeys(JsonObject settings, string[] keyParts)
    {
        // Build all possible flattened key patterns that could conflict
        // For key "a.b.c", we need to remove "a:b:c" from the root
        var flattenedKey = string.Join(":", keyParts);
        settings.Remove(flattenedKey);

        // Also check for partial flattened keys at each level
        // For example, if we have "a.b.c", we should also check for "a:b" in the root
        // that might contain a "c" value
        // A single property name is not a flattened key. Removing it here would
        // discard existing arrays or scalars before array traversal can validate them.
        for (int i = 2; i < keyParts.Length; i++)
        {
            var partialKey = string.Join(":", keyParts.Take(i));
            if (settings.ContainsKey(partialKey) && settings[partialKey] is not JsonObject)
            {
                // This is a flattened value that conflicts with our nested structure
                settings.Remove(partialKey);
            }
        }
    }

    /// <summary>
    /// Deletes a nested value, compacting arrays and cleaning up empty parent objects.
    /// </summary>
    private static bool DeleteNestedValue(JsonNode current, KeyPart[] keyParts, string key, int depth)
    {
        var part = keyParts[depth];
        if (current is not JsonObject and not JsonArray)
        {
            return false;
        }

        var child = GetChildValue(current, part, key, allowAppend: false);
        if (depth == keyParts.Length - 1)
        {
            if (current is JsonArray array)
            {
                var index = GetArrayIndex(part, key);
                if (index >= array.Count)
                {
                    return false;
                }
                array.RemoveAt(index);
                return true;
            }

            return current.AsObject().Remove(part.Name);
        }

        if (child is null || !DeleteNestedValue(child, keyParts, key, depth + 1))
        {
            return false;
        }

        // Keep empty arrays and array elements: removing them would change the setting's
        // type or shift an unrelated element when deleting a property inside an array.
        if (child is JsonObject { Count: 0 } && current is JsonObject parent)
        {
            parent.Remove(part.Name);
        }

        return true;
    }

    /// <summary>
    /// Recursively flattens a JsonObject into a dictionary with dot notation keys.
    /// </summary>
    private static void FlattenJsonObject(JsonObject obj, Dictionary<string, string> result, string prefix)
    {
        foreach (var kvp in obj)
        {
            // Normalize colon-separated keys to dot notation for consistent display
            var normalizedKey = kvp.Key.Replace(':', '.');
            var key = string.IsNullOrEmpty(prefix) ? normalizedKey : $"{prefix}.{normalizedKey}";

            FlattenJsonValue(kvp.Value, result, key);
        }
    }

    private static void FlattenJsonValue(JsonNode? value, Dictionary<string, string> result, string key)
    {
        if (value is JsonObject { Count: > 0 } obj)
        {
            FlattenJsonObject(obj, result, key);
        }
        else if (value is JsonArray { Count: > 0 } array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                FlattenJsonValue(array[i], result, $"{key}[{i.ToString(CultureInfo.InvariantCulture)}]");
            }
        }
        else if (value is not null)
        {
            result[key] = value.ToString();
        }
    }

    public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
    {
        var configKey = string.Join(':', ParseKey(key).Select(part => part.Name));
        return Task.FromResult(configuration[configKey]);
    }

    public Task<string?> GetConfigurationFromDirectoryAsync(string key, DirectoryInfo startDirectory, bool continueSearchWhenKeyMissing = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        var configKey = string.Join(':', ParseKey(key).Select(part => part.Name));

        // 1. Project-relative local settings: walk up from startDirectory to find the nearest
        //    config file. Most command lookups stop at that file, even when it omits the key,
        //    so a parent directory's unrelated app config doesn't override global settings.
        //    Targeted inheritance paths can explicitly continue past a key-missing file.
        //    Intentionally bypasses the process-wide IConfiguration (which is rooted at the
        //    CLI's launch cwd via ConfigurationHelper.RegisterSettingsFiles) so that commands
        //    that operate on a path other than cwd (e.g. `aspire update --apphost <elsewhere>`)
        //    consult the project's own aspire.config.json instead of the caller's cwd.
        for (var searchDirectory = startDirectory; searchDirectory is not null; searchDirectory = searchDirectory.Parent)
        {
            var configFilePath = Path.Combine(searchDirectory.FullName, AspireConfigFile.FileName);
            if (TryReadConfigurationValue(configFilePath, configKey, out var configFileValue))
            {
                return Task.FromResult<string?>(configFileValue);
            }
            else if (File.Exists(configFilePath))
            {
                if (!continueSearchWhenKeyMissing)
                {
                    break;
                }
            }

            var legacySettingsPath = ConfigurationHelper.BuildPathToSettingsJsonFile(searchDirectory.FullName);
            if (TryReadConfigurationValue(legacySettingsPath, configKey, out var legacySettingsValue))
            {
                return Task.FromResult<string?>(legacySettingsValue);
            }
            else if (File.Exists(legacySettingsPath) && !continueSearchWhenKeyMissing)
            {
                break;
            }
        }

        // 2. Global settings file fallback (lower precedence).
        //
        // Transitional path: identity-channel is now baked into the CLI binary (AspireCliChannel
        // assembly metadata) and the acquisition scripts no longer seed a "channel" field into
        // global settings. The read here remains so a user who deliberately ran
        // `aspire config set -g channel <x>` continues to get their preference honored by
        // `aspire update` until that workflow is removed in a follow-up. New per-project flows
        // (`aspire add`, `aspire init`) do not consult global config and must not start to.
        var globalSettingsFilePath = GetSettingsFilePath(isGlobal: true);
        if (File.Exists(globalSettingsFilePath))
        {
            var globalConfig = LoadSettingsFileForReading(globalSettingsFilePath);
            var globalValue = globalConfig[configKey];
            if (!string.IsNullOrWhiteSpace(globalValue))
            {
                return Task.FromResult<string?>(globalValue);
            }
        }

        return Task.FromResult<string?>(null);
    }

    private static bool TryReadConfigurationValue(string settingsFilePath, string configKey, [NotNullWhen(true)] out string? value)
    {
        value = null;

        if (!File.Exists(settingsFilePath))
        {
            return false;
        }

        var config = LoadSettingsFileForReading(settingsFilePath);
        var candidateValue = config[configKey];
        if (string.IsNullOrWhiteSpace(candidateValue))
        {
            return false;
        }

        value = candidateValue;
        return true;
    }

    /// <summary>
    /// Loads a single settings file into an isolated <see cref="IConfigurationRoot"/> for
    /// directory-scoped lookups, mirroring <c>ConfigurationHelper.AddSettingsFile</c>'s
    /// JSON-with-comments parsing and "throw on invalid JSON" behavior so directory-scoped
    /// reads fail loudly the same way startup-time loads do.
    /// </summary>
    private static IConfigurationRoot LoadSettingsFileForReading(string filePath)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return new ConfigurationBuilder().Build();
        }
        catch (UnauthorizedAccessException)
        {
            return new ConfigurationBuilder().Build();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new ConfigurationBuilder().Build();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content, documentOptions: ConfigurationHelper.ParseOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidJsonInConfigFile, filePath, ex.Message),
                ex);
        }

        if (node is not JsonObject)
        {
            return new ConfigurationBuilder().Build();
        }

        var cleanJson = node.ToJsonString();
        var bytes = System.Text.Encoding.UTF8.GetBytes(cleanJson);
        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(bytes))
            .Build();
    }

    private static KeyPart[] ParseKey(string key)
    {
        var parts = new List<KeyPart>();
        // Paths such as "sources.team.plugins[0]" and "matrix[0][1].name" use
        // brackets only for non-negative indices. Dotted numbers remain object
        // property names unless traversal reaches an existing JSON array.
        foreach (var segment in key.Replace(':', '.').Split('.'))
        {
            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];
            if (name.Length == 0 || name.Contains(']'))
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationKeyInvalid, key), nameof(key));
            }
            parts.Add(new KeyPart(name, IsArrayIndex: false));

            while (bracket >= 0)
            {
                var end = segment.IndexOf(']', bracket + 1);
                if (end < 0 || !int.TryParse(segment.AsSpan(bracket + 1, end - bracket - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationKeyInvalid, key), nameof(key));
                }
                parts.Add(new KeyPart(index.ToString(CultureInfo.InvariantCulture), IsArrayIndex: true));
                bracket = end + 1;
                if (bracket == segment.Length)
                {
                    break;
                }
                if (segment[bracket] != '[')
                {
                    throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationKeyInvalid, key), nameof(key));
                }
            }
        }

        return parts.ToArray();
    }

    private static void ResolveKeyParts(KeyPart[] parts, string resolvedKey)
    {
        var names = resolvedKey.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i] with { Name = names[i] };
        }
    }

    private static int GetArrayIndex(KeyPart part, string key)
    {
        if (!int.TryParse(part.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationArrayIndexInvalid, key), nameof(key));
        }

        return index;
    }

    private static JsonNode? GetChildValue(JsonNode current, KeyPart part, string key, bool allowAppend)
    {
        if (current is JsonArray array)
        {
            var index = GetArrayIndex(part, key);
            if (index < array.Count)
            {
                return array[index];
            }
            if (allowAppend && index > array.Count)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationArrayIndexOutOfRange, index, key, array.Count), nameof(key));
            }

            return null;
        }
        if (part.IsArrayIndex)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ConfigCommandStrings.ConfigurationArrayRequired, key));
        }

        return current.AsObject()[part.Name];
    }

    private static void SetChildValue(JsonNode current, KeyPart part, string key, JsonNode value)
    {
        _ = GetChildValue(current, part, key, allowAppend: true);
        if (current is JsonArray array)
        {
            var index = GetArrayIndex(part, key);
            if (index == array.Count)
            {
                array.Add(value);
            }
            else
            {
                array[index] = value;
            }
        }
        else
        {
            current.AsObject()[part.Name] = value;
        }
    }

    private readonly record struct KeyPart(string Name, bool IsArrayIndex);
}
