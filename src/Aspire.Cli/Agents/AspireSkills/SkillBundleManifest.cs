// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Describes a published Aspire skills bundle.
/// </summary>
internal sealed class SkillBundleManifest
{
    public string? Version { get; init; }

    public SkillBundleSupports? Supports { get; init; }

    public SkillBundleSkill[] Skills { get; init; } = [];
}

/// <summary>
/// Describes the Aspire versions supported by a skills bundle.
/// </summary>
internal sealed class SkillBundleSupports
{
    public string? AspireCli { get; init; }

    public string? AspireSdk { get; init; }
}

/// <summary>
/// Describes a single skill in an Aspire skills bundle.
/// </summary>
internal sealed class SkillBundleSkill
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public bool IsDefault { get; init; }

    public string[] ApplicableLanguages { get; init; } = [];

    public string[] InstallExcludedRelativePaths { get; init; } = [];

    public SkillBundleFile[] Files { get; init; } = [];
}

/// <summary>
/// Describes a single file in an Aspire skills bundle.
/// </summary>
internal sealed class SkillBundleFile
{
    public string? RelativePath { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>
/// Describes the Aspire skills bundle archive embedded in the CLI.
/// </summary>
internal sealed class EmbeddedAspireSkillsBundleMetadata
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
[JsonSerializable(typeof(SkillBundleManifest))]
[JsonSerializable(typeof(EmbeddedAspireSkillsBundleMetadata))]
internal sealed partial class AspireSkillsJsonSerializerContext : JsonSerializerContext
{
}
