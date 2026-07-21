// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents an agent asset that can be installed into a location.
/// </summary>
[DebuggerDisplay("Name = {Name}, Description = {Description}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetDefinition
{
    /// <summary>
    /// The Playwright CLI skill for browser automation.
    /// </summary>
    public static readonly AgentAssetDefinition PlaywrightCli = new(
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        assetContent: null,
        assetType: AgentAssetKind.Skill,
        sourceKind: AgentAssetSourceKind.ExternalInstaller, // Playwright is installed via PlaywrightCliInstaller, not a static file
        installExcludedRelativePaths: [],
        isDefault: false);

    /// <summary>
    /// The dotnet-inspect skill for querying .NET API surfaces.
    /// Only offered when the workspace contains a .NET AppHost.
    /// </summary>
    public static readonly AgentAssetDefinition DotnetInspect = new(
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        CommonAgentApplicators.DotnetInspectSkillFileContent,
        sourceKind: AgentAssetSourceKind.Static,
        assetType: AgentAssetKind.Skill,
        installExcludedRelativePaths: [],
        isDefault: false,
        applicableLanguages: [KnownLanguageId.CSharp]);

    /// <summary>
    /// Creates an asset definition sourced from the Aspire skills bundle. All bundle-sourced
    /// assets are pre-selected by default in the install prompt; callers like <c>aspire new</c>
    /// and standalone <c>aspire agent init</c> can still narrow that set with a predicate
    /// (see <c>AgentInitCommand.ExcludeOneTimeSetupAgentAssetsFromDefaults</c>).
    /// </summary>
    internal static AgentAssetDefinition CreateAspireSkillsBundle(
        string name,
        string description,
        AgentAssetKind assetType,
        IReadOnlyList<string>? installExcludedRelativePaths = null,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        return new AgentAssetDefinition(
            name,
            description,
            assetContent: null,
            assetType: assetType,
            sourceKind: AgentAssetSourceKind.AspireSkillsBundle,
            installExcludedRelativePaths: installExcludedRelativePaths ?? [],
            isDefault: true,
            applicableLanguages);
    }

    private AgentAssetDefinition(string name, string description, string? assetContent, AgentAssetKind assetType, AgentAssetSourceKind sourceKind, IReadOnlyList<string> installExcludedRelativePaths, bool isDefault, IReadOnlyList<string>? applicableLanguages = null)
    {
        Name = name;
        Description = description;
        AssetContent = assetContent;
        AssetType = assetType;
        SourceKind = sourceKind;
        InstallExcludedRelativePaths = installExcludedRelativePaths;
        IsDefault = isDefault;
        ApplicableLanguages = applicableLanguages ?? [];
    }

    /// <summary>
    /// Gets the skill name (used as the folder name under skill locations).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description shown in the selection prompt.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the content for the top-level asset file when the asset is defined as a single-file bundle.
    /// </summary>
    public string? AssetContent { get; }

    /// <summary>
    /// Gets the type of asset being installed.
    /// </summary>
    public AgentAssetKind AssetType { get; }

    /// <summary>
    /// Gets where the installable files for this asset come from.
    /// </summary>
    public AgentAssetSourceKind SourceKind { get; }

    /// <summary>
    /// Gets whether this asset has files that <c>aspire agent init</c> installs directly.
    /// </summary>
    public bool HasInstallableFiles => AssetContent is not null || SourceKind is AgentAssetSourceKind.AspireSkillsBundle;

    /// <summary>
    /// Gets relative paths that should be excluded when the asset is installed into a workspace.
    /// </summary>
    public IReadOnlyList<string> InstallExcludedRelativePaths { get; }

    /// <summary>
    /// Gets whether a bundled file for this asset should be installed into a workspace.
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
    /// Gets whether this asset should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets the set of language identifiers (from <see cref="KnownLanguageId"/>) this asset applies to.
    /// An empty list means the asset is language-agnostic and always offered.
    /// When non-empty, the asset is only offered when the detected language matches one of the entries.
    /// </summary>
    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Returns whether this asset is applicable for the given detected language.
    /// An asset with no <see cref="ApplicableLanguages"/> restrictions is always applicable.
    /// An asset with restrictions is only applicable when the detected language matches one of the entries.
    /// When no language is detected (<paramref name="detectedLanguage"/> is <c>null</c>), language-restricted assets are excluded.
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

        return ApplicableLanguages.Any(l => string.Equals(l, detectedLanguage.Value.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns whether this asset has the specified name.
    /// </summary>
    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal) => string.Equals(Name, name, comparison);

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

        return relativePath.Length > excludedPath.Length && relativePath[excludedPath.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Gets CLI-defined assets that are not sourced from the Aspire skills bundle.
    /// </summary>
    public static IReadOnlyList<AgentAssetDefinition> CliDefined { get; } = [PlaywrightCli, DotnetInspect];

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Identifies where asset files are sourced from.
/// </summary>
internal enum AgentAssetSourceKind
{
    /// <summary>
    /// The asset is represented by static content compiled into the CLI.
    /// </summary>
    Static,

    /// <summary>
    /// The asset is installed from the external Aspire skills bundle.
    /// </summary>
    AspireSkillsBundle,

    /// <summary>
    /// The asset is managed by a dedicated external installer.
    /// </summary>
    ExternalInstaller
}
