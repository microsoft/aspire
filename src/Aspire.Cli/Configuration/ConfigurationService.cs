// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
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

        // Set the configuration value using dot notation support
        SetNestedValue(settings, key, value);

        await ConfigurationHelper.WriteSettingsFileAsync(settingsFilePath, settings, cancellationToken);
    }

    public async Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        var settingsFilePath = GetSettingsFilePath(isGlobal);

        if (!File.Exists(settingsFilePath))
        {
            return false;
        }

        try
        {
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

            // Delete using dot notation support and return whether deletion occurred
            var deleted = DeleteNestedValue(settings, key);

            if (deleted)
            {
                await ConfigurationHelper.WriteSettingsFileAsync(settingsFilePath, settings, cancellationToken);
            }

            return deleted;
        }
        catch
        {
            return false;
        }
    }

    public string GetSettingsFilePath(bool isGlobal)
    {
        if (isGlobal)
        {
            return globalSettingsFile.FullName;
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
        await LoadConfigurationFromFileAsync(globalSettingsFile.FullName, allConfig, cancellationToken);

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
        await LoadConfigurationFromFileAsync(globalSettingsFile.FullName, globalConfig, cancellationToken);
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
    /// Sets a nested value in a JsonObject using dot notation.
    /// Creates intermediate objects as needed and replaces primitives with objects when necessary.
    /// Also removes any conflicting flattened keys (colon-separated format) to prevent duplicate key errors.
    /// </summary>
    private static void SetNestedValue(JsonObject settings, string key, string value)
    {
        // Normalize colon-separated keys to dot notation since both represent
        // the same configuration hierarchy (e.g., "features:polyglotSupportEnabled"
        // is equivalent to "features.polyglotSupportEnabled")
        key = key.Replace(':', '.');

        var keyParts = key.Split('.');

        // Remove any conflicting flattened keys (e.g., "features:showAllTemplates" when setting "features.showAllTemplates")
        // This prevents duplicate key errors when loading the configuration
        RemoveConflictingFlattenedKeys(settings, keyParts);

        var currentObject = settings;

        // Navigate to the parent object, creating objects as needed
        for (int i = 0; i < keyParts.Length - 1; i++)
        {
            var part = keyParts[i];

            // If the property doesn't exist or isn't an object, replace it with a new object
            if (!currentObject.ContainsKey(part) || currentObject[part] is not JsonObject)
            {
                currentObject[part] = new JsonObject();
            }

            currentObject = currentObject[part]!.AsObject();
        }

        // Set the final value
        var finalKey = keyParts[keyParts.Length - 1];
        currentObject[finalKey] = value;
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
        for (int i = 1; i < keyParts.Length; i++)
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
    /// Deletes a nested value from a JsonObject using dot notation.
    /// Cleans up empty parent objects after deletion.
    /// </summary>
    private static bool DeleteNestedValue(JsonObject settings, string key)
    {
        // Normalize colon-separated keys to dot notation
        key = key.Replace(':', '.');

        var keyParts = key.Split('.');

        // Remove any flat colon-separated key at root level (legacy format)
        var flattenedKey = string.Join(":", keyParts);
        var removedFlat = settings.Remove(flattenedKey);

        var currentObject = settings;
        var objectPath = new List<(JsonObject obj, string key)>();

        // Navigate to the target value, keeping track of the path
        for (int i = 0; i < keyParts.Length - 1; i++)
        {
            var part = keyParts[i];
            objectPath.Add((currentObject, part));

            if (!currentObject.ContainsKey(part) || currentObject[part] is not JsonObject)
            {
                return removedFlat; // Path doesn't exist, but may have removed flat key
            }

            currentObject = currentObject[part]!.AsObject();
        }

        var finalKey = keyParts[keyParts.Length - 1];

        // Check if the final key exists
        if (!currentObject.ContainsKey(finalKey))
        {
            return removedFlat;
        }

        // Remove the final key
        currentObject.Remove(finalKey);

        // Clean up empty parent objects, working backwards
        for (int i = objectPath.Count - 1; i >= 0; i--)
        {
            var (parentObject, parentKey) = objectPath[i];

            // If the current object is empty, remove it from its parent
            if (currentObject.Count == 0)
            {
                parentObject.Remove(parentKey);
                currentObject = parentObject;
            }
            else
            {
                break; // Stop cleanup if we encounter a non-empty object
            }
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

            if (kvp.Value is JsonObject nestedObj)
            {
                FlattenJsonObject(nestedObj, result, key);
            }
            else if (kvp.Value is not null)
            {
                result[key] = kvp.Value.ToString();
            }
        }
    }

    public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
    {
        // Convert dot notation to colon notation for IConfiguration access
        var configKey = key.Replace('.', ':');
        return Task.FromResult(configuration[configKey]);
    }

    public Task<string?> GetConfigurationFromDirectoryAsync(string key, DirectoryInfo startDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        var configKey = key.Replace('.', ':');

        // 1. Project-relative local settings: walk up from startDirectory.
        //    Intentionally bypasses the process-wide IConfiguration (which is rooted at the
        //    CLI's launch cwd via ConfigurationHelper.RegisterSettingsFiles) so that commands
        //    that operate on a path other than cwd (e.g. `aspire update --apphost <elsewhere>`)
        //    consult the project's own aspire.config.json instead of the caller's cwd.
        var localConfigPath = ConfigurationHelper.FindNearestConfigFilePath(startDirectory);
        if (localConfigPath is not null)
        {
            var localConfig = LoadSettingsFileForReading(localConfigPath);
            var localValue = localConfig[configKey];
            if (!string.IsNullOrWhiteSpace(localValue))
            {
                return Task.FromResult<string?>(localValue);
            }
        }

        // 2. Global settings file fallback (lower precedence).
        if (File.Exists(globalSettingsFile.FullName))
        {
            var globalConfig = LoadSettingsFileForReading(globalSettingsFile.FullName);
            var globalValue = globalConfig[configKey];
            if (!string.IsNullOrWhiteSpace(globalValue))
            {
                return Task.FromResult<string?>(globalValue);
            }
        }

        return Task.FromResult<string?>(null);
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
}
