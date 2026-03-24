// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Utils;

internal static class ConfigurationHelper
{
    /// <summary>
    /// Standard options for parsing JSON that may contain non-spec features like comments and trailing commas.
    /// </summary>
    public static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static void RegisterSettingsFiles(IConfigurationBuilder configuration, DirectoryInfo workingDirectory, FileInfo globalSettingsFile)
    {
        var currentDirectory = workingDirectory;

        // Find the nearest local settings file (prefer aspire.config.json, fall back to .aspire/settings.json)
        FileInfo? localSettingsFile = null;

        while (currentDirectory is not null)
        {
            // Check for aspire.config.json first (new format)
            var newSettingsPath = Path.Combine(currentDirectory.FullName, AspireConfigFile.FileName);
            if (File.Exists(newSettingsPath))
            {
                localSettingsFile = new FileInfo(newSettingsPath);
                break;
            }

            // TODO: Remove legacy .aspire/settings.json fallback once confident most users have migrated.
            // Tracked by https://github.com/microsoft/aspire/issues/15239
            // Fall back to .aspire/settings.json (legacy format)
            var legacySettingsPath = BuildPathToSettingsJsonFile(currentDirectory.FullName);
            if (File.Exists(legacySettingsPath))
            {
                localSettingsFile = new FileInfo(legacySettingsPath);
                break;
            }

            currentDirectory = currentDirectory.Parent;
        }

        // Add global settings first (if it exists) - lower precedence
        if (File.Exists(globalSettingsFile.FullName))
        {
            AddSettingsFile(configuration, globalSettingsFile.FullName);
        }

        // Then add local settings (if found) - this will override global settings
        if (localSettingsFile is not null)
        {
            AddSettingsFile(configuration, localSettingsFile.FullName);
        }
    }

    internal static string BuildPathToSettingsJsonFile(string workingDirectory)
    {
        return Path.Combine(workingDirectory, ".aspire", "settings.json");
    }

    /// <summary>
    /// Serializes a JsonObject and writes it to a settings file, creating the directory if needed.
    /// </summary>
    internal static async Task WriteSettingsFileAsync(string filePath, JsonObject settings, CancellationToken cancellationToken = default)
    {
        var jsonContent = JsonSerializer.Serialize(settings, JsonSourceGenerationContext.Default.JsonObject);

        EnsureDirectoryExists(filePath);
        await File.WriteAllTextAsync(filePath, jsonContent, cancellationToken);
    }

    /// <summary>
    /// Serializes a JsonObject and writes it to a settings file, creating the directory if needed.
    /// </summary>
    internal static void WriteSettingsFile(string filePath, JsonObject settings)
    {
        var jsonContent = JsonSerializer.Serialize(settings, JsonSourceGenerationContext.Default.JsonObject);

        EnsureDirectoryExists(filePath);
        File.WriteAllText(filePath, jsonContent);
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void AddSettingsFile(IConfigurationBuilder configuration, string filePath)
    {
        // Proactively normalize the settings file to prevent duplicate key errors.
        // This handles files corrupted by mixing colon and dot notation
        // (e.g., both "features:key" flat entry and "features" nested object).
        TryNormalizeSettingsFile(filePath);

        // Pre-process the file to handle comments and trailing commas.
        // Microsoft.Extensions.Configuration.Json doesn't support JSON comments,
        // so we parse with comment support and load the clean JSON via stream.
        try
        {
            var content = File.ReadAllText(filePath);
            var node = JsonNode.Parse(content, documentOptions: ParseOptions);
            if (node is not null)
            {
                var cleanJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                var bytes = System.Text.Encoding.UTF8.GetBytes(cleanJson);
                configuration.AddJsonStream(new MemoryStream(bytes));
                return;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidJsonInConfigFile, filePath, ex.Message),
                ex);
        }

        configuration.AddJsonFile(filePath, optional: true);
    }

    /// <summary>
    /// Normalizes a settings file by converting flat colon-separated keys to nested JSON objects.
    /// </summary>
    internal static bool TryNormalizeSettingsFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);

            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var settings = JsonNode.Parse(content, documentOptions: ParseOptions)?.AsObject();

            if (settings is null)
            {
                return false;
            }

            // Find all colon-separated keys at root level
            var colonKeys = new List<(string key, string? value)>();

            foreach (var kvp in settings)
            {
                if (kvp.Key.Contains(':'))
                {
                    colonKeys.Add((kvp.Key, kvp.Value?.ToString()));
                }
            }

            if (colonKeys.Count == 0)
            {
                return false;
            }

            // Remove colon keys and re-add them as nested structure
            foreach (var (key, value) in colonKeys)
            {
                settings.Remove(key);

                // Convert "a:b:c" to nested {"a": {"b": {"c": value}}}
                var parts = key.Split(':');
                var currentObject = settings;
                var pathConflict = false;

                // Walk all but the last segment, creating objects as needed.
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var part = parts[i];

                    if (!currentObject.ContainsKey(part) || currentObject[part] is null)
                    {
                        currentObject[part] = new JsonObject();
                    }
                    else if (currentObject[part] is JsonObject)
                    {
                        currentObject = currentObject[part]!.AsObject();
                        continue;
                    }
                    else
                    {
                        // Existing non-object value conflicts with the desired nested structure.
                        // Prefer the existing nested value and drop the flat key.
                        pathConflict = true;
                        break;
                    }

                    currentObject = currentObject[part]!.AsObject();
                }

                if (pathConflict)
                {
                    continue;
                }

                var finalKey = parts[parts.Length - 1];

                // If the final key already exists, keep its value and drop the flat key.
                if (currentObject.ContainsKey(finalKey) && currentObject[finalKey] is not null)
                {
                    continue;
                }

                currentObject[finalKey] = value;
            }

            WriteSettingsFile(filePath, settings);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
