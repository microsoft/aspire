// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Describes localized messages used while acquiring an Aspire-skills bundle.
/// </summary>
internal sealed record AspireSkillsBundleInstallerMessages(
    string InstallingStatus,
    string GitHubUnavailable,
    string InvalidBundle,
    string InvalidMetadata,
    string MissingMetadataVersion,
    string MetadataRepositoryMismatch,
    string MissingMetadataTag,
    string MissingMetadataAssetName,
    string MissingMetadataSha512);

/// <summary>
/// Describes one Aspire-skills bundle and the agent asset type it contains.
/// </summary>
internal sealed class AspireSkillsBundleDescriptor
{
    internal AspireSkillsBundleDescriptor(
        AgentAssetKind assetKind,
        string assetPrefix,
        string cacheDirectoryName,
        string displayName,
        string manifestFileName,
        string manifestAssetsPropertyName,
        string embeddedArchiveResourceName,
        string embeddedMetadataResourceName,
        AspireSkillsBundleInstallerMessages messages)
    {
        AssetKind = assetKind;
        AssetPrefix = assetPrefix;
        CacheDirectoryName = cacheDirectoryName;
        DisplayName = displayName;
        ManifestFileName = manifestFileName;
        ManifestAssetsPropertyName = manifestAssetsPropertyName;
        EmbeddedArchiveResourceName = embeddedArchiveResourceName;
        EmbeddedMetadataResourceName = embeddedMetadataResourceName;
        Messages = messages;
    }

    /// <summary>
    /// Gets the descriptor for the bundle containing agent skills.
    /// </summary>
    public static AspireSkillsBundleDescriptor Skills { get; } = new(
        assetKind: AgentAssetKind.Skills,
        assetPrefix: "aspire-skills",
        cacheDirectoryName: "aspire-skills",
        displayName: "Aspire skills",
        manifestFileName: "skill-manifest.json",
        manifestAssetsPropertyName: "skills",
        embeddedArchiveResourceName: "aspire-skills.bundle.tgz",
        embeddedMetadataResourceName: "aspire-skills.metadata.json",
        messages: new AspireSkillsBundleInstallerMessages(
            InstallingStatus: AgentCommandStrings.AspireSkillsInstaller_InstallingStatus,
            GitHubUnavailable: AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable,
            InvalidBundle: AgentCommandStrings.AspireSkillsInstaller_InvalidBundle,
            InvalidMetadata: AgentCommandStrings.AspireSkillsInstaller_InvalidMetadata,
            MissingMetadataVersion: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataVersion,
            MetadataRepositoryMismatch: AgentCommandStrings.AspireSkillsInstaller_MetadataRepositoryMismatch,
            MissingMetadataTag: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataTag,
            MissingMetadataAssetName: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataAssetName,
            MissingMetadataSha512: AgentCommandStrings.AspireSkillsInstaller_MissingMetadataSha512));

    public AgentAssetKind AssetKind { get; }

    public string AssetPrefix { get; }

    public string CacheDirectoryName { get; }

    public string DisplayName { get; }

    public string ManifestFileName { get; }

    public string ManifestAssetsPropertyName { get; }

    public string EmbeddedArchiveResourceName { get; }

    public string EmbeddedMetadataResourceName { get; }

    public AspireSkillsBundleInstallerMessages Messages { get; }

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
