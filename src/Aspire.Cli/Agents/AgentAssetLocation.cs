// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a location where agent asset files can be installed.
/// </summary>
[DebuggerDisplay("Id = {Id}, DisplayName = {DisplayName}, Description = {Description}, IsDefault = {IsDefault}, AssetType = {AssetType}")]
internal sealed class AgentAssetLocation
{
    /// <summary>
    /// Standard <c>.agents/skills/</c> location supported by VS Code, GitHub Copilot, and OpenCode.
    /// </summary>
    public static readonly AgentAssetLocation StandardSkills = new(
        "standard",
        AgentCommandStrings.SkillLocation_StandardName,
        AgentCommandStrings.SkillLocation_StandardDescription,
        Path.Combine(".agents", "skills"),
        isDefault: true,
        installLocation: InstallLocation.Workspace | InstallLocation.User,
        assetType: AgentAssetKind.Skill);

    /// <summary>
    /// Claude Code <c>.claude/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ClaudeCode = new(
        "claudecode",
        AgentCommandStrings.SkillLocation_ClaudeCodeName,
        AgentCommandStrings.SkillLocation_ClaudeCodeDescription,
        Path.Combine(".claude", "skills"),
        isDefault: false,
        installLocation: InstallLocation.Workspace,
        assetType: AgentAssetKind.Skill);

    /// <summary>
    /// VS Code / GitHub Copilot <c>.github/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation GitHubSkills = new(
        "github",
        AgentCommandStrings.SkillLocation_GitHubSkillsName,
        AgentCommandStrings.SkillLocation_GitHubSkillsDescription,
        Path.Combine(".github", "skills"),
        isDefault: false,
        installLocation: InstallLocation.Workspace,
        assetType: AgentAssetKind.Skill);

    /// <summary>
    /// OpenCode <c>.opencode/skill/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation OpenCode = new(
        "opencode",
        AgentCommandStrings.SkillLocation_OpenCodeName,
        AgentCommandStrings.SkillLocation_OpenCodeDescription,
        Path.Combine(".opencode", "skill"),
        isDefault: false,
        installLocation: InstallLocation.Workspace,
        assetType: AgentAssetKind.Skill);

    /// <summary>
    /// Standard <c>.github/extensions/</c> location supported by GitHub Copilot
    /// </summary>
    public static readonly AgentAssetLocation StandardExtensions = new(
        "standard-extensions",
        AgentCommandStrings.ExtensionLocation_StandardName,
        AgentCommandStrings.ExtensionLocation_StandardDescription,
        Path.Combine(".github", "extensions"),
        userRelativeAssetDirectory: Path.Combine(".copilot", "extensions"),
        isDefault: true,
        installLocation: InstallLocation.Workspace | InstallLocation.User,
        assetType: AgentAssetKind.Extension);

    private AgentAssetLocation(string id, string displayName, string description, string relativeAssetDirectory, bool isDefault, InstallLocation installLocation, AgentAssetKind assetType, string? userRelativeAssetDirectory = null)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        RelativeAgentAssetDirectory = relativeAssetDirectory;
        UserRelativeAgentAssetDirectory = userRelativeAssetDirectory ?? relativeAssetDirectory;
        IsDefault = isDefault;
        InstallLocation = installLocation;
        AssetType = assetType;
    }

    /// <summary>
    /// Gets the non-localized identifier for this location, used for CLI option matching.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the display name for this location.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the description shown alongside the name in prompts.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the relative agent asset directory path (e.g., ".agents/skills").
    /// </summary>
    public string RelativeAgentAssetDirectory { get; }

    /// <summary>
    /// Gets the relative user-level agent asset directory path.
    /// </summary>
    public string UserRelativeAgentAssetDirectory { get; }

    /// <summary>
    /// Gets whether this location should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets whether this location also installs asset files at the user level (<c>~/</c>).
    /// </summary>
    public InstallLocation InstallLocation { get; }

    /// <summary>
    /// Gets the type of asset being installed
    /// </summary>
    public AgentAssetKind AssetType { get; }

    /// <summary>
    /// Gets all available asset locations.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> All { get; } = [StandardSkills, ClaudeCode, GitHubSkills, OpenCode, StandardExtensions];

    /// <inheritdoc />
    public override string ToString() => Id;
}

/// <summary>
/// When installing agent asset files, indicates where the files should be installed.
/// </summary>
[Flags]
internal enum InstallLocation
{
    /// <summary>
    /// Install the asset files at the workspace level.
    /// </summary>
    Workspace = 1,

    /// <summary>
    /// Install the asset files at the user level.
    /// </summary>
    User = 2
}
