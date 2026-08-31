// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes an agent asset that can be selected and installed.
/// </summary>
[DebuggerDisplay("AssetKind = {AssetKind}, Name = {Name}, Description = {Description}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetDefinition
{
    /// <summary>
    /// The Playwright CLI skill for browser automation.
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
    /// The dotnet-inspect skill for querying .NET API surfaces.
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
    /// The Aspire MCP server configuration applied to detected agent environments.
    /// </summary>
    public static readonly AgentAssetDefinition AspireMcpServer = new(
        AgentAssetKind.Mcp,
        "aspire-mcp-server",
        AgentCommandStrings.InitCommand_ConfigureMcpServer,
        AgentAssetSourceKind.Action,
        files: [],
        installExcludedRelativePaths: [],
        isDefault: false);

    private AgentAssetDefinition(
        AgentAssetKind assetKind,
        string name,
        string description,
        AgentAssetSourceKind sourceKind,
        IReadOnlyList<AgentAssetFile> files,
        IReadOnlyList<string> installExcludedRelativePaths,
        bool isDefault,
        bool hasInstallableFiles = false,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        AssetKind = assetKind;
        Name = name;
        Description = description;
        SourceKind = sourceKind;
        Files = [.. files];
        InstallExcludedRelativePaths = [.. installExcludedRelativePaths];
        IsDefault = isDefault;
        HasInstallableFiles = hasInstallableFiles || Files.Count > 0;
        ApplicableLanguages = applicableLanguages is null ? [] : [.. applicableLanguages];
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
    /// Gets where the asset's installable content comes from.
    /// </summary>
    public AgentAssetSourceKind SourceKind { get; }

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
    /// Gets whether the asset should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets the language identifiers to which this asset applies.
    /// </summary>
    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Gets agent assets defined directly by the CLI.
    /// </summary>
    public static IReadOnlyList<AgentAssetDefinition> CliDefined { get; } =
        [PlaywrightCli, DotnetInspect, AspireMcpServer];

    /// <summary>
    /// Gets CLI-defined assets of the specified kind.
    /// </summary>
    public static IReadOnlyList<AgentAssetDefinition> GetCliDefined(AgentAssetKind assetKind)
        => CliDefined.Where(asset => asset.AssetKind == assetKind).ToList();

    /// <summary>
    /// Creates a skill asset sourced from the Aspire skills bundle.
    /// </summary>
    internal static AgentAssetDefinition CreateAspireSkillsBundle(
        string name,
        string description,
        IReadOnlyList<string>? installExcludedRelativePaths = null,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        return new(
            AgentAssetKind.Skill,
            name,
            description,
            AgentAssetSourceKind.AspireSkillsBundle,
            files: [],
            installExcludedRelativePaths: installExcludedRelativePaths ?? [],
            isDefault: true,
            hasInstallableFiles: true,
            applicableLanguages);
    }

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

    /// <summary>
    /// Gets whether this asset has the specified name.
    /// </summary>
    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal)
        => string.Equals(Name, name, comparison);

    /// <inheritdoc />
    public override string ToString() => Name;

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
/// Identifies where an agent asset's installable content is sourced from.
/// </summary>
internal enum AgentAssetSourceKind
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

    /// <summary>
    /// The asset is applied through detected environment actions.
    /// </summary>
    Action,
}
