// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Commands;

/// <summary>
/// Information about Aspire configuration files and available features.
/// </summary>
/// <param name="LocalSettingsPath">Path to the local settings file (.aspire/settings.json).</param>
/// <param name="GlobalSettingsPath">Path to the global settings file (~/.aspire/globalsettings.json).</param>
/// <param name="AvailableFeatures">List of all available feature metadata.</param>
/// <param name="LocalSettingsSchema">Schema for the local settings.json file structure (includes all properties).</param>
/// <param name="GlobalSettingsSchema">Schema for the global settings.json file structure (excludes local-only properties).</param>
/// <param name="ConfigFileSchema">Schema for the aspire.config.json file structure.</param>
/// <param name="Capabilities">List of CLI capabilities advertised to extensions.</param>
internal sealed record ConfigInfo(
    string LocalSettingsPath,
    string GlobalSettingsPath,
    List<FeatureInfo> AvailableFeatures,
    SettingsSchema LocalSettingsSchema,
    SettingsSchema GlobalSettingsSchema,
    SettingsSchema? ConfigFileSchema,
    string[] Capabilities);

/// <summary>
/// Information about a single feature flag.
/// </summary>
/// <param name="Name">The feature flag name (without the "features." prefix).</param>
/// <param name="Description">A description of what the feature does.</param>
/// <param name="DefaultValue">The default value if not explicitly configured.</param>
internal sealed record FeatureInfo(string Name, string Description, bool DefaultValue);

/// <summary>
/// Schema information for the settings.json file structure.
/// </summary>
/// <param name="Properties">List of top-level properties in the settings file.</param>
internal sealed record SettingsSchema(List<PropertyInfo> Properties);

/// <summary>
/// Information about a single property in the settings schema.
/// </summary>
/// <param name="Name">The JSON property name.</param>
/// <param name="Type">The property type (e.g., "string", "boolean", "object", "array").</param>
/// <param name="Description">A description of what the property does.</param>
/// <param name="Required">Whether the property is required.</param>
/// <param name="SubProperties">For object-typed properties with a known shape, the nested property definitions.</param>
/// <param name="AdditionalPropertiesType">For dictionary-typed properties, the JSON type of the values (e.g., "string").</param>
internal sealed record PropertyInfo(
    string Name,
    string Type,
    string Description,
    bool Required,
    List<PropertyInfo>? SubProperties = null,
    string? AdditionalPropertiesType = null);
