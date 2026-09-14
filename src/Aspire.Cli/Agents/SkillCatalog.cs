// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Combines sourced skills with the CLI-defined skills that take precedence over them.
/// </summary>
internal sealed class SkillCatalog : IAgentAssetCatalog
{
    private readonly IAgentAssetSource _assetSource;

    public static readonly AgentAssetDefinition PlaywrightCli = new(
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        files: [],
        installExcludedRelativePaths: [],
        isDefault: false);

    public static readonly AgentAssetDefinition DotnetInspect = new(
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        files: [new AgentAssetFile("SKILL.md", CommonAgentApplicators.DotnetInspectSkillFileContent)],
        installExcludedRelativePaths: [],
        isDefault: false,
        applicableLanguages: [KnownLanguageId.CSharp]);

    public static IReadOnlyList<AgentAssetDefinition> CliDefined { get; } = [PlaywrightCli, DotnetInspect];

    /// <summary>
    /// Standard <c>.agents/skills/</c> location supported by VS Code, GitHub Copilot, and OpenCode.
    /// </summary>
    public static readonly AgentAssetLocation Standard = new(
        "standard",
        AgentCommandStrings.SkillLocation_StandardName,
        AgentCommandStrings.SkillLocation_StandardDescription,
        Path.Combine(".agents", "skills"),
        isDefault: true,
        scopes: AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User);

    /// <summary>
    /// Claude Code <c>.claude/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ClaudeCode = new(
        "claudecode",
        AgentCommandStrings.SkillLocation_ClaudeCodeName,
        AgentCommandStrings.SkillLocation_ClaudeCodeDescription,
        Path.Combine(".claude", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace);

    /// <summary>
    /// VS Code and GitHub Copilot <c>.github/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation GitHubSkills = new(
        "github",
        AgentCommandStrings.SkillLocation_GitHubSkillsName,
        AgentCommandStrings.SkillLocation_GitHubSkillsDescription,
        Path.Combine(".github", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace);

    /// <summary>
    /// OpenCode <c>.opencode/skill/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation OpenCode = new(
        "opencode",
        AgentCommandStrings.SkillLocation_OpenCodeName,
        AgentCommandStrings.SkillLocation_OpenCodeDescription,
        Path.Combine(".opencode", "skill"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace);

    public static IReadOnlyList<AgentAssetLocation> KnownLocations { get; } =
        [Standard, ClaudeCode, GitHubSkills, OpenCode];

    public SkillCatalog(IAgentAssetSource assetSource)
    {
        _assetSource = assetSource;
    }

    public string Name => "skills";

    public IReadOnlyList<AgentClient> SupportedClients => AgentClient.All;

    public IReadOnlyList<AgentAssetLocation> Locations => KnownLocations;

    public AgentAssetFileInstaller FileInstaller => AgentAssetFileInstaller.Additive;

    public async Task<AgentAssetCatalogResult> ResolveAsync(string? requestedAssets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedNames = GetRequestedNames(requestedAssets);
        if (string.Equals(requestedAssets, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase) ||
            (requestedNames.Length > 0 && requestedNames.All(IsCliDefinedSkillName)))
        {
            return new(CliDefined, DiagnosticMessage: null, IsFailure: false);
        }

        var result = await _assetSource.GetAssetsAsync(cancellationToken);
        if (result.IsAvailable)
        {
            return new(
                [.. result.Assets.Where(static asset => !IsCliDefinedSkillName(asset.Name)), .. CliDefined],
                DiagnosticMessage: null,
                IsFailure: false);
        }

        // An unavailable source must not prevent use of built-in skills. Only an explicit
        // source-only name needs the acquisition diagnostic before choice validation fails.
        var diagnosticMessage = requestedNames.Any(static name => !IsCliDefinedSkillName(name))
            ? result.Message
            : null;
        return new(CliDefined, diagnosticMessage, IsFailure: false);
    }

    public IEnumerable<AgentAssetInstallTarget> ResolveInstallTargets(
        AgentAssetLocation location,
        DirectoryInfo workspaceDirectory,
        DirectoryInfo homeDirectory,
        IEnvironment environment)
        => location.ResolveInstallTargets(workspaceDirectory, homeDirectory);

    private static bool IsCliDefinedSkillName(string name)
        => CliDefined.Any(skill => skill.HasName(name, StringComparison.OrdinalIgnoreCase));

    private static string[] GetRequestedNames(string? requestedAssets)
    {
        if (string.IsNullOrWhiteSpace(requestedAssets) ||
            string.Equals(requestedAssets, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedAssets, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // --skills accepts comma-separated names, for example "playwright-cli, dotnet-inspect".
        // Ignore empty entries and surrounding whitespace, as the choice parser does.
        return requestedAssets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
