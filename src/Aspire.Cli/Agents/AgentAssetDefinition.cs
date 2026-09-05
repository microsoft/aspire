// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes common selection metadata for an agent asset.
/// </summary>
[DebuggerDisplay("AssetKind = {AssetKind}, Name = {Name}, Description = {Description}, IsDefault = {IsDefault}")]
internal abstract class AgentAssetDefinition
{
    protected AgentAssetDefinition(
        AgentAssetKind assetKind,
        string name,
        string description,
        bool isDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        AssetKind = assetKind;
        Name = name;
        Description = description;
        IsDefault = isDefault;
    }

    /// <summary>
    /// Gets the asset kind.
    /// </summary>
    public AgentAssetKind AssetKind { get; }

    /// <summary>
    /// Gets the asset name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description shown in selection prompts.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets whether the asset should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets whether this asset has the specified name.
    /// </summary>
    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal)
        => string.Equals(Name, name, comparison);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Describes a file-backed agent asset.
/// </summary>
internal sealed class AgentFileAssetDefinition : AgentAssetDefinition
{
    internal AgentFileAssetDefinition(
        AgentAssetKind assetKind,
        string name,
        string description,
        AgentFileAssetSourceKind sourceKind,
        IReadOnlyList<AgentAssetFile> files,
        IReadOnlyList<string> installExcludedRelativePaths,
        bool isDefault,
        AgentExternalInstallerId? externalInstallerId = null,
        IReadOnlyList<string>? applicableLanguages = null)
        : base(assetKind, name, description, isDefault)
    {
        if ((sourceKind is AgentFileAssetSourceKind.ExternalInstaller) != externalInstallerId.HasValue)
        {
            throw new ArgumentException(
                "External installer assets must identify their installer, and other file assets cannot specify one.",
                nameof(externalInstallerId));
        }

        SourceKind = sourceKind;
        Files = [.. files];
        InstallExcludedRelativePaths = [.. installExcludedRelativePaths];
        ExternalInstallerId = externalInstallerId;
        ApplicableLanguages = applicableLanguages is null ? [] : [.. applicableLanguages];
    }

    /// <summary>
    /// Creates a skill asset sourced from the Aspire skills bundle.
    /// </summary>
    internal static AgentFileAssetDefinition CreateAspireSkillsBundle(
        AgentAssetKind assetKind,
        string name,
        string description,
        IReadOnlyList<string>? installExcludedRelativePaths = null,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        return new(
            assetKind,
            name,
            description,
            AgentFileAssetSourceKind.AspireSkillsBundle,
            files: [],
            installExcludedRelativePaths: installExcludedRelativePaths ?? [],
            isDefault: true,
            applicableLanguages: applicableLanguages);
    }

    /// <summary>
    /// Gets where the asset's installable content comes from.
    /// </summary>
    public AgentFileAssetSourceKind SourceKind { get; }

    /// <summary>
    /// Gets files stored directly on the asset definition.
    /// </summary>
    public IReadOnlyList<AgentAssetFile> Files { get; }

    /// <summary>
    /// Gets the dedicated installer for an externally installed asset.
    /// </summary>
    public AgentExternalInstallerId? ExternalInstallerId { get; }

    /// <summary>
    /// Gets relative paths that should be excluded when the asset is installed.
    /// </summary>
    public IReadOnlyList<string> InstallExcludedRelativePaths { get; }

    /// <summary>
    /// Gets the language identifiers to which this asset applies.
    /// </summary>
    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Gets whether a bundled file should be installed.
    /// </summary>
    public bool ShouldInstallFile(string relativePath)
    {
        foreach (var excludedPath in InstallExcludedRelativePaths)
        {
            if (PathMatchesOrIsUnder(relativePath, excludedPath))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether this asset applies to the detected language.
    /// </summary>
    public bool IsApplicableToLanguage(LanguageId? detectedLanguage)
    {
        if (ApplicableLanguages.Count == 0)
        {
            return true;
        }

        if (detectedLanguage is null)
        {
            return false;
        }

        return ApplicableLanguages.Any(language =>
            string.Equals(language, detectedLanguage.Value.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathMatchesOrIsUnder(string relativePath, string excludedPath)
    {
        if (string.Equals(relativePath, excludedPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!relativePath.StartsWith(excludedPath, StringComparison.Ordinal))
        {
            return false;
        }

        return relativePath.Length > excludedPath.Length &&
            relativePath[excludedPath.Length] == Path.DirectorySeparatorChar;
    }
}

/// <summary>
/// Describes an agent asset applied through detected environment actions.
/// </summary>
internal sealed class AgentActionAssetDefinition : AgentAssetDefinition
{
    internal AgentActionAssetDefinition(
        AgentAssetKind assetKind,
        string name,
        string description,
        bool isDefault)
        : base(assetKind, name, description, isDefault)
    {
    }
}

/// <summary>
/// Identifies where a file-backed agent asset's content is sourced from.
/// </summary>
internal enum AgentFileAssetSourceKind
{
    /// <summary>
    /// The asset is represented by files compiled into the CLI.
    /// </summary>
    Static,

    /// <summary>
    /// The asset is installed from the external Aspire skills bundle.
    /// </summary>
    AspireSkillsBundle,

    /// <summary>
    /// The asset is managed by a dedicated external installer.
    /// </summary>
    ExternalInstaller,
}

/// <summary>
/// Identifies dedicated installers for externally installed agent assets.
/// </summary>
internal enum AgentExternalInstallerId
{
    /// <summary>
    /// Playwright CLI and its Skill files.
    /// </summary>
    PlaywrightCli,
}
