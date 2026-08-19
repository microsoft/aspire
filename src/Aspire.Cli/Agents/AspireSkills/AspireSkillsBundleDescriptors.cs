// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Defines the Aspire-skills bundle descriptors consumed by the CLI.
/// </summary>
internal static class AspireSkillsBundleDescriptors
{
    /// <summary>
    /// Gets the descriptor for the bundle containing agent skills.
    /// </summary>
    public static AspireSkillsBundleDescriptor Skills { get; } = new(
        AssetKind: AgentAssetKind.Skills,
        AssetPrefix: "aspire-skills",
        CacheDirectoryName: "aspire-skills",
        DisplayName: "Aspire skills",
        ManifestFileName: "skill-manifest.json",
        ManifestAssetsPropertyName: "skills",
        EmbeddedArchiveResourceName: "aspire-skills.bundle.tgz",
        EmbeddedMetadataResourceName: "aspire-skills.metadata.json",
        Messages: new AspireSkillsBundleInstallerMessages(
            InstallingStatus: AgentCommandStrings.AspireSkillsInstaller_InstallingStatus,
            GitHubUnavailable: AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable,
            InvalidBundle: AgentCommandStrings.AspireSkillsInstaller_InvalidBundle,
            InvalidMetadata: AgentCommandStrings.AspireSkillsInstaller_InvalidMetadata,
            MissingMetadataVersion: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataVersion,
            MetadataRepositoryMismatch: AgentCommandStrings.AspireSkillsInstaller_MetadataRepositoryMismatch,
            MissingMetadataTag: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataTag,
            MissingMetadataAssetName: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataAssetName,
            MissingMetadataSha512: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataSha512));

    /// <summary>
    /// Gets the descriptor for the specified agent asset kind.
    /// </summary>
    public static AspireSkillsBundleDescriptor Get(AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skills => Skills,
            _ => throw new ArgumentOutOfRangeException(nameof(assetKind), assetKind, "Unsupported agent asset kind."),
        };
    }
}
