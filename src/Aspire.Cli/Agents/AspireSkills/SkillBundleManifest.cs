// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Describes a published bundle from aspire-skills.
/// </summary>
internal sealed class BundleManifest
{
    public string? Version { get; init; }

    public BundleSupports? Supports { get; init; }

    public BundleAsset[] Assets { get; init; } = [];
}

/// <summary>
/// Represents the JSON document for a published bundle manifest.
/// </summary>
internal sealed class BundleManifestDocument
{
    public string? Version { get; init; }

    public BundleSupports? Supports { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AssetCollections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public BundleManifest ToManifest(string assetsPropertyName)
    {
        var assets = AssetCollections.TryGetValue(assetsPropertyName, out var assetsJson)
            ? assetsJson.Deserialize(AspireSkillsJsonSerializerContext.Default.BundleAssetArray) ?? []
            : [];

        return new BundleManifest
        {
            Version = Version,
            Supports = Supports,
            Assets = assets
        };
    }
}

/// <summary>
/// Describes the Aspire versions supported by a bundle from aspire-skills.
/// </summary>
internal sealed class BundleSupports
{
    public string? AspireCli { get; init; }

    public string? AspireSdk { get; init; }
}

/// <summary>
/// Describes a single asset in a bundle from aspire-skills.
/// </summary>
internal sealed class BundleAsset
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public string[] ApplicableLanguages { get; init; } = [];

    public string[] InstallExcludedRelativePaths { get; init; } = [];

    public BundleFile[] Files { get; init; } = [];
}

/// <summary>
/// Describes a single file in a bundle from aspire-skills.
/// </summary>
internal sealed class BundleFile
{
    public string? RelativePath { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>
/// Describes the Aspire skills bundle archive embedded in the CLI.
/// </summary>
internal sealed class EmbeddedBundleMetadata
{
    public string? Version { get; init; }

    public string? Repository { get; init; }

    public string? Tag { get; init; }

    public string? AssetName { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>
/// Source-generation context for Aspire skills bundle JSON.
/// </summary>
[JsonSourceGenerationOptions(
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BundleManifestDocument))]
[JsonSerializable(typeof(BundleSupports))]
[JsonSerializable(typeof(BundleAsset[]))]
[JsonSerializable(typeof(EmbeddedBundleMetadata))]
internal sealed partial class AspireSkillsJsonSerializerContext : JsonSerializerContext
{
}
