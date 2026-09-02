// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

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
        bool hasInstallableFiles = false,
        IReadOnlyList<string>? applicableLanguages = null)
        : base(assetKind, name, description, isDefault)
    {
        if (assetKind.GetBackingKind() is not AgentAssetBackingKind.File)
        {
            throw new ArgumentException($"Agent asset kind '{assetKind}' is not file-backed.", nameof(assetKind));
        }

        SourceKind = sourceKind;
        Files = [.. files];
        InstallExcludedRelativePaths = [.. installExcludedRelativePaths];
        HasInstallableFiles = hasInstallableFiles || Files.Count > 0;
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
            hasInstallableFiles: true,
            applicableLanguages);
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
    /// Gets whether the asset has files that <c>aspire agent init</c> installs directly.
    /// </summary>
    public bool HasInstallableFiles { get; }

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
        if (assetKind.GetBackingKind() is not AgentAssetBackingKind.Action)
        {
            throw new ArgumentException($"Agent asset kind '{assetKind}' is not action-backed.", nameof(assetKind));
        }
    }
}

/// <summary>
/// Provides the agent assets defined directly by the CLI.
/// </summary>
internal static class AgentAssetCatalog
{
    /// <summary>
    /// The Playwright CLI skill for browser automation.
    /// </summary>
    public static readonly AgentFileAssetDefinition PlaywrightCli = new(
        AgentAssetKind.Skill,
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        AgentFileAssetSourceKind.ExternalInstaller,
        files: [],
        installExcludedRelativePaths: [],
        isDefault: false);

    /// <summary>
    /// The dotnet-inspect skill for querying .NET API surfaces.
    /// </summary>
    public static readonly AgentFileAssetDefinition DotnetInspect = new(
        AgentAssetKind.Skill,
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        AgentFileAssetSourceKind.Static,
        files: [new AgentAssetFile("SKILL.md", CommonAgentApplicators.DotnetInspectSkillFileContent)],
        installExcludedRelativePaths: [],
        isDefault: false,
        applicableLanguages: [KnownLanguageId.CSharp]);

    /// <summary>
    /// The Aspire MCP server configuration applied to detected agent environments.
    /// </summary>
    public static readonly AgentActionAssetDefinition AspireMcpServer = new(
        AgentAssetKind.Mcp,
        "aspire",
        AgentCommandStrings.InitCommand_ConfigureMcpServer,
        isDefault: false);

    /// <summary>
    /// Gets every agent asset defined directly by the CLI.
    /// </summary>
    public static IReadOnlyList<AgentAssetDefinition> All { get; } =
        [PlaywrightCli, DotnetInspect, AspireMcpServer];

    /// <summary>
    /// Gets file-backed assets of the specified kind.
    /// </summary>
    public static IReadOnlyList<AgentFileAssetDefinition> GetFileAssets(AgentAssetKind assetKind)
        => All.OfType<AgentFileAssetDefinition>()
            .Where(asset => asset.AssetKind == assetKind)
            .ToList();

    /// <summary>
    /// Gets action-backed assets of the specified kind.
    /// </summary>
    public static IReadOnlyList<AgentActionAssetDefinition> GetActionAssets(AgentAssetKind assetKind)
        => All.OfType<AgentActionAssetDefinition>()
            .Where(asset => asset.AssetKind == assetKind)
            .ToList();
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
