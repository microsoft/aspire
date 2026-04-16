// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Configuration;

/// <summary>
/// Represents the aspire.config.json configuration file.
/// Consolidates apphost location, launch settings, and CLI config into one file.
/// </summary>
/// <remarks>
/// <para>The new unified format (<c>aspire.config.json</c>) replaces the legacy split across
/// <c>.aspire/settings.json</c> (local settings) and <c>apphost.run.json</c> (launch profiles).</para>
/// <para>Example <c>aspire.config.json</c>:</para>
/// <code>
/// {
///   "appHost": { "path": "app.ts", "language": "typescript/nodejs" },
///   "sdk": { "version": "9.2.0" },
///   "channel": "stable",
///   "docs": {
///     "llmsTxtUrl": "https://aspire.dev/llms-small.txt",
///     "api": { "sitemapUrl": "https://aspire.dev/sitemap-0.xml" }
///   },
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
    /// Documentation source configuration for aspire docs and aspire docs api.
    /// </summary>
    [JsonPropertyName("docs")]
    [Description("Documentation source overrides for aspire docs and aspire docs api. Leave these unset to use the built-in aspire.dev sources.")]
    public AspireConfigDocs? Docs { get; set; }

    /// <summary>
    /// Launch profiles (ports, env vars). Replaces apphost.run.json.
    /// </summary>
    [JsonPropertyName("profiles")]
    [Description("Launch profiles (ports, environment variables). Replaces apphost.run.json.")]
    public Dictionary<string, AspireConfigProfile>? Profiles { get; set; }

    /// <summary>
    /// Package references for non-first-class languages.
    /// </summary>
    /// <remarks>
    /// Each entry value is either a <b>string</b> (short form, always NuGet — empty means the
    /// SDK version, non-empty is an explicit version) or an <b>object</b> (long form, carries
    /// a <c>source</c> discriminator and per-source fields).
    ///
    /// <para>String form examples:</para>
    /// <code>
    /// "Aspire.Hosting.Redis": "",          // NuGet, SDK version
    /// "Aspire.Hosting.Kafka": "9.2.0"      // NuGet, explicit version
    /// </code>
    ///
    /// <para>Object form examples:</para>
    /// <code>
    /// "Aspire.Hosting.Redis":   { "source": "nuget",   "version": "9.2.0" }
    /// "Aspire.Hosting.Local":   { "source": "project", "path": "../Local/Local.csproj" }
    /// "@spike/aspire-kafka":    { "source": "npm",     "path": "./kafka-integration/host.ts" }
    /// </code>
    ///
    /// Dispatched per-source by <see cref="GetIntegrationReferences"/>. Adding a new ecosystem
    /// means one new <see cref="IntegrationSource"/> value plus one parsing branch here.
    /// </remarks>
    [JsonPropertyName("packages")]
    [Description("Package references for the AppHost. Value is either a string (NuGet, short form — empty = SDK version, otherwise explicit version) or an object with a required \"source\" discriminator (\"nuget\", \"project\", or \"npm\") and per-source fields (version for nuget, path for project/npm).")]
    public Dictionary<string, PackageEntry>? Packages { get; set; }

    /// <summary>
    /// Loads aspire.config.json from the specified directory.
    /// </summary>
    /// <returns>The deserialized config, or <c>null</c> if the file does not exist.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the file exists but contains invalid JSON.</exception>
    public static AspireConfigFile? Load(string directory)
    {
        var filePath = Path.Combine(directory, FileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

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

    /// <summary>
    /// Saves aspire.config.json to the specified directory.
    /// Uses relaxed JSON escaping so non-ASCII characters (CJK, etc.) are preserved as-is.
    /// </summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, FileName);
        var json = JsonSerializer.Serialize(this, JsonSourceGenerationContext.RelaxedEscaping.AspireConfigFile);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Loads aspire.config.json from the specified directory, falling back to legacy
    /// .aspire/settings.json + apphost.run.json and migrating if needed.
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

            // Legacy .aspire/settings.json stores appHostPath relative to the .aspire/ directory,
            // but aspire.config.json stores it relative to the config file's own directory (the parent
            // of .aspire/). Re-base the path so it resolves correctly from the new location.
            // Paths are always stored with '/' separators regardless of platform, but we normalize
            // to the OS separator for Path operations and back to '/' for storage.
            if (config.AppHost?.Path is { Length: > 0 } migratedPath && !Path.IsPathRooted(migratedPath))
            {
                var legacySettingsDir = Path.Combine(directory, AspireJsonConfiguration.SettingsFolder);
                var absolutePath = PathNormalizer.NormalizePathForCurrentPlatform(
                    Path.Combine(legacySettingsDir, migratedPath));
                config.AppHost.Path = PathNormalizer.NormalizePathForStorage(
                    Path.GetRelativePath(directory, absolutePath));
            }

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
    /// Adds or updates a NuGet package reference. Always writes a NuGet entry — the object
    /// form is reserved for entries that can't be expressed as a bare version (project
    /// references, npm integrations) and is written by hand.
    /// </summary>
    public void AddOrUpdatePackage(string packageId, string version)
    {
        Packages ??= [];
        Packages[packageId] = PackageEntry.Nuget(version);
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
    /// Gets all integration references including the base <c>Aspire.Hosting</c> package.
    /// Per-entry string-or-object parsing is handled by <see cref="PackageEntryConverter"/>;
    /// this method just materializes each <see cref="PackageEntry"/> into an
    /// <see cref="IntegrationReference"/> with absolute paths resolved against
    /// <paramref name="configDirectory"/>.
    /// </summary>
    public IEnumerable<IntegrationReference> GetIntegrationReferences(string defaultSdkVersion, string configDirectory)
    {
        var sdkVersion = GetEffectiveSdkVersion(defaultSdkVersion);

        // Base package always included
        yield return IntegrationReference.FromPackage("Aspire.Hosting", sdkVersion);

        if (Packages is null)
        {
            yield break;
        }

        foreach (var (packageName, entry) in Packages)
        {
            // Skip base packages and SDK-only packages
            if (string.Equals(packageName, "Aspire.Hosting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(packageName, "Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return entry.Source switch
            {
                IntegrationSource.Nuget => IntegrationReference.FromPackage(
                    packageName,
                    string.IsNullOrWhiteSpace(entry.Version) ? sdkVersion : entry.Version),

                IntegrationSource.Project => IntegrationReference.FromProject(
                    packageName,
                    Path.GetFullPath(Path.Combine(configDirectory, entry.Path!))),

                IntegrationSource.Npm => IntegrationReference.FromNpm(
                    packageName,
                    Path.GetFullPath(Path.Combine(configDirectory, entry.Path!))),

                _ => throw new InvalidOperationException(
                    $"Package '{packageName}' has unhandled source '{entry.Source}'.")
            };
        }
    }

    /// <summary>
    /// Checks if aspire.config.json exists in the specified directory.
    /// </summary>
    public static bool Exists(string directory)
    {
        return File.Exists(Path.Combine(directory, FileName));
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

            // Legacy settings.json stored packages as Dictionary<string,string> with an
            // ad-hoc ".csproj" suffix for project references. Translate to strong entries.
            if (settings.Packages is not null)
            {
                config.Packages = new Dictionary<string, PackageEntry>();
                foreach (var (name, value) in settings.Packages)
                {
                    var trimmed = value?.Trim();
                    config.Packages[name] = trimmed is not null &&
                                            trimmed.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        ? PackageEntry.Project(trimmed)
                        : PackageEntry.Nuget(string.IsNullOrEmpty(trimmed) ? null : trimmed);
                }
            }
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
/// Documentation source configuration within aspire.config.json.
/// </summary>
internal sealed class AspireConfigDocs
{
    /// <summary>
    /// Optional override for the llms.txt documentation source consumed by aspire docs.
    /// </summary>
    [JsonPropertyName("llmsTxtUrl")]
    [Description($"""Optional override for the llms.txt documentation source consumed by aspire docs. Defaults to {DocsSourceConfiguration.DefaultLlmsTxtUrl}.""")]
    public string? LlmsTxtUrl { get; set; }

    /// <summary>
    /// Optional API reference source overrides consumed by aspire docs api.
    /// </summary>
    [JsonPropertyName("api")]
    [Description("Optional API reference source overrides consumed by aspire docs api. Leave these unset to use the built-in aspire.dev API sources.")]
    public AspireConfigApiDocs? Api { get; set; }
}

/// <summary>
/// API documentation source configuration within aspire.config.json.
/// </summary>
internal sealed class AspireConfigApiDocs
{
    /// <summary>
    /// Optional override for the API sitemap consumed by aspire docs api.
    /// </summary>
    [JsonPropertyName("sitemapUrl")]
    [Description($"""Optional override for the API sitemap consumed by aspire docs api. Defaults to {ApiDocsSourceConfiguration.DefaultSitemapUrl}.""")]
    public string? SitemapUrl { get; set; }
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
