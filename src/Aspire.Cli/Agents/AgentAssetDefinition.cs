// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents an agent skill or extension and its installable files.
/// </summary>
[DebuggerDisplay("AssetKind = {AssetKind}, Name = {Name}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetDefinition
{
    /// <summary>
    /// The Playwright CLI skill, managed by its external installer.
    /// </summary>
    public static readonly AgentAssetDefinition PlaywrightCli = new(
        AgentAssetKind.Skill,
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        AgentAssetSourceKind.ExternalInstaller,
        files: [],
        installExcludedRelativePaths: [],
        isDefault: false);

    /// <summary>
    /// The dotnet-inspect skill, offered only for .NET AppHosts.
    /// </summary>
    public static readonly AgentAssetDefinition DotnetInspect = new(
        AgentAssetKind.Skill,
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        AgentAssetSourceKind.Static,
        files: [new AgentAssetFile("SKILL.md", CommonAgentApplicators.DotnetInspectSkillFileContent)],
        installExcludedRelativePaths: [],
        isDefault: false,
        applicableLanguages: [KnownLanguageId.CSharp]);

    /// <summary>
    /// Gets CLI-defined assets that are not sourced from a bundle.
    /// </summary>
    public static IReadOnlyList<AgentAssetDefinition> CliDefined { get; } = [PlaywrightCli, DotnetInspect];

    internal AgentAssetDefinition(
        AgentAssetKind assetKind,
        string name,
        string description,
        AgentAssetSourceKind sourceKind,
        IReadOnlyList<AgentAssetFile> files,
        IReadOnlyList<string> installExcludedRelativePaths,
        bool isDefault,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        AssetKind = assetKind;
        Name = name;
        Description = description;
        SourceKind = sourceKind;
        Files = [.. files];
        InstallExcludedRelativePaths = [.. installExcludedRelativePaths];
        IsDefault = isDefault;
        ApplicableLanguages = applicableLanguages is null ? [] : [.. applicableLanguages];
    }

    /// <summary>
    /// Creates a bundle-sourced asset, selected by default in all installation flows.
    /// </summary>
    internal static AgentAssetDefinition CreateAspireSkillsBundle(
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
            AgentAssetSourceKind.AspireSkillsBundle,
            files: [],
            installExcludedRelativePaths: installExcludedRelativePaths ?? [],
            isDefault: true,
            applicableLanguages);
    }

    public AgentAssetKind AssetKind { get; }

    public string Name { get; }

    public string Description { get; }

    public AgentAssetSourceKind SourceKind { get; }

    /// <summary>
    /// Gets files stored directly on this definition.
    /// </summary>
    public IReadOnlyList<AgentAssetFile> Files { get; }

    public bool HasInstallableFiles => Files.Count > 0 || SourceKind is AgentAssetSourceKind.AspireSkillsBundle;

    public IReadOnlyList<string> InstallExcludedRelativePaths { get; }

    public bool IsDefault { get; }

    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Gets whether a bundled file should be installed.
    /// </summary>
    public bool ShouldInstallFile(string relativePath)
    {
        foreach (var excludedPath in InstallExcludedRelativePaths)
        {
            if (string.Equals(relativePath, excludedPath, StringComparison.Ordinal) ||
                (relativePath.StartsWith(excludedPath, StringComparison.Ordinal) &&
                 relativePath.Length > excludedPath.Length &&
                 relativePath[excludedPath.Length] == Path.DirectorySeparatorChar))
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
        return ApplicableLanguages.Count == 0 ||
            (detectedLanguage is not null && ApplicableLanguages.Any(language =>
                string.Equals(language, detectedLanguage.Value.Value, StringComparison.OrdinalIgnoreCase)));
    }

    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal)
        => string.Equals(Name, name, comparison);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Identifies where an agent asset's files are sourced.
/// </summary>
internal enum AgentAssetSourceKind
{
    Static,
    AspireSkillsBundle,
    ExternalInstaller,
}
