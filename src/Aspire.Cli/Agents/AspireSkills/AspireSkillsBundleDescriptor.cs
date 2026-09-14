// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Defines immutable bundle identity, layout, and required-file validation.
/// </summary>
internal sealed class AspireSkillsBundleDescriptor
{
    public static AspireSkillsBundleDescriptor Skills { get; } = new()
    {
        TelemetryName = "skills",
        AssetPrefix = "aspire-skills",
        CacheDirectoryName = "aspire-skills",
        DisplayName = "Aspire skills",
        ManifestFileName = "skill-manifest.json",
        ManifestAssetsPropertyName = "skills",
        ContentRootDirectoryName = "skills",
        RequiredFileName = "SKILL.md",
        ValidateRequiredFile = SkillFileValidator.Validate,
        DecodeFilesAsText = true,
        RequirePortablePaths = false,
        UnavailableMessage = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable, "Aspire skills"),
        EmbeddedArchiveResourceName = "aspire-skills.bundle.tgz",
        EmbeddedMetadataResourceName = "aspire-skills.metadata.json",
    };

    public static AspireSkillsBundleDescriptor Extensions { get; } = new()
    {
        TelemetryName = "extensions",
        AssetPrefix = "aspire-extensions",
        CacheDirectoryName = "aspire-extensions",
        DisplayName = "Aspire extensions",
        ManifestFileName = "extension-manifest.json",
        ManifestAssetsPropertyName = "extensions",
        ContentRootDirectoryName = "extensions",
        RequiredFileName = "extension.mjs",
        ValidateRequiredFile = static (_, _) => { },
        DecodeFilesAsText = false,
        RequirePortablePaths = true,
        UnavailableMessage = AgentCommandStrings.InitCommand_ExtensionBundleUnavailable,
        EmbeddedArchiveResourceName = "aspire-extensions.bundle.tgz",
        EmbeddedMetadataResourceName = "aspire-extensions.metadata.json",
    };

    public required string TelemetryName { get; init; }

    public required string AssetPrefix { get; init; }

    public required string CacheDirectoryName { get; init; }

    public required string DisplayName { get; init; }

    public required string ManifestFileName { get; init; }

    public required string ManifestAssetsPropertyName { get; init; }

    public required string ContentRootDirectoryName { get; init; }

    public required string RequiredFileName { get; init; }

    /// <summary>
    /// Validates an asset's required file using its name and file contents.
    /// </summary>
    public required Action<string, ReadOnlySpan<byte>> ValidateRequiredFile { get; init; }

    public required bool DecodeFilesAsText { get; init; }

    public required bool RequirePortablePaths { get; init; }

    public required string UnavailableMessage { get; init; }

    public required string EmbeddedArchiveResourceName { get; init; }

    public required string EmbeddedMetadataResourceName { get; init; }
}
