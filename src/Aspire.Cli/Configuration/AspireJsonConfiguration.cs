// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Configuration;

/// <summary>
/// Represents the .aspire/settings.json configuration file for polyglot app hosts.
/// This is the single source of truth for polyglot AppHost configuration,
/// analogous to .csproj for .NET AppHost projects.
/// </summary>
internal sealed class AspireJsonConfiguration
{
    public const string SettingsFolder = ".aspire";
    public const string FileName = "settings.json";

    /// <summary>
    /// The JSON Schema URL for this configuration file.
    /// </summary>
    [JsonPropertyName("$schema")]
    [Description("The JSON Schema URL for this configuration file.")]
    public string? Schema { get; set; }

    /// <summary>
    /// The path to the AppHost entry point file (e.g., "Program.cs", "app.ts").
    /// Relative to the directory containing .aspire/settings.json.
    /// </summary>
    [JsonPropertyName("appHostPath")]
    [LocalAspireJsonConfigurationProperty]
    [Description("The path to the AppHost entry point file (e.g., \"Program.cs\", \"app.ts\"). Relative to the directory containing .aspire/settings.json.")]
    public string? AppHostPath { get; set; }

    /// <summary>
    /// The language identifier for this AppHost (e.g., "typescript", "python").
    /// Used to determine which runtime to use for execution.
    /// </summary>
    [JsonPropertyName("language")]
    [Description("The language identifier for this AppHost (e.g., \"typescript\", \"python\"). Used to determine which runtime to use for execution.")]
    public string? Language { get; set; }

    /// <summary>
    /// The Aspire channel to use for package resolution (e.g., "stable", "preview", "staging").
    /// Used by aspire add to determine which NuGet feed to use.
    /// </summary>
    [JsonPropertyName("channel")]
    [Description("The Aspire channel to use for package resolution (e.g., \"stable\", \"preview\", \"staging\"). Used by aspire add to determine which NuGet feed to use.")]
    public string? Channel { get; set; }

    /// <summary>
    /// The Aspire SDK version used for this polyglot AppHost project.
    /// Determines the version of Aspire.Hosting packages to use.
    /// </summary>
    [JsonPropertyName("sdkVersion")]
    [Description("The Aspire SDK version used for this polyglot AppHost project. Determines the version of Aspire.Hosting packages to use.")]
    public string? SdkVersion { get; set; }

    /// <summary>
    /// Package references as an object literal (like npm's package.json).
    /// Key is package name, value is version.
    /// </summary>
    [JsonPropertyName("packages")]
    [Description("Package references as an object literal (like npm's package.json). Key is package name, value is version.")]
    public Dictionary<string, string>? Packages { get; set; }

    /// <summary>
    /// Feature flags for enabling/disabling experimental or optional features.
    /// Key is feature name, value is enabled (true) or disabled (false).
    /// </summary>
    [JsonPropertyName("features")]
    [JsonConverter(typeof(FlexibleBooleanDictionaryConverter))]
    [Description("Feature flags for enabling/disabling experimental or optional features. Key is feature name, value is enabled (true) or disabled (false).")]
    public Dictionary<string, bool>? Features { get; set; }

    /// <summary>
    /// Captures any additional properties not explicitly defined in this class.
    /// This ensures settings like "features" are preserved when saving.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Gets the full path to the settings.json file.
    /// </summary>
    public static string GetFilePath(string directory)
    {
        return Path.Combine(directory, SettingsFolder, FileName);
    }

    /// <summary>
    /// Loads the .aspire/settings.json configuration from the specified directory.
    /// Returns null if the file doesn't exist.
    /// </summary>
    public static AspireJsonConfiguration? Load(string directory)
    {
        var filePath = GetFilePath(directory);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = File.ReadAllText(filePath);

        // Handle empty files or whitespace-only content
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.AspireJsonConfiguration);
    }

    /// <summary>
    /// Loads the .aspire/settings.json configuration from the specified directory,
    /// or creates a new one with the specified SDK version if it doesn't exist.
    /// Ensures SdkVersion is always set.
    /// </summary>
    /// <param name="directory">The directory to load from.</param>
    /// <param name="defaultSdkVersion">The default SDK version to use if not already set.</param>
    /// <returns>The loaded or created configuration with SdkVersion guaranteed to be set.</returns>
    public static AspireJsonConfiguration LoadOrCreate(string directory, string defaultSdkVersion)
    {
        var config = Load(directory) ?? new AspireJsonConfiguration();
        config.SdkVersion ??= defaultSdkVersion;
        return config;
    }

    /// <summary>
    /// Saves the .aspire/settings.json configuration to the specified directory.
    /// </summary>
    public void Save(string directory)
    {
        var folderPath = Path.Combine(directory, SettingsFolder);
        Directory.CreateDirectory(folderPath);

        var filePath = Path.Combine(folderPath, FileName);
        var json = JsonSerializer.Serialize(this, JsonSourceGenerationContext.Default.AspireJsonConfiguration);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Adds a package reference, updating the version if it already exists.
    /// </summary>
    public void AddOrUpdatePackage(string packageId, string version)
    {
        Packages ??= [];
        Packages[packageId] = version;
    }

    /// <summary>
    /// Removes a package reference.
    /// </summary>
    public bool RemovePackage(string packageId)
    {
        if (Packages is null)
        {
            return false;
        }

        return Packages.Remove(packageId);
    }

    /// <summary>
    /// Gets the effective SDK version for package-based AppHost preparation.
    /// Falls back to <paramref name="defaultSdkVersion"/> when no SDK version is configured.
    /// </summary>
    public string GetEffectiveSdkVersion(string defaultSdkVersion)
    {
        return string.IsNullOrWhiteSpace(SdkVersion) ? defaultSdkVersion : SdkVersion;
    }

    /// <summary>
    /// Gets all integration references (both NuGet packages and project references)
    /// including the base Aspire.Hosting package.
    /// A value ending in ".csproj" is treated as a project reference; otherwise as a NuGet version.
    /// Empty package versions are resolved to the effective SDK version.
    /// </summary>
    /// <param name="defaultSdkVersion">Default SDK version to use when not configured.</param>
    /// <param name="settingsDirectory">The directory containing .aspire/settings.json, used to resolve relative project paths.</param>
    /// <returns>Enumerable of IntegrationReference objects.</returns>
    public IEnumerable<IntegrationReference> GetIntegrationReferences(string defaultSdkVersion, string settingsDirectory)
    {
        var sdkVersion = GetEffectiveSdkVersion(defaultSdkVersion);

        // Base package always included
        yield return IntegrationReference.FromPackage("Aspire.Hosting", sdkVersion);

        if (Packages is null)
        {
            yield break;
        }

        foreach (var (packageName, value) in Packages)
        {
            // Skip base packages and SDK-only packages
            if (string.Equals(packageName, "Aspire.Hosting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(packageName, "Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var trimmedValue = value?.Trim();

            if (string.IsNullOrEmpty(trimmedValue))
            {
                // NuGet package reference with no explicit version — fall back to the SDK version
                yield return IntegrationReference.FromPackage(packageName, sdkVersion);
                continue;
            }

            if (trimmedValue.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                // Project reference — resolve relative path to absolute
                var absolutePath = Path.GetFullPath(Path.Combine(settingsDirectory, trimmedValue));
                yield return IntegrationReference.FromProject(packageName, absolutePath);
            }
            else
            {
                // NuGet package reference with explicit version
                yield return IntegrationReference.FromPackage(packageName, trimmedValue);
            }
        }
    }

}
