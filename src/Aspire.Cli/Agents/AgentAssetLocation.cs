// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a file location or action target for an agent asset.
/// </summary>
[DebuggerDisplay("AssetKind = {AssetKind}, Id = {Id}, DisplayName = {DisplayName}, Description = {Description}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetLocation
{
    /// <summary>
    /// Standard <c>.agents/skills/</c> location supported by VS Code, GitHub Copilot, and OpenCode.
    /// </summary>
    public static readonly AgentAssetLocation Standard = new(
        AgentAssetKind.Skill,
        "standard",
        AgentCommandStrings.SkillLocation_StandardName,
        AgentCommandStrings.SkillLocation_StandardDescription,
        Path.Combine(".agents", "skills"),
        isDefault: true,
        includeUserLevel: true);

    /// <summary>
    /// Claude Code <c>.claude/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ClaudeCode = new(
        AgentAssetKind.Skill,
        "claudecode",
        AgentCommandStrings.SkillLocation_ClaudeCodeName,
        AgentCommandStrings.SkillLocation_ClaudeCodeDescription,
        Path.Combine(".claude", "skills"),
        isDefault: false,
        includeUserLevel: false);

    /// <summary>
    /// VS Code and GitHub Copilot <c>.github/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation GitHubSkills = new(
        AgentAssetKind.Skill,
        "github",
        AgentCommandStrings.SkillLocation_GitHubSkillsName,
        AgentCommandStrings.SkillLocation_GitHubSkillsDescription,
        Path.Combine(".github", "skills"),
        isDefault: false,
        includeUserLevel: false);

    /// <summary>
    /// OpenCode <c>.opencode/skill/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation OpenCode = new(
        AgentAssetKind.Skill,
        "opencode",
        AgentCommandStrings.SkillLocation_OpenCodeName,
        AgentCommandStrings.SkillLocation_OpenCodeDescription,
        Path.Combine(".opencode", "skill"),
        isDefault: false,
        includeUserLevel: false);

    /// <summary>
    /// The logical target representing every compatible detected agent environment.
    /// </summary>
    public static readonly AgentAssetLocation DetectedAgentEnvironments = new(
        AgentAssetKind.Mcp,
        "detected-agent-environments",
        AgentCommandStrings.InitCommand_ConfigureMcpServer,
        AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments,
        relativeAssetDirectory: null,
        isDefault: true,
        includeUserLevel: false);

    private AgentAssetLocation(
        AgentAssetKind assetKind,
        string id,
        string displayName,
        string description,
        string? relativeAssetDirectory,
        bool isDefault,
        bool includeUserLevel)
    {
        AssetKind = assetKind;
        Id = id;
        DisplayName = displayName;
        Description = description;
        RelativeAssetDirectory = relativeAssetDirectory;
        IsDefault = isDefault;
        IncludeUserLevel = includeUserLevel;
    }

    /// <summary>
    /// Gets the kind of agent asset installed at this location.
    /// </summary>
    public AgentAssetKind AssetKind { get; }

    /// <summary>
    /// Gets the non-localized identifier for this location or target.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the display name for this location or target.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the description shown alongside the name in prompts.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the relative asset directory, or <see langword="null"/> for an action target.
    /// </summary>
    public string? RelativeAssetDirectory { get; }

    /// <summary>
    /// Gets whether this location is backed by a file-system directory.
    /// </summary>
    public bool IsFileLocation => RelativeAssetDirectory is not null;

    /// <summary>
    /// Gets whether this location should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets whether this location also installs skill files at the user level.
    /// </summary>
    public bool IncludeUserLevel { get; }

    /// <summary>
    /// Gets all available agent asset locations and action targets.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> All { get; } =
        [Standard, ClaudeCode, GitHubSkills, OpenCode, DetectedAgentEnvironments];

    /// <summary>
    /// Gets the locations or targets available for the specified asset kind.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> GetLocations(AgentAssetKind assetKind)
        => All.Where(location => location.AssetKind == assetKind).ToList();

    /// <inheritdoc />
    public override string ToString() => Id;
}
