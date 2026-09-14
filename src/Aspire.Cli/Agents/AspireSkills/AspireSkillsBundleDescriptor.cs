// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Defines immutable bundle identity, layout, and required-file validation.
/// </summary>
internal sealed class AspireSkillsBundleDescriptor
{
    public required AgentAssetKind AssetKind { get; init; }

    public required string AssetKindName { get; init; }

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

    public required string EmbeddedArchiveResourceName { get; init; }

    public required string EmbeddedMetadataResourceName { get; init; }
}
