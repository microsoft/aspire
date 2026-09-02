// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

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
        isDefault: false,
        externalInstallerId: AgentExternalInstallerId.PlaywrightCli);

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
