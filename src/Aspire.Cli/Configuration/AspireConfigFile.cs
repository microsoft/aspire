// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Configuration;

/// <summary>
/// Represents the aspire.config.json (or aspire.config.yaml/.yml) configuration file.
/// Consolidates apphost location, launch settings, and CLI config into one file.
/// </summary>
/// <remarks>
/// <para>The new unified format (<c>aspire.config.json</c> or <c>aspire.config.yaml</c>) replaces
/// the legacy split across <c>.aspire/settings.json</c> (local settings) and
/// <c>apphost.run.json</c> (launch profiles).</para>
/// <para>Example <c>aspire.config.json</c>:</para>
/// <code>
/// {
///   "appHost": { "path": "app.ts", "language": "typescript/nodejs" },
///   "sdk": { "version": "9.2.0" },
///   "channel": "stable",
///   "features": { "polyglotSupportEnabled": true },
///   "profiles": {
///     "default": {
///       "applicationUrl": "https://localhost:17000;http://localhost:15000",
///       "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
///     }
///   },
///   "packages": { "Aspire.Hosting.Redis": "9.2.0" }
/// }
/// </code>
/// <para>Equivalent <c>aspire.config.yaml</c>:</para>
/// <code>
/// appHost:
///   path: app.ts
///   language: typescript/nodejs
/// sdk:
///   version: "9.2.0"
/// channel: stable
/// features:
///   polyglotSupportEnabled: true
/// profiles:
///   default:
///     applicationUrl: "https://localhost:17000;http://localhost:15000"
///     environmentVariables:
///       ASPNETCORE_ENVIRONMENT: Development
/// packages:
///   Aspire.Hosting.Redis: "9.2.0"
/// </code>
/// <para>Legacy <c>.aspire/settings.json</c> (flat keys):</para>
/// <code>
/// { "appHostPath": "app.ts", "language": "typescript/nodejs", "sdkVersion": "9.2.0" }
/// </code>
/// <para>Legacy <c>apphost.run.json</c> (launch profiles):</para>
/// <code>
/// { "profiles": { "default": { "applicationUrl": "https://localhost:17000" } } }
/// </code>
/// </remarks>
internal sealed class AspireConfigFile
{
    public const string FileName = "aspire.config.json";
    public const string FileNameYaml = "aspire.config.yaml";
    public const string FileNameYml = "aspire.config.yml";

    /// <summary>
    /// All recognized config file names, in precedence order.
    /// </summary>
    internal static readonly string[] s_allFileNames = [FileNameYaml, FileNameYml, FileName];

    /// <summary>
    /// Tracks which file name was used when loading, so <see cref="Save"/> can write back
    /// in the same format. <c>null</c> means the config was created in memory (defaults to JSON).
    /// </summary>
    [JsonIgnore]
    public string? SourceFileName { get; set; }

    /// <summary>
    /// The JSON Schema URL for this configuration file.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// AppHost entry point configuration.
    /// </summary>
    [JsonPropertyName("appHost")]
    [Description("AppHost entry point configuration.")]
    [LocalAspireJsonConfigurationProperty]
    public AspireConfigAppHost? AppHost { get; set; }

    /// <summary>
    /// Aspire SDK version configuration.
    /// </summary>
    [JsonPropertyName("sdk")]
    [Description("Aspire SDK version configuration.")]
    public AspireConfigSdk? Sdk { get; set; }

    /// <summary>
    /// Convenience accessor for the Aspire SDK version.
    /// Gets or sets <see cref="AspireConfigSdk.Version"/> on the <see cref="Sdk"/> object,
    /// creating the nested object when setting a value.
    /// </summary>
    [JsonIgnore]
    public string? SdkVersion
    {
        get => Sdk?.Version;
        set => (Sdk ??= new AspireConfigSdk()).Version = value;
    }

    /// <summary>
    /// Aspire channel for package resolution.
    /// </summary>
    [JsonPropertyName("channel")]
    [Description("The Aspire channel to use for package resolution (e.g., \"stable\", \"preview\", \"staging\", \"daily\"). Used by aspire add to determine which NuGet feed to use.")]
    public string? Channel { get; set; }

    /// <summary>
    /// Feature flags.
    /// </summary>
    [JsonPropertyName("features")]
    [Description("Feature flags for enabling/disabling experimental or optional features. Key is feature name, value is enabled (true) or disabled (false).")]
    public Dictionary<string, bool>? Features { get; set; }

    /// <summary>
    /// Launch profiles (ports, env vars). Replaces apphost.run.json.
    /// </summary>
    [JsonPropertyName("profiles")]
    [Description("Launch profiles (ports, environment variables). Replaces apphost.run.json.")]
    public Dictionary<string, AspireConfigProfile>? Profiles { get; set; }

    /// <summary>
    /// Package references for non-first-class languages.
    /// </summary>
    [JsonPropertyName("packages")]
    [Description("Package references for non-first-class languages. Key is package name, value is version. A value ending in \".csproj\" is treated as a project reference.")]
    public Dictionary<string, string>? Packages { get; set; }

    /// <summary>
    /// Finds the config file in a directory. Returns the full path and file name,
    /// or <c>null</c> if no config file exists. Throws if multiple formats are present.
    /// </summary>
    internal static (string filePath, string fileName)? FindConfigFile(string directory)
    {
        var found = new List<string>();

        foreach (var name in s_allFileNames)
        {
            if (File.Exists(Path.Combine(directory, name)))
            {
                found.Add(name);
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        if (found.Count > 1)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.MultipleConfigFormatsFound,
                    directory, string.Join(", ", found)));
        }

        return (Path.Combine(directory, found[0]), found[0]);
    }

    /// <summary>
    /// Loads the aspire config file (JSON, YAML, or YML) from the specified directory.
    /// </summary>
    /// <returns>The deserialized config, or <c>null</c> if the file does not exist.</returns>
    /// <exception cref="InvalidOperationException">Thrown when multiple config formats exist in the same directory.</exception>
    /// <exception cref="JsonException">Thrown when a JSON config file contains invalid JSON.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a YAML config file contains invalid YAML.</exception>
    public static AspireConfigFile? Load(string directory)
    {
        var found = FindConfigFile(directory);
        if (found is null)
        {
            return null;
        }

        var (filePath, fileName) = found.Value;

        var config = IsYamlFile(fileName)
            ? LoadYaml(filePath)
            : LoadJson(filePath);

        config.SourceFileName = fileName;
        return config;
    }

    private static AspireConfigFile LoadJson(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.AspireConfigFile)
                ?? new AspireConfigFile();
        }
        catch (JsonException ex)
        {
            throw new JsonException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidJsonInConfigFile, filePath, ex.Message),
                ex.Path, ex.LineNumber, ex.BytePositionInLine, ex);
        }
    }

    private static AspireConfigFile LoadYaml(string filePath)
    {
        try
        {
            var yaml = File.ReadAllText(filePath);
            var json = YamlJsonConverter.YamlToJson(yaml);
            return JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.AspireConfigFile)
                ?? new AspireConfigFile();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidYamlInConfigFile, filePath, ex.Message),
                ex);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidYamlInConfigFile, filePath, ex.Message),
                ex);
        }
    }

    /// <summary>
    /// Returns true if the file name indicates a YAML config file.
    /// </summary>
    internal static bool IsYamlFile(string fileName)
    {
        return string.Equals(fileName, FileNameYaml, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, FileNameYml, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saves the config to the specified directory, preserving the format it was loaded from.
    /// New configs (or those loaded from JSON) are saved as <c>aspire.config.json</c>;
    /// configs loaded from YAML are saved back as YAML.
    /// Uses relaxed JSON escaping so non-ASCII characters (CJK, etc.) are preserved as-is.
    /// </summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var targetFileName = SourceFileName ?? FileName;
        var filePath = Path.Combine(directory, targetFileName);
        var json = JsonSerializer.Serialize(this, JsonSourceGenerationContext.RelaxedEscaping.AspireConfigFile);

        if (IsYamlFile(targetFileName))
        {
            var yaml = YamlJsonConverter.JsonToYaml(json);
            File.WriteAllText(filePath, yaml);
        }
        else
        {
            File.WriteAllText(filePath, json);
        }
    }

    /// <summary>
    /// Loads the aspire config file (JSON, YAML, or YML) from the specified directory,
    /// falling back to legacy .aspire/settings.json + apphost.run.json and migrating if needed.
    /// </summary>
    public static AspireConfigFile LoadOrCreate(string directory, string? defaultSdkVersion = null)
    {
        // Prefer aspire.config.json
        var config = Load(directory);
        if (config is not null)
        {
            if (defaultSdkVersion is not null)
            {
                config.SdkVersion ??= defaultSdkVersion;
            }

            return config;
        }

        // TODO: Remove legacy .aspire/settings.json + apphost.run.json fallback once confident
        // most users have migrated. Tracked by https://github.com/microsoft/aspire/issues/15239
        // Fall back to .aspire/settings.json + apphost.run.json → migrate
        var legacyConfig = AspireJsonConfiguration.Load(directory);
        if (legacyConfig is not null)
        {
            var profiles = ReadApphostRunProfiles(Path.Combine(directory, "apphost.run.json"));
            config = FromLegacy(legacyConfig, profiles);

            // Persist the migrated config (legacy files are kept for older CLI versions)
            config.Save(directory);
        }
        else
        {
            config = new AspireConfigFile();
        }

        if (defaultSdkVersion is not null)
        {
            config.SdkVersion ??= defaultSdkVersion;
        }

        return config;
    }

    /// <summary>
    /// Reads launch profiles from an apphost.run.json file.
    /// </summary>
    /// <remarks>
    /// This is legacy migration code that reads the old apphost.run.json format and converts
    /// it to <see cref="AspireConfigProfile"/> entries. Will be removed once legacy files are
    /// no longer supported. Tracked by https://github.com/microsoft/aspire/issues/15239
    /// </remarks>
    internal static Dictionary<string, AspireConfigProfile>? ReadApphostRunProfiles(string apphostRunPath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(apphostRunPath))
            {
                return null;
            }

            var json = File.ReadAllText(apphostRunPath);
            using var doc = JsonDocument.Parse(json, ConfigurationHelper.ParseOptions);

            if (!doc.RootElement.TryGetProperty("profiles", out var profilesElement))
            {
                return null;
            }

            var profiles = new Dictionary<string, AspireConfigProfile>();
            foreach (var prop in profilesElement.EnumerateObject())
            {
                var profile = new AspireConfigProfile();

                if (prop.Value.TryGetProperty("applicationUrl", out var appUrl) &&
                    appUrl.ValueKind == JsonValueKind.String)
                {
                    profile.ApplicationUrl = appUrl.GetString();
                }

                if (prop.Value.TryGetProperty("environmentVariables", out var envVars) &&
                    envVars.ValueKind == JsonValueKind.Object)
                {
                    profile.EnvironmentVariables = new Dictionary<string, string>();
                    foreach (var envProp in envVars.EnumerateObject())
                    {
                        var envValue = envProp.Value.ValueKind switch
                        {
                            JsonValueKind.String => envProp.Value.GetString()!,
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            JsonValueKind.Number => envProp.Value.GetRawText(),
                            JsonValueKind.Null => "",
                            _ => null
                        };

                        if (envValue is not null)
                        {
                            if (envProp.Value.ValueKind != JsonValueKind.String)
                            {
                                logger?.LogWarning(
                                    "Environment variable '{Name}' has a non-string value ({ValueKind}). Converting to string \"{Value}\".",
                                    envProp.Name, envProp.Value.ValueKind, envValue);
                            }

                            profile.EnvironmentVariables[envProp.Name] = envValue;
                        }
                        else
                        {
                            logger?.LogWarning(
                                "Environment variable '{Name}' has an unsupported value type ({ValueKind}) and will be ignored.",
                                envProp.Name, envProp.Value.ValueKind);
                        }
                    }
                }

                profiles[prop.Name] = profile;
            }

            return profiles.Count > 0 ? profiles : null;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to read launch profiles from {Path}", apphostRunPath);
            return null;
        }
    }

    /// <summary>
    /// Gets the effective SDK version for package-based AppHost preparation.
    /// Falls back to <paramref name="defaultSdkVersion"/> when no SDK version is configured.
    /// </summary>
    public string GetEffectiveSdkVersion(string defaultSdkVersion)
    {
        return string.IsNullOrWhiteSpace(Sdk?.Version) ? defaultSdkVersion : Sdk.Version;
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
    /// Gets all integration references (both NuGet packages and project references)
    /// including the base Aspire.Hosting package.
    /// A value ending in ".csproj" is treated as a project reference; otherwise as a NuGet version.
    /// Empty package versions are resolved to the effective SDK version.
    /// </summary>
    /// <param name="defaultSdkVersion">Default SDK version to use when not configured.</param>
    /// <param name="configDirectory">The directory containing aspire.config.json, used to resolve relative project paths.</param>
    public IEnumerable<IntegrationReference> GetIntegrationReferences(string defaultSdkVersion, string configDirectory)
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
                var absolutePath = Path.GetFullPath(Path.Combine(configDirectory, trimmedValue));
                yield return IntegrationReference.FromProject(packageName, absolutePath);
            }
            else
            {
                // NuGet package reference with explicit version
                yield return IntegrationReference.FromPackage(packageName, trimmedValue);
            }
        }
    }

    /// <summary>
    /// Checks if any aspire config file (JSON, YAML, or YML) exists in the specified directory.
    /// </summary>
    public static bool Exists(string directory)
    {
        foreach (var name in s_allFileNames)
        {
            if (File.Exists(Path.Combine(directory, name)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates from a legacy AspireJsonConfiguration + apphost.run.json.
    /// </summary>
    public static AspireConfigFile FromLegacy(AspireJsonConfiguration? settings, Dictionary<string, AspireConfigProfile>? profiles)
    {
        var config = new AspireConfigFile();

        if (settings is not null)
        {
            config.AppHost = new AspireConfigAppHost
            {
                Path = settings.AppHostPath,
                Language = settings.Language
            };

            if (!string.IsNullOrEmpty(settings.SdkVersion))
            {
                config.Sdk = new AspireConfigSdk { Version = settings.SdkVersion };
            }

            config.Channel = settings.Channel;
            config.Features = settings.Features;
            config.Packages = settings.Packages;
        }

        config.Profiles = profiles;

        return config;
    }
}

/// <summary>
/// AppHost entry point configuration within aspire.config.json.
/// </summary>
internal sealed class AspireConfigAppHost
{
    /// <summary>
    /// Relative path to the AppHost entry point file.
    /// </summary>
    [JsonPropertyName("path")]
    [Description("Relative path to the AppHost entry point file (e.g., \"Program.cs\", \"app.ts\"). Relative to the directory containing aspire.config.json.")]
    public string? Path { get; set; }

    /// <summary>
    /// Language identifier (e.g., "typescript/nodejs", "python").
    /// </summary>
    [JsonPropertyName("language")]
    [Description("Language identifier (e.g., \"typescript/nodejs\", \"python\"). Used to determine which runtime to use for execution.")]
    public string? Language { get; set; }
}

/// <summary>
/// SDK version configuration within aspire.config.json.
/// </summary>
internal sealed class AspireConfigSdk
{
    /// <summary>
    /// The Aspire SDK version.
    /// </summary>
    [JsonPropertyName("version")]
    [Description("The Aspire SDK version. Determines the version of Aspire.Hosting packages to use.")]
    public string? Version { get; set; }
}

/// <summary>
/// Launch profile within aspire.config.json.
/// </summary>
internal sealed class AspireConfigProfile
{
    /// <summary>
    /// Application URLs (e.g., "https://localhost:17000;http://localhost:15000").
    /// </summary>
    [JsonPropertyName("applicationUrl")]
    [Description("Application URLs (e.g., \"https://localhost:17000;http://localhost:15000\").")]
    public string? ApplicationUrl { get; set; }

    /// <summary>
    /// Environment variables for this profile.
    /// </summary>
    [JsonPropertyName("environmentVariables")]
    [Description("Environment variables for this profile.")]
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
}
